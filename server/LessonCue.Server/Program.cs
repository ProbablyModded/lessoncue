using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using LessonCue.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var dataPath = Environment.GetEnvironmentVariable("LESSONCUE_DATA_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine(UpdateService.InstalledVersion());
    return;
}
if (args.Contains("--youtube-runtime-update", StringComparer.Ordinal))
{
    Environment.ExitCode = await YouTubeRuntimeUpdateCommand.RunAsync(dataPath);
    return;
}
if (DatabaseVerificationCommand.TryGetPath(args, out var databaseToVerify))
{
    Environment.ExitCode = await DatabaseVerificationCommand.RunAsync(databaseToVerify);
    return;
}
if (AdminRecoveryCommand.IsRequested(args))
{
    Environment.ExitCode = await AdminRecoveryCommand.RunAsync(args, dataPath);
    return;
}
var configPath = Path.Combine(dataPath, "config");
var databasePath = Path.Combine(dataPath, "database");
var mediaPath = Path.Combine(dataPath, "media", "originals");
Directory.CreateDirectory(configPath);
Directory.CreateDirectory(databasePath);
Directory.CreateDirectory(mediaPath);
var keyPath = Path.Combine(configPath, "keys");
Directory.CreateDirectory(keyPath);
builder.Configuration.AddJsonFile(Path.Combine(configPath, "appsettings.json"), optional: true, reloadOnChange: true);

var troubleshootingLog = new TroubleshootingLog(dataPath);
builder.Logging.AddProvider(troubleshootingLog);

var port = HttpPortConfiguration.Resolve(dataPath);
var useDefaultHttpBinding = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 20L * 1024 * 1024 * 1024;
    // Avahi publishes both address families when IPv6 is enabled. Binding the
    // native appliance to 0.0.0.0 only advertised an IPv6 endpoint that
    // Kestrel could never serve, which made some Android TV NSD clients stop
    // before they tried the working IPv4 address. ListenAnyIP creates the
    // dual-stack listener for the default appliance path; deployments that set
    // ASPNETCORE_URLS retain control of their explicit binding.
    if (useDefaultHttpBinding) options.ListenAnyIP(port);
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 20L * 1024 * 1024 * 1024);

builder.Services.AddDbContext<LessonCueDb>(options =>
    options.UseSqlite(
        $"Data Source={Path.Combine(databasePath, "lessoncue.db")}",
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
    .SetApplicationName("LessonCue");
builder.Services.AddScoped<ManifestService>();
builder.Services.AddSingleton(troubleshootingLog);
builder.Services.AddSingleton(new PairingCodeService(dataPath, builder.Configuration["LessonCue:PairingPin"]));
builder.Services.AddSingleton(new BackupService(dataPath));
// Optional, and inert until an administrator turns it on.
builder.Services.AddScoped<LessonCue.Server.Activities.ReservedGameCodePool>();
builder.Services.AddHttpClient<LessonCue.Server.Shortener.ShlinkClient>(client => client.Timeout = TimeSpan.FromSeconds(12));
builder.Services.AddSingleton<LessonCue.Server.Shortener.ReservedCodeProvisioner>();
builder.Services.AddHttpClient("shortener-probe")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddSingleton(provider => new LessonCue.Server.Shortener.ShortenerService(
    provider.GetRequiredService<IServiceScopeFactory>(),
    provider.GetRequiredService<LessonCue.Server.Shortener.ShlinkClient>(),
    provider.GetRequiredService<LessonCue.Server.Shortener.ReservedCodeProvisioner>(),
    provider.GetRequiredService<IHttpClientFactory>().CreateClient("shortener-probe"),
    dataPath));
builder.Services.AddHostedService<LessonCue.Server.Shortener.ShortenerHealthService>();
builder.Services.AddHttpClient("backup-offsite", client =>
    client.Timeout = TimeSpan.FromHours(6))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(20)
    });
builder.Services.AddHttpClient("backup-google-oauth", client =>
    client.Timeout = TimeSpan.FromSeconds(25))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });
builder.Services.AddHttpClient("migration-transfer", client =>
    {
        client.Timeout = TimeSpan.FromHours(6);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(20)
    });
builder.Services.AddSingleton<MigrationTransferService>();
builder.Services.AddSingleton(services => new BackupPolicyService(
    dataPath,
    services.GetRequiredService<IServiceScopeFactory>(),
    services.GetRequiredService<BackupService>(),
    services.GetRequiredService<IDataProtectionProvider>(),
    services.GetRequiredService<IHttpClientFactory>(),
    services.GetRequiredService<ILogger<BackupPolicyService>>()));
builder.Services.AddHostedService(services =>
    services.GetRequiredService<BackupPolicyService>());
builder.Services.AddSingleton(new MediaStoragePaths(dataPath));
builder.Services.AddSingleton(services => new TroubleshootingReportBuilder(
    services.GetRequiredService<TroubleshootingLog>(),
    services.GetRequiredService<MediaStoragePaths>(),
    services.GetRequiredService<YouTubeRuntimeUpdateService>(),
    services.GetRequiredService<LessonCue.Server.Shortener.ShortenerService>(),
    services.GetRequiredService<BackupPolicyService>()));
builder.Services.AddSingleton(new StorageService(dataPath));
builder.Services.AddSingleton(services => new YouTubeRuntimeUpdateService(
    dataPath, services.GetRequiredService<ILogger<YouTubeRuntimeUpdateService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<YouTubeRuntimeUpdateService>());
builder.Services.AddSingleton<UploadSessionService>();
builder.Services.AddHostedService(services => services.GetRequiredService<UploadSessionService>());
builder.Services.AddSingleton<HardwareAccelerationService>();
builder.Services.AddHostedService(services => services.GetRequiredService<HardwareAccelerationService>());
builder.Services.AddSingleton(services => new HttpPortService(dataPath, port, services.GetRequiredService<ILogger<HttpPortService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<HttpPortService>());
builder.Services.AddSingleton(services => new LocalAddressService(dataPath, port, services.GetRequiredService<ILogger<LocalAddressService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<LocalAddressService>());
builder.Services.AddHostedService<MediaProcessingService>();
builder.Services.AddHostedService<AdaptiveTranscodeService>();
builder.Services.AddHostedService<PresentationConversionService>();
builder.Services.AddHostedService<YouTubeImportService>();
builder.Services.AddHostedService<MediaRetentionService>();
builder.Services.AddHostedService<RecurringLessonGeneratorService>();
builder.Services.AddHostedService<AudienceRetentionService>();
builder.Services.AddSingleton(services => new ScreenDiagnosticCleanupService(services.GetRequiredService<IServiceScopeFactory>(),
    dataPath, services.GetRequiredService<ILogger<ScreenDiagnosticCleanupService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<ScreenDiagnosticCleanupService>());
builder.Services.AddHttpClient("updates", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LessonCue-Server/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
});
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(services => services.GetRequiredService<UpdateService>());
builder.Services.AddSingleton(services => new SupportBundleBuilder(
    services.GetRequiredService<StorageService>(),
    services.GetRequiredService<BackupPolicyService>(),
    services.GetRequiredService<UpdateService>(),
    services.GetRequiredService<YouTubeRuntimeUpdateService>(),
    services.GetRequiredService<LessonCue.Server.Shortener.ShortenerService>(),
    services.GetRequiredService<ILogger<SupportBundleBuilder>>()));
builder.Services.AddHttpClient("cloudflare-tunnel", client => client.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddHttpClient("account-email", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("deepseek-review", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LessonCue-Troubleshooting-Review/1.0");
});
builder.Services.AddHttpClient("presentation-import", client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LessonCue-Server/1.0");
});
builder.Services.AddHttpClient("signage-widgets", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LessonCue-Signage/1.0 (+https://github.com/ProbablyModded/lessoncue)");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<SignageWidgetService>();
builder.Services.AddSingleton(services => new SignageCredentialStore(dataPath,
    services.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddHostedService(services => services.GetRequiredService<SignageWidgetService>());
builder.Services.AddSingleton(services => new LiveStreamRelayService(dataPath,
    services.GetRequiredService<ILogger<LiveStreamRelayService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<LiveStreamRelayService>());
builder.Services.AddSingleton(services => new AccountEmailService(dataPath,
    services.GetRequiredService<IDataProtectionProvider>(), services.GetRequiredService<IHttpClientFactory>(),
    services.GetRequiredService<ILogger<AccountEmailService>>()));
builder.Services.AddSingleton(services => new TroubleshootingEmailService(
    services.GetRequiredService<IServiceScopeFactory>(),
    services.GetRequiredService<AccountEmailService>(),
    services.GetRequiredService<TroubleshootingReportBuilder>(),
    services.GetRequiredService<ILogger<TroubleshootingEmailService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<TroubleshootingEmailService>());
builder.Services.AddSingleton(services => new TroubleshootingReviewService(
    dataPath,
    services.GetRequiredService<IServiceScopeFactory>(),
    services.GetRequiredService<TroubleshootingReportBuilder>(),
    services.GetRequiredService<AccountEmailService>(),
    services.GetRequiredService<IHttpClientFactory>(),
    services.GetRequiredService<ILogger<TroubleshootingReviewService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<TroubleshootingReviewService>());
builder.Services.AddSingleton(services => new TroubleshootingReportPullService(
    services.GetRequiredService<IServiceScopeFactory>(),
    services.GetRequiredService<TroubleshootingReportBuilder>()));
builder.Services.AddSingleton(services => new CloudflareTunnelService(dataPath,
    services.GetRequiredService<HttpPortService>(), services.GetRequiredService<IHttpClientFactory>(),
    services.GetRequiredService<ILogger<CloudflareTunnelService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<CloudflareTunnelService>());
builder.Services.AddSingleton(new ActivityAvailabilityService(dataPath));
builder.Services.AddSingleton(services => new ActivityJoinAddressService(dataPath,
    services.GetRequiredService<LocalAddressService>(),
    services.GetRequiredService<CloudflareTunnelService>(),
    services.GetRequiredService<HttpPortService>(),
    services.GetRequiredService<LessonCue.Server.Shortener.ShortenerService>()));
builder.Services.AddSingleton<IPasswordHasher<PairingAttempt>, PasswordHasher<PairingAttempt>>();
builder.Services.AddSingleton<IPasswordHasher<AdminAccount>, PasswordHasher<AdminAccount>>();
builder.Services.AddSingleton<IPasswordHasher<Organization>, PasswordHasher<Organization>>();
builder.Services.AddSingleton<AdminMfaService>();
builder.Services.AddSingleton<ControllerSessionService>();
builder.Services.AddSingleton<LessonCue.Server.Activities.IActivityRandomSource, LessonCue.Server.Activities.CryptoRandomSource>();
builder.Services.AddScoped<LessonCue.Server.Activities.ActivityService>();
builder.Services.AddScoped<LessonCue.Server.Activities.ActivitySessionService>();
builder.Services.AddHostedService<LessonCue.Server.Activities.ActivityAutoPilotService>();
builder.Services.AddSignalR();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "lessoncue.admin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async context =>
    {
        var id = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(id, out var accountId)) { context.RejectPrincipal(); return; }
        var sessionVersionValue = context.Principal?.FindFirst("session_version")?.Value;
        var sessionVersion = int.TryParse(sessionVersionValue, out var parsedVersion) ? parsedVersion : 1;
        var db = context.HttpContext.RequestServices.GetRequiredService<LessonCueDb>();
        if (!await db.AdminAccounts.AsNoTracking().AnyAsync(x => x.Id == accountId && !x.Disabled &&
            !x.PendingApproval && !x.PendingSetup && x.SessionVersion == sessionVersion,
            context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
});
builder.Services.AddAuthorization(LessonCuePermissions.AddPolicies);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("pairing", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0
        }));
    options.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0
        }));
    options.AddPolicy("audience", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("audience-submit", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            // A classroom commonly shares one NAT address. Keep abuse bounded
            // without preventing a room of participants from submitting at once.
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("activity-public", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            // Public display snapshots and participant reconnect fallbacks are
            // shared by a classroom NAT. SignalR carries normal updates, but
            // the fallback must still accommodate a large local room.
            PermitLimit = 1200,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("activity-submit", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("migration", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        }));
    options.AddPolicy("upload-session", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        }));
    options.AddPolicy("upload-chunk", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("media-processing", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            // These requests can launch downloads, office conversion, or video
            // transcodes. Bound accidental loops without blocking normal editing.
            PermitLimit = 20,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        }));
    options.AddPolicy("remote-preview", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            // Weather and calendar previews make outbound requests from the server.
            PermitLimit = 60,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        }));
    options.AddPolicy("report-pull", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
forwardedHeaders.KnownProxies.Add(System.Net.IPAddress.Loopback);
forwardedHeaders.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
app.UseForwardedHeaders(forwardedHeaders);
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self' ws: wss:; frame-src 'self' https: http:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    var mediaPathValue = context.Request.Path.Value ?? "";
    if (HttpMethods.IsGet(context.Request.Method) &&
        mediaPathValue.StartsWith("/api/v1/media/", StringComparison.Ordinal) &&
        (mediaPathValue.EndsWith("/file", StringComparison.Ordinal) ||
         mediaPathValue.EndsWith("/playback", StringComparison.Ordinal) ||
         mediaPathValue.Contains("/transcodes/", StringComparison.Ordinal) ||
         mediaPathValue.EndsWith("/thumbnail", StringComparison.Ordinal) ||
         mediaPathValue.EndsWith("/filmstrip", StringComparison.Ordinal) ||
         mediaPathValue.EndsWith("/waveform", StringComparison.Ordinal)))
    {
        // Display media is intentionally reachable on the trusted TV network,
        // but shared proxies must not retain it as public content.
        context.Response.Headers.CacheControl = "private, max-age=86400";
        context.Response.Headers["X-Robots-Tag"] = "noindex, noarchive";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var unsafeMethod = context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";
    var restore = context.RequestServices.GetRequiredService<BackupService>();
    if (unsafeMethod && restore.IsRestoring && !context.Request.Path.StartsWithSegments("/api/v1/backups/restore"))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "LessonCue is restoring a backup. Try again after it finishes." });
        return;
    }
    if (unsafeMethod && context.Request.Path.StartsWithSegments("/api/v1") && context.User.Identity?.IsAuthenticated == true)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
    }
    if (context.User.HasClaim("must_change_password", "true") &&
        context.Request.Path.StartsWithSegments("/api/v1") &&
        !context.Request.Path.StartsWithSegments("/api/v1/auth/session") &&
        !context.Request.Path.StartsWithSegments("/api/v1/auth/password/change-required") &&
        !context.Request.Path.StartsWithSegments("/api/v1/auth/logout"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Change the temporary password before using LessonCue." });
        return;
    }
    await next();
});
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    Exception? failure = null;
    try
    {
        await next();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        failure = ex;
        app.Logger.LogError(ex,
            "HTTP request failed. {Method} {Path} returned an exception. Trace {TraceId}; user {User}",
            context.Request.Method, context.Request.Path, context.TraceIdentifier,
            context.User.Identity?.Name ?? "anonymous");
        throw;
    }
    finally
    {
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (failure is null && context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            app.Logger.LogWarning(
                "HTTP request returned a failure status. {Method} {Path} returned {StatusCode} in {ElapsedMs:0.0} ms. Trace {TraceId}; user {User}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsedMs,
                context.TraceIdentifier, context.User.Identity?.Name ?? "anonymous");
        }
    }
});

var serverId = ServerIdentity.LoadOrCreate(dataPath);
try
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
    await db.Database.EnsureCreatedAsync();
    await DatabaseUpgrade.ApplyAsync(db);
    await SeedData.RunAsync(db);
}
catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
{
    app.Logger.LogCritical(ex, "LessonCue database initialization failed");
    await app.DisposeAsync();
    await RecoveryModeApp.RunAsync(args, port, dataPath, serverId.ToString(), ex);
    return;
}

if (args.Contains("--seed-animal-activity-pack", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
    var result = await LessonCue.Server.Activities.AnimalActivityPack.EnsureAsync(db);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result,
        LessonCue.Server.Activities.ActivityJsonDefaults.Options));
    await app.DisposeAsync();
    return;
}

var serverName = Environment.GetEnvironmentVariable("LESSONCUE_SERVER_NAME")
    ?? builder.Configuration["LessonCue:ServerName"] ?? "LessonCue";
var pairingCodes = app.Services.GetRequiredService<PairingCodeService>();
var localAddress = app.Services.GetRequiredService<LocalAddressService>();
var httpPort = app.Services.GetRequiredService<HttpPortService>();

app.MapGet("/.well-known/lessoncue", () => new
{
    product = "LessonCue",
    serverId,
    serverName,
    localAddress = httpPort.Status.Address,
    apiVersion = 1,
    pairingEnabled = true
});

app.MapGet("/health/live", () => Results.Ok(new { status = "alive", serverId }));

Func<LessonCueDb, CancellationToken, Task<IResult>> readiness = async (db, ct) =>
{
    var report = await ServerReadiness.CheckAsync(db, dataPath, serverId, ct);
    return Results.Json(
        report,
        statusCode: report.Status == "healthy"
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
};
app.MapGet("/health/ready", readiness);
app.MapGet("/health", readiness);

app.MapGet(TroubleshootingReportPullService.EndpointPath, async (
    HttpRequest request,
    HttpResponse response,
    TroubleshootingReportPullService pull,
    CancellationToken ct) =>
{
    if (!TroubleshootingReportPullService.IsConfigured)
        return Results.NotFound();
    if (!TroubleshootingReportPullService.Authorize(request))
    {
        response.Headers.WWWAuthenticate = "Bearer";
        return Results.Unauthorized();
    }

    try
    {
        var result = await pull.GetAsync(request.Headers.IfNoneMatch.ToString(), ct);
        response.Headers.ETag = result.ETag;
        response.Headers["Last-Modified"] = result.GeneratedAt.ToUniversalTime().ToString("R");
        response.Headers.CacheControl = "no-store";
        response.Headers.Vary = "Authorization";
        response.Headers["X-Robots-Tag"] = "noindex, noarchive";
        response.Headers["X-LessonCue-Report-Generated-At"] = result.GeneratedAt.ToUniversalTime().ToString("O");
        if (result.NotModified) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(result.Content!, "application/json");
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Live troubleshooting report generation failed");
        return Results.Problem(
            "The live troubleshooting report is temporarily unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "LC.REPORT.GENERATION.UNAVAILABLE"
            });
    }
}).AllowAnonymous().RequireRateLimiting("report-pull");

var api = app.MapGroup("/api/v1");

api.MapGet("/display-capabilities", (string? platform) =>
    Results.Ok(string.IsNullOrWhiteSpace(platform)
        ? new[] { DisplayCapabilities.For("web-player"), DisplayCapabilities.For("android-tv") }
        : [DisplayCapabilities.For(platform)]));

api.MapGet("/migration/export", (
    HttpRequest request,
    MigrationTransferService transfers) =>
{
    var authorization = request.Headers.Authorization.ToString();
    var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authorization["Bearer ".Length..]
        : null;
    var source = transfers.Consume(token);
    return source is null
        ? Results.NotFound()
        : Results.File(
            source.Path,
            "application/vnd.lessoncue.backup",
            source.FileName,
            enableRangeProcessing: false);
}).RequireRateLimiting("migration");

app.MapLessonCueAdmin(mediaPath, dataPath, serverId, serverName);
app.MapAudienceInteraction();
LessonCue.Server.Activities.ActivityApi.Map(app);

api.MapGet("/media/{mediaId:guid}/file", async (Guid mediaId, LessonCueDb db, CancellationToken ct) =>
{
    var media = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mediaId, ct);
    if (media is null || media.SourceKind == "link") return Results.NotFound();
    var path = ContainedPath.ResolveExistingFile(mediaPath, media.RelativePath);
    if (path is null) return Results.NotFound();
    return Results.File(path, media.ContentType, media.FileName, enableRangeProcessing: true,
        entityTag: media.Sha256 is null ? null : new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{media.Sha256}\""));
});

api.MapGet("/media/{mediaId:guid}/playback", async (Guid mediaId, LessonCueDb db,
    MediaStoragePaths paths, CancellationToken ct) =>
{
    var media = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mediaId, ct);
    if (media is null || media.SourceKind == "link") return Results.NotFound();
    var compatible = media.CompatibilityStatus == "ready" && !string.IsNullOrWhiteSpace(media.CompatibilityPath);
    var root = compatible ? paths.Compatibility : paths.Originals;
    var relative = compatible ? media.CompatibilityPath! : media.RelativePath;
    var path = ContainedPath.ResolveExistingFile(root, relative);
    if (path is null) return Results.NotFound();
    var contentType = compatible ? "video/mp4" : media.ContentType;
    var fileName = compatible ? Path.GetFileNameWithoutExtension(media.FileName) + ".mp4" : media.FileName;
    var hash = compatible ? media.CompatibilitySha256 : media.Sha256;
    return Results.File(path, contentType, fileName, enableRangeProcessing: true,
        entityTag: hash is null ? null : new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{hash}\""));
});

api.MapGet("/media/{mediaId:guid}/transcodes/{profile}", async (Guid mediaId, string profile,
    LessonCueDb db, MediaStoragePaths paths, CancellationToken ct) =>
{
    var variant = await db.MediaTranscodeVariants.AsNoTracking().SingleOrDefaultAsync(x =>
        x.MediaAssetId == mediaId && x.Profile == profile && x.Status == "ready", ct);
    if (variant?.RelativePath is null) return Results.NotFound();
    var path = ContainedPath.ResolveExistingFile(paths.Transcodes, variant.RelativePath);
    if (path is null) return Results.NotFound();
    return Results.File(path, "video/mp4", enableRangeProcessing: true,
        entityTag: variant.Sha256 is null ? null : new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{variant.Sha256}\""));
});

api.MapGet("/media/{mediaId:guid}/thumbnail", async (Guid mediaId, LessonCueDb db, CancellationToken ct) =>
{
    var media = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mediaId, ct);
    if (media?.ThumbnailPath is null) return Results.NotFound();
    var thumbnails = Path.Combine(dataPath, "media", "thumbnails");
    var path = ContainedPath.ResolveExistingFile(thumbnails, media.ThumbnailPath);
    if (path is null) return Results.NotFound();
    return Results.File(path, "image/jpeg", enableRangeProcessing: true);
});

api.MapGet("/media/{mediaId:guid}/filmstrip", async (Guid mediaId, LessonCueDb db,
    MediaStoragePaths paths, CancellationToken ct) =>
{
    var media = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mediaId, ct);
    return DerivativeFile(media?.FilmstripPath, paths.Thumbnails, "image/jpeg");
});

api.MapGet("/media/{mediaId:guid}/waveform", async (Guid mediaId, LessonCueDb db,
    MediaStoragePaths paths, CancellationToken ct) =>
{
    var media = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mediaId, ct);
    return DerivativeFile(media?.WaveformPath, paths.Thumbnails, "image/png");
});

api.MapGet("/signage/{signageId:guid}/zones/{zoneId}/stream/{fileName}", async (
    Guid signageId, string zoneId, string fileName, LessonCueDb db, LiveStreamRelayService streams,
    CancellationToken ct) =>
{
    var signage = await db.SignagePlaylists.AsNoTracking().SingleOrDefaultAsync(x => x.Id == signageId && x.Enabled, ct);
    SignageZoneInput? zone = null;
    if (signage is not null)
    {
        var zones = SignageLayout.ParseZones(signage.ZonesJson);
        if (signage.LayoutId is { } layoutId)
        {
            var layout = await db.SignageLayouts.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == layoutId && value.PublishedVersion > 0, ct);
            if (layout is not null) zones = SignageLayout.ParseZones(layout.PublishedZonesJson);
        }
        zone = zones.FirstOrDefault(value => value.Id.Equals(zoneId, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            var publishedLayouts = await db.SignageLayouts.AsNoTracking().Where(value => value.PublishedVersion > 0)
                .Select(value => value.PublishedZonesJson).ToListAsync(ct);
            zone = publishedLayouts.SelectMany(SignageLayout.ParseZones)
                .FirstOrDefault(value => value.Id.Equals(zoneId, StringComparison.OrdinalIgnoreCase));
        }
    }
    if (zone?.Type is not ("stream" or "presentation") ||
        !SignageLayout.TryStreamUrl(zone.SourceUrl, out var sourceUrl))
        return Results.NotFound();
    if (fileName.Equals("index.m3u8", StringComparison.OrdinalIgnoreCase))
    {
        var prepared = await streams.PreparePlaylistAsync(signageId, zone.Id, sourceUrl, ct);
        return prepared.Path is null
            ? Results.Problem(prepared.Error, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.File(prepared.Path, "application/vnd.apple.mpegurl",
                enableRangeProcessing: false, lastModified: DateTimeOffset.UtcNow);
    }
    var segment = streams.ResolveSegment(signageId, zone.Id, fileName);
    return segment is null ? Results.NotFound() : Results.File(segment, "video/mp2t", enableRangeProcessing: false);
}).DisableAntiforgery();

api.MapGet("/signage/{signageId:guid}/zones/{zoneId}/html", async (
    Guid signageId, string zoneId, LessonCueDb db, HttpContext context, CancellationToken ct) =>
{
    var signage = await db.SignagePlaylists.AsNoTracking().SingleOrDefaultAsync(
        value => value.Id == signageId && value.Enabled, ct);
    if (signage is null) return Results.NotFound();
    var zones = SignageLayout.ParseZones(signage.ZonesJson);
    if (signage.LayoutId is { } layoutId)
    {
        var layout = await db.SignageLayouts.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == layoutId && value.PublishedVersion > 0, ct);
        if (layout is not null) zones = SignageLayout.ParseZones(layout.PublishedZonesJson);
    }
    var zone = zones.FirstOrDefault(value => value.Id.Equals(zoneId, StringComparison.OrdinalIgnoreCase));
    if (zone is null)
    {
        var publishedLayouts = await db.SignageLayouts.AsNoTracking().Where(value => value.PublishedVersion > 0)
            .Select(value => value.PublishedZonesJson).ToListAsync(ct);
        zone = publishedLayouts.SelectMany(SignageLayout.ParseZones)
            .FirstOrDefault(value => value.Id.Equals(zoneId, StringComparison.OrdinalIgnoreCase));
    }
    if (zone?.Type != "customHtml" || string.IsNullOrWhiteSpace(zone.Content)) return Results.NotFound();
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'none'; img-src data: blob: https: http:; media-src blob: https: http:; " +
        "style-src 'unsafe-inline' https: http:; script-src 'unsafe-inline' 'unsafe-eval' https: http:; " +
        "connect-src https: http: ws: wss:; font-src data: https: http:; frame-src https: http:";
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(zone.Content, "text/html; charset=utf-8");
}).DisableAntiforgery();

api.MapPost("/pairing/request", async (PairingRequestInput input, LessonCueDb db,
    IPasswordHasher<PairingAttempt> hasher, CancellationToken ct) =>
{
    var attempt = new PairingAttempt
    {
        DeviceName = input.DeviceName.Trim(),
        Platform = input.Platform,
        AppVersion = input.AppVersion,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
    };
    attempt.PinHash = hasher.HashPassword(attempt, pairingCodes.Current);
    db.PairingAttempts.Add(attempt);
    // Do not put a pending pairing identifier in a durable log. The request id and
    // PIN together are credentials until the attempt is completed or expires.
    db.AuditEvents.Add(new AuditEvent { Actor = input.DeviceName, Action = "screen.pair.request", Object = "pending" });
    await db.SaveChangesAsync(ct);
    return Results.Accepted(value: new { requestId = attempt.Id, attempt.ExpiresAt });
}).RequireRateLimiting("pairing");

api.MapPost("/pairing/confirm", async (PairingConfirmInput input, LessonCueDb db,
    IPasswordHasher<PairingAttempt> hasher, CancellationToken ct) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var now = DateTimeOffset.UtcNow;
    var attempt = await db.PairingAttempts.AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == input.RequestId, ct);
    if (attempt is null || attempt.Completed || attempt.ExpiresAt <= now || attempt.FailedAttempts >= 5)
        return Results.BadRequest(new { error = "Pairing request expired or locked." });
    var result = hasher.VerifyHashedPassword(attempt, attempt.PinHash, input.Pin);
    if (result == PasswordVerificationResult.Failed)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PairingAttempts"
            SET "FailedAttempts" = "FailedAttempts" + 1
            WHERE "Id" = {input.RequestId}
              AND "Completed" = 0
              AND "ExpiresAt" > {now}
              AND "FailedAttempts" < 5
            """, ct);
        await transaction.CommitAsync(ct);
        return Results.BadRequest(new { error = "Incorrect PIN." });
    }

    var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
        UPDATE "PairingAttempts"
        SET "Completed" = 1
        WHERE "Id" = {input.RequestId}
          AND "Completed" = 0
          AND "ExpiresAt" > {now}
          AND "FailedAttempts" < 5
        """, ct);
    if (claimed != 1)
        return Results.BadRequest(new { error = "Pairing request expired or locked." });

    var screen = new Screen { Name = attempt.DeviceName, Platform = attempt.Platform };
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    db.Screens.Add(screen);
    db.DeviceCredentials.Add(new DeviceCredential { ScreenId = screen.Id, TokenHash = HashToken(token) });
    db.AuditEvents.Add(new AuditEvent { Actor = attempt.DeviceName, Action = "screen.pair.complete", Object = screen.Id.ToString() });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { screenId = screen.Id, deviceToken = token, apiVersion = 1, serverPublicKey = (string?)null });
}).RequireRateLimiting("pairing");

api.MapGet("/screens/{screenId:guid}/manifest", async (Guid screenId, HttpRequest request,
    LessonCueDb db, ManifestService manifests, CancellationToken ct) =>
{
    if (!await HasDeviceAccess(request, db, screenId, ct)) return Results.Unauthorized();
    var manifest = await manifests.BuildAsync(screenId, ct);
    return manifest is null ? Results.NotFound() : Results.Ok(manifest);
});

api.MapGet("/screens/{screenId:guid}/control", async (Guid screenId, int? after, HttpRequest request,
    LessonCueDb db, CancellationToken ct) =>
{
    if (!await HasDeviceAccess(request, db, screenId, ct)) return Results.Unauthorized();
    var screen = await db.Screens.AsNoTracking().SingleOrDefaultAsync(x => x.Id == screenId && !x.Revoked, ct);
    if (screen is null) return Results.NotFound();
    var command = after is null ? null : await db.PlaybackCommands.AsNoTracking()
        .Where(x => x.ScreenId == screenId && x.Version > after.Value)
        .OrderBy(x => x.Version).FirstOrDefaultAsync(ct);
    return Results.Ok(new { changed = command is not null, version = command?.Version ?? screen.ControlVersion,
        action = command?.Action ?? "none", lessonId = command?.LessonId,
        itemId = command?.ItemId, positionMs = command?.PositionMs,
        issuedAt = command?.IssuedAt, state = screen.PlaybackState,
        screenshotRequestId = screen.AllowDiagnosticScreenshots && screen.ScreenshotStatus == "pending" &&
            screen.ScreenshotExpiresAt > DateTimeOffset.UtcNow ? screen.ScreenshotRequestId : null,
        screenshotExpiresAt = screen.AllowDiagnosticScreenshots && screen.ScreenshotStatus == "pending" &&
            screen.ScreenshotExpiresAt > DateTimeOffset.UtcNow ? screen.ScreenshotExpiresAt : null });
});

api.MapPut("/tv/screens/{screenId:guid}/diagnostics/screenshot/{requestId:guid}", async (Guid screenId, Guid requestId,
    HttpRequest request, LessonCueDb db, IHubContext<SyncHub> hub, CancellationToken ct) =>
{
    if (!await HasDeviceAccess(request, db, screenId, ct)) return Results.Unauthorized();
    var screen = await db.Screens.SingleOrDefaultAsync(x => x.Id == screenId && !x.Revoked, ct);
    if (screen is null) return Results.NotFound();
    if (!screen.AllowDiagnosticScreenshots || screen.ScreenshotStatus != "pending" ||
        screen.ScreenshotRequestId != requestId || screen.ScreenshotExpiresAt <= DateTimeOffset.UtcNow)
        return Results.Conflict(new { error = "This one-time diagnostic screenshot request is no longer active." });
    if (request.ContentLength is null or <= 0 or > 8 * 1024 * 1024)
        return Results.BadRequest(new { error = "Screenshot must be between 1 byte and 8 MB." });
    var extension = request.ContentType?.StartsWith("image/png", StringComparison.OrdinalIgnoreCase) == true ? ".png" :
        request.ContentType?.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) == true ? ".jpg" : null;
    if (extension is null) return Results.BadRequest(new { error = "Screenshot must be JPEG or PNG." });

    var relative = Path.Combine("diagnostics", "screens", screenId.ToString("N"), requestId.ToString("N") + extension);
    var destination = Path.GetFullPath(Path.Combine(dataPath, relative));
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    var temporary = destination + ".upload";
    await using (var output = File.Create(temporary)) await request.Body.CopyToAsync(output, ct);
    if (new FileInfo(temporary).Length > 8 * 1024 * 1024) { File.Delete(temporary); return Results.BadRequest(new { error = "Screenshot exceeds 8 MB." }); }
    var signature = new byte[8];
    await using (var input = File.OpenRead(temporary)) _ = await input.ReadAsync(signature, ct);
    var validJpeg = extension == ".jpg" && signature[0] == 0xff && signature[1] == 0xd8 && signature[2] == 0xff;
    var validPng = extension == ".png" && signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    if (!validJpeg && !validPng) { File.Delete(temporary); return Results.BadRequest(new { error = "Screenshot data does not match its image type." }); }
    ContainedPath.DeleteIfContained(dataPath, screen.ScreenshotRelativePath);
    File.Move(temporary, destination, true);
    screen.ScreenshotRelativePath = relative;
    screen.ScreenshotCapturedAt = DateTimeOffset.UtcNow;
    screen.ScreenshotStatus = "ready";
    db.AuditEvents.Add(new AuditEvent { Actor = screen.Name, Action = "screen.screenshot.capture", Object = screen.Id.ToString(),
        Summary = "Privacy-gated diagnostic screenshot captured; automatic expiry 24 hours" });
    await db.SaveChangesAsync(ct);
    await hub.Clients.Group("admins").SendAsync("ScreenStatusChanged", new { screen.Id }, ct);
    return Results.Accepted();
});

api.MapPost("/tv/status", async (TvStatusInput input, HttpRequest request, LessonCueDb db,
    IHubContext<SyncHub> hub, CancellationToken ct) =>
{
    if (!await HasDeviceAccess(request, db, input.ScreenId, ct)) return Results.Unauthorized();
    var screen = await db.Screens.SingleOrDefaultAsync(x => x.Id == input.ScreenId, ct);
    if (screen is null) return Results.NotFound();
    ScreenTelemetry.Apply(screen, input, DateTimeOffset.UtcNow,
        request.HttpContext.Connection.RemoteIpAddress?.ToString());
    if (input.SignageId is { } signageId)
    {
        var now = DateTimeOffset.UtcNow;
        var recent = await db.SignageProofRecords.OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(x => x.ScreenId == input.ScreenId && x.SignageId == signageId &&
                x.Event == "shown", ct);
        if (recent?.StartedAt < now.AddMinutes(-2)) recent = null;
        if (recent is null)
        {
            db.SignageProofRecords.Add(new SignageProofRecord
            {
                ScreenId = input.ScreenId, SignageId = signageId, Version = Math.Max(1, input.SignageVersion ?? 1),
                SignageName = input.SignageName?.Trim() ?? "", Event = string.IsNullOrWhiteSpace(input.SignageError) ? "shown" : "error",
                StartedAt = now, EndedAt = now, Error = input.SignageError
            });
        }
        else
        {
            recent.EndedAt = now;
            recent.DurationMs = Math.Max(0, (long)(now - recent.StartedAt).TotalMilliseconds);
            if (!string.IsNullOrWhiteSpace(input.SignageError)) recent.Error = input.SignageError;
        }
    }
    await db.SaveChangesAsync(ct);
    await hub.Clients.Group("admins").SendAsync("ScreenStatusChanged", new { screen.Id }, ct);
    return Results.Accepted();
});

app.MapHub<SyncHub>("/hubs/sync").RequireAuthorization();
app.MapHub<LessonCue.Server.Activities.ActivityHub>("/hubs/activities");
app.MapFallbackToFile("index.html");
app.Run();

static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

static IResult DerivativeFile(string? relativePath, string root, string contentType)
{
    var path = ContainedPath.ResolveExistingFile(root, relativePath);
    return path is not null
        ? Results.File(path, contentType, enableRangeProcessing: true)
        : Results.NotFound();
}

static async Task<bool> HasDeviceAccess(HttpRequest request, LessonCueDb db, Guid screenId, CancellationToken ct)
{
    var authorization = request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var hash = HashToken(authorization[7..].Trim());
    var access = await (from credential in db.DeviceCredentials.AsNoTracking()
        join screen in db.Screens.AsNoTracking() on credential.ScreenId equals screen.Id
        where credential.ScreenId == screenId && credential.TokenHash == hash && credential.RevokedAt == null
        select new { credential.CreatedAt, screen.Platform, screen.LastSeenAt }).SingleOrDefaultAsync(ct);
    if (access is null) return false;
    return !ScreenDiagnosticCleanupService.IsBrowserPairExpired(
        access.Platform, access.LastSeenAt, access.CreatedAt, DateTimeOffset.UtcNow);
}

public partial class Program;
