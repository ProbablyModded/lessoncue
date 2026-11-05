using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LessonCue.Server;

public sealed record BackupDestinationInput(
    string Provider,
    string? WebDavUrl,
    string Authentication,
    string? Username,
    string? Secret,
    int RetentionCount,
    int RetentionDays,
    string? FolderName = null,
    string? GoogleDriveClientId = null,
    string? GoogleDriveClientSecret = null);

public sealed record BackupDestinationStatus(
    string Provider,
    bool Enabled,
    string? WebDavUrl,
    string Authentication,
    string? Username,
    bool SecretConfigured,
    int RetentionCount,
    int RetentionDays,
    DateTimeOffset? LastUploadedAt,
    string? LastUploadedFileName,
    int? RemoteBackupCount,
    string? LastError,
    string? WebDavRootUrl = null,
    string? FolderName = null,
    DateTimeOffset? LastMediaSyncAt = null,
    int? LastMediaSyncAdded = null,
    int? LastMediaSyncUpdated = null,
    int? LastMediaSyncDeleted = null,
    string? GoogleDriveClientId = null,
    bool GoogleDriveConnected = false,
    bool GoogleDriveClientSecretConfigured = false,
    bool GoogleDriveReauthorizationRequired = false);

public sealed record BackupPolicyInput(
    bool Enabled,
    string Frequency,
    int HourLocal,
    int? WeeklyDay,
    bool IncludeMedia,
    int RetentionCount,
    int RetentionDays,
    string SecretHandling,
    string? BackupPassword,
    string? RemoteWebDavUrl,
    string RemoteAuthentication,
    string? RemoteUsername,
    string? RemoteSecret,
    IReadOnlyList<BackupDestinationInput>? Destinations = null,
    string? MediaMode = null);

public sealed record BackupPolicyStatus(
    bool Enabled,
    string Frequency,
    int HourLocal,
    int? WeeklyDay,
    bool IncludeMedia,
    int RetentionCount,
    int RetentionDays,
    string SecretHandling,
    bool BackupPasswordConfigured,
    string? RemoteWebDavUrl,
    string RemoteAuthentication,
    string? RemoteUsername,
    bool RemoteSecretConfigured,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? LastVerifiedAt,
    string? LastBackupFileName,
    string? LastError,
    DateTimeOffset? NextRunAt,
    bool Overdue,
    bool Running,
    IReadOnlyList<BackupDestinationStatus>? Destinations = null,
    string MediaMode = "backup");

public sealed class BackupPolicyService : BackgroundService
{
    private const string MediaSyncManifestName = ".lessoncue-media-sync.json";
    private const int OwnCloudMediaChunkBytes = 2 * 1024 * 1024;
    private const int MediaUploadAttempts = 3;
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromMinutes(2);
    private static readonly XNamespace DavNamespace = "DAV:";
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod MkColMethod = new("MKCOL");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string dataPath;
    private readonly string policyPath;
    private readonly IServiceScopeFactory scopes;
    private readonly BackupService backups;
    private readonly IDataProtector protector;
    private readonly IHttpClientFactory clients;
    private readonly GoogleDriveBackupClient googleDrive;
    private readonly ILogger<BackupPolicyService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool running;

    public BackupPolicyService(
        string dataPath,
        IServiceScopeFactory scopes,
        BackupService backups,
        IDataProtectionProvider protection,
        IHttpClientFactory clients,
        ILogger<BackupPolicyService> logger)
    {
        this.dataPath = dataPath;
        policyPath = Path.Combine(dataPath, "config", "backup-policy.json");
        this.scopes = scopes;
        this.backups = backups;
        protector = protection.CreateProtector("LessonCue.BackupPolicy.v1");
        this.clients = clients;
        googleDrive = new GoogleDriveBackupClient(clients);
        this.logger = logger;
    }

    public BackupPolicyStatus GetStatus(string timeZone)
    {
        var policy = Read();
        var next = policy.Enabled
            ? Schedule(policy, timeZone, DateTimeOffset.UtcNow)
            : null;
        return Public(policy, next);
    }

    public async Task<BackupPolicyStatus> UpdateAsync(
        BackupPolicyInput input,
        string timeZone,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
        var current = Read();
        var frequency = input.Frequency.Trim().ToLowerInvariant();
        if (frequency is not ("daily" or "weekly"))
            throw new ArgumentException("Choose a daily or weekly backup schedule.");
        if (input.HourLocal is < 0 or > 23)
            throw new ArgumentException("Backup hour must be from 0 through 23.");
        if (frequency == "weekly" && input.WeeklyDay is not (>= 0 and <= 6))
            throw new ArgumentException("Choose a weekday for the weekly backup.");
        if (input.RetentionCount is < 1 or > 365)
            throw new ArgumentException("Keep between 1 and 365 scheduled backups.");
        if (input.RetentionDays is < 1 or > 3650)
            throw new ArgumentException("Retention must be from 1 to 3,650 days.");
        var mediaMode = NormalizeMediaMode(input.MediaMode, input.IncludeMedia);
        var secretHandling = input.SecretHandling.Trim().ToLowerInvariant();
        if (secretHandling is not ("exclude" or "include"))
            throw new ArgumentException("Choose whether server credentials are excluded or included.");

        var protectedPassword = current.ProtectedBackupPassword;
        if (!string.IsNullOrEmpty(input.BackupPassword))
        {
            if (input.BackupPassword.Length is < 12 or > 1024)
                throw new ArgumentException("Backup passwords must contain 12–1,024 characters.");
            protectedPassword = protector.Protect(input.BackupPassword);
        }
        if (input.Enabled && string.IsNullOrEmpty(protectedPassword))
            throw new ArgumentException("Enter the password for scheduled encrypted backups.");

        var requestedDestinations = input.Destinations ?? LegacyDestination(input);
        var destinations = new List<StoredBackupDestination>();
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedDestinations)
        {
            var provider = NormalizeProvider(requested.Provider);
            if (provider == "googledrive")
            {
                var clientId = requested.GoogleDriveClientId?.Trim();
                if (string.IsNullOrWhiteSpace(clientId)) continue;
                if (clientId.Length > 512)
                    throw new ArgumentException("The Google OAuth client ID cannot exceed 512 characters.");
                if (!providers.Add(provider))
                    throw new ArgumentException("Configure only one Google Drive backup destination.");
                if (requested.RetentionCount is < 1 or > 365)
                    throw new ArgumentException("Google Drive backup retention must keep 1–365 copies.");
                if (requested.RetentionDays is < 1 or > 3650)
                    throw new ArgumentException("Google Drive backup retention must be 1–3,650 days.");

                var driveFolderName = NormalizeFolderName(requested.FolderName) ?? "LessonCue";
                var previousDrive = (current.Destinations ?? []).FirstOrDefault(destination =>
                    string.Equals(destination.Provider, provider, StringComparison.OrdinalIgnoreCase));
                var sameDriveClient = previousDrive is not null &&
                                      string.Equals(previousDrive.GoogleDriveOAuthClientId, clientId, StringComparison.Ordinal);
                var sameDriveRemote = sameDriveClient &&
                                      string.Equals(previousDrive!.FolderName, driveFolderName, StringComparison.Ordinal);
                var protectedClientSecret = sameDriveClient ? previousDrive!.ProtectedGoogleDriveOAuthClientSecret : null;
                if (!string.IsNullOrEmpty(requested.GoogleDriveClientSecret))
                {
                    if (requested.GoogleDriveClientSecret.Length > 4096)
                        throw new ArgumentException("The Google OAuth client secret cannot exceed 4,096 characters.");
                    protectedClientSecret = protector.Protect(requested.GoogleDriveClientSecret);
                }
                if (string.IsNullOrEmpty(protectedClientSecret))
                    throw new ArgumentException("Enter the Google OAuth client secret and save before connecting Google Drive.");

                destinations.Add(new StoredBackupDestination
                {
                    Provider = provider,
                    FolderName = driveFolderName,
                    Authentication = "oauth",
                    GoogleDriveOAuthClientId = clientId,
                    ProtectedGoogleDriveOAuthClientSecret = protectedClientSecret,
                    ProtectedGoogleDriveRefreshToken = sameDriveClient ? previousDrive!.ProtectedGoogleDriveRefreshToken : null,
                    GoogleDriveFolderId = sameDriveRemote ? previousDrive!.GoogleDriveFolderId : null,
                    RetentionCount = requested.RetentionCount,
                    RetentionDays = requested.RetentionDays,
                    LastUploadedAt = sameDriveRemote ? previousDrive!.LastUploadedAt : null,
                    LastUploadedFileName = sameDriveRemote ? previousDrive!.LastUploadedFileName : null,
                    RemoteBackupCount = sameDriveRemote ? previousDrive!.RemoteBackupCount : null,
                    LastError = sameDriveRemote ? previousDrive!.LastError : null,
                    GoogleDriveReauthorizationRequired = sameDriveRemote &&
                        string.IsNullOrEmpty(requested.GoogleDriveClientSecret)
                        ? previousDrive!.GoogleDriveReauthorizationRequired
                        : false,
                    LastMediaSyncAt = sameDriveRemote ? previousDrive!.LastMediaSyncAt : null,
                    LastMediaSyncAdded = sameDriveRemote ? previousDrive!.LastMediaSyncAdded : null,
                    LastMediaSyncUpdated = sameDriveRemote ? previousDrive!.LastMediaSyncUpdated : null,
                    LastMediaSyncDeleted = sameDriveRemote ? previousDrive!.LastMediaSyncDeleted : null
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(requested.WebDavUrl)) continue;
            if (!providers.Add(provider))
                throw new ArgumentException($"Configure only one {provider} backup destination.");
            var rootUrl = NormalizeRemoteUrl(requested.WebDavUrl);
            var folderName = NormalizeFolderName(requested.FolderName);
            var remoteUrl = AppendRemoteFolder(rootUrl, folderName);
            var authentication = requested.Authentication.Trim().ToLowerInvariant();
            if (authentication is not ("none" or "basic" or "bearer"))
                throw new ArgumentException("Choose no authentication, basic authentication, or a bearer token.");
            var username = string.IsNullOrWhiteSpace(requested.Username)
                ? null
                : requested.Username.Trim();
            if (authentication == "basic" && string.IsNullOrWhiteSpace(username))
                throw new ArgumentException($"Enter the {provider} WebDAV username.");
            if (requested.RetentionCount is < 1 or > 365)
                throw new ArgumentException($"{provider} backup retention must keep 1–365 copies.");
            if (requested.RetentionDays is < 1 or > 3650)
                throw new ArgumentException($"{provider} backup retention must be 1–3,650 days.");

            var previous = (current.Destinations ?? []).FirstOrDefault(destination =>
                string.Equals(destination.Provider, provider, StringComparison.OrdinalIgnoreCase));
            var sameRemote = previous is not null &&
                             string.Equals(previous.WebDavUrl, remoteUrl, StringComparison.Ordinal) &&
                             string.Equals(previous.FolderName, folderName, StringComparison.Ordinal) &&
                             string.Equals(previous.Authentication, authentication, StringComparison.Ordinal) &&
                             string.Equals(previous.Username, username, StringComparison.Ordinal);
            var protectedRemoteSecret = sameRemote ? previous!.ProtectedSecret : null;
            if (!string.IsNullOrEmpty(requested.Secret))
            {
                if (requested.Secret.Length > 4096)
                    throw new ArgumentException("The remote credential cannot exceed 4,096 characters.");
                protectedRemoteSecret = protector.Protect(requested.Secret);
            }
            if (authentication != "none" && string.IsNullOrEmpty(protectedRemoteSecret))
                throw new ArgumentException($"Enter the {provider} WebDAV password or bearer token.");

            destinations.Add(new StoredBackupDestination
            {
                Provider = provider,
                WebDavRootUrl = rootUrl,
                WebDavUrl = remoteUrl,
                FolderName = folderName,
                Authentication = authentication,
                Username = username,
                ProtectedSecret = protectedRemoteSecret,
                RetentionCount = requested.RetentionCount,
                RetentionDays = requested.RetentionDays,
                LastUploadedAt = sameRemote ? previous!.LastUploadedAt : null,
                LastUploadedFileName = sameRemote ? previous!.LastUploadedFileName : null,
                RemoteBackupCount = sameRemote ? previous!.RemoteBackupCount : null,
                LastError = sameRemote ? previous!.LastError : null,
                LastMediaSyncAt = sameRemote ? previous!.LastMediaSyncAt : null,
                LastMediaSyncAdded = sameRemote ? previous!.LastMediaSyncAdded : null,
                LastMediaSyncUpdated = sameRemote ? previous!.LastMediaSyncUpdated : null,
                LastMediaSyncDeleted = sameRemote ? previous!.LastMediaSyncDeleted : null
            });
        }

        if (mediaMode == "sync" && destinations.Count == 0)
            throw new ArgumentException(
                "Media sync requires at least one configured off-site destination.");

        var legacy = destinations.FirstOrDefault(destination =>
            string.Equals(destination.Provider, "webdav", StringComparison.OrdinalIgnoreCase));

        var revised = current with
        {
            Enabled = input.Enabled,
            EnabledAt = input.Enabled
                ? current.EnabledAt ?? DateTimeOffset.UtcNow
                : null,
            Frequency = frequency,
            HourLocal = input.HourLocal,
            WeeklyDay = frequency == "weekly" ? input.WeeklyDay : null,
            IncludeMedia = mediaMode == "backup",
            MediaMode = mediaMode,
            RetentionCount = input.RetentionCount,
            RetentionDays = input.RetentionDays,
            SecretHandling = secretHandling,
            ProtectedBackupPassword = protectedPassword,
            RemoteWebDavUrl = legacy?.WebDavUrl,
            RemoteAuthentication = legacy?.Authentication ?? "none",
            RemoteUsername = legacy?.Username,
            ProtectedRemoteSecret = legacy?.ProtectedSecret,
            Destinations = destinations
        };
        await WriteAsync(revised, ct);
        return Public(
            revised,
            revised.Enabled ? Schedule(revised, timeZone, DateTimeOffset.UtcNow) : null);
        }
        finally { gate.Release(); }
    }

    public async Task<BackupPolicyStatus> RunNowAsync(string timeZone, CancellationToken ct)
    {
        await RunPolicyAsync(timeZone, ct, force: true);
        return GetStatus(timeZone);
    }

    public async Task<string> BeginGoogleDriveAuthorizationAsync(
        string callbackUri,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(callbackUri, UriKind.Absolute, out var callback) ||
            callback.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(callback.UserInfo) ||
            !string.IsNullOrEmpty(callback.Query) ||
            !string.IsNullOrEmpty(callback.Fragment))
            throw new ArgumentException("Google Drive authorization requires this server's HTTPS callback URL.");

        await gate.WaitAsync(ct);
        try
        {
            var policy = Read();
            var destinations = policy.Destinations?.ToList() ?? [];
            var index = destinations.FindIndex(item => item.Provider == "googledrive");
            if (index < 0)
                throw new InvalidOperationException("Save a Google Drive destination before connecting it.");
            var destination = destinations[index];
            if (string.IsNullOrWhiteSpace(destination.GoogleDriveOAuthClientId) ||
                string.IsNullOrWhiteSpace(destination.ProtectedGoogleDriveOAuthClientSecret))
                throw new InvalidOperationException("Save the Google OAuth client ID and secret first.");

            var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var requestConsent = string.IsNullOrEmpty(destination.ProtectedGoogleDriveRefreshToken) ||
                                 destination.GoogleDriveReauthorizationRequired;
            destination = destination with
            {
                GoogleDriveOAuthStateProtected = protector.Protect(state),
                GoogleDriveOAuthStateExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                GoogleDriveOAuthRedirectUri = callback.AbsoluteUri,
                LastError = null,
                GoogleDriveReauthorizationRequired = false
            };
            destinations[index] = destination;
            await WriteAsync(policy with { Destinations = destinations }, ct);
            return googleDrive.CreateAuthorizationUrl(
                destination.GoogleDriveOAuthClientId,
                callback.AbsoluteUri,
                state,
                requestConsent);
        }
        finally { gate.Release(); }
    }

    public async Task CompleteGoogleDriveAuthorizationAsync(
        string state,
        string code,
        CancellationToken ct)
    {
        string clientId;
        string clientSecret;
        string callbackUri;
        string? existingProtectedRefreshToken;
        await gate.WaitAsync(ct);
        try
        {
            var policy = Read();
            var destinations = policy.Destinations?.ToList() ?? [];
            var index = destinations.FindIndex(item => item.Provider == "googledrive");
            if (index < 0) throw new InvalidOperationException("The Google Drive destination was removed.");
            var destination = destinations[index];
            if (string.IsNullOrEmpty(destination.GoogleDriveOAuthStateProtected) ||
                destination.GoogleDriveOAuthStateExpiresAt is not { } expiresAt ||
                expiresAt < DateTimeOffset.UtcNow ||
                string.IsNullOrEmpty(destination.GoogleDriveOAuthRedirectUri))
                throw new InvalidOperationException("This Google authorization request expired. Start Connect again.");

            string expectedState;
            try { expectedState = protector.Unprotect(destination.GoogleDriveOAuthStateProtected); }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("The Google authorization state is invalid. Start Connect again.");
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expectedState), Encoding.UTF8.GetBytes(state)))
                throw new InvalidOperationException("Google returned an invalid authorization state. Start Connect again.");

            clientId = destination.GoogleDriveOAuthClientId ??
                       throw new InvalidOperationException("The Google OAuth client ID is missing.");
            clientSecret = string.IsNullOrEmpty(destination.ProtectedGoogleDriveOAuthClientSecret)
                ? throw new InvalidOperationException("The Google OAuth client secret is missing.")
                : protector.Unprotect(destination.ProtectedGoogleDriveOAuthClientSecret);
            callbackUri = destination.GoogleDriveOAuthRedirectUri;
            existingProtectedRefreshToken = destination.ProtectedGoogleDriveRefreshToken;

            // Consume the one-time state before exchanging the authorization code.
            destinations[index] = destination with
            {
                GoogleDriveOAuthStateProtected = null,
                GoogleDriveOAuthStateExpiresAt = null,
                GoogleDriveOAuthRedirectUri = null
            };
            await WriteAsync(policy with { Destinations = destinations }, ct);
        }
        finally { gate.Release(); }

        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Google did not return an authorization code.");
        var refreshToken = await googleDrive.ExchangeAuthorizationCodeAsync(
            clientId, clientSecret, code, callbackUri, ct);
        if (refreshToken is null && string.IsNullOrEmpty(existingProtectedRefreshToken))
            throw new InvalidOperationException(
                "Google authorized LessonCue but did not return a refresh token. Reconnect and approve offline access.");

        await gate.WaitAsync(ct);
        try
        {
            var policy = Read();
            var destinations = policy.Destinations?.ToList() ?? [];
            var index = destinations.FindIndex(item => item.Provider == "googledrive");
            if (index < 0 || destinations[index].GoogleDriveOAuthClientId != clientId)
                throw new InvalidOperationException("Google Drive settings changed during authorization. Connect again.");
            destinations[index] = destinations[index] with
            {
                ProtectedGoogleDriveRefreshToken = refreshToken is null
                    ? existingProtectedRefreshToken
                    : protector.Protect(refreshToken),
                LastError = null,
                GoogleDriveReauthorizationRequired = false
            };
            await WriteAsync(policy with { Destinations = destinations }, ct);
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                var policy = Read();
                if (policy.Enabled)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
                    var timeZone = await db.Organizations.AsNoTracking()
                        .OrderBy(item => item.Id).Select(x => x.TimeZone)
                        .FirstOrDefaultAsync(stoppingToken) ?? "UTC";
                    var boundary = LatestBoundary(policy, timeZone, DateTimeOffset.UtcNow);
                    var recentlyAttempted = policy.LastAttemptAt is not null &&
                                            policy.LastAttemptAt > DateTimeOffset.UtcNow.AddHours(-1);
                    var authorizationBlockedForBoundary = policy.LastAttemptAt is { } attempt &&
                        attempt >= boundary && HasGoogleDriveReauthorizationRequired(policy);
                    if (!recentlyAttempted && !authorizationBlockedForBoundary &&
                        (policy.LastSucceededAt is null || policy.LastSucceededAt < boundary))
                        await RunPolicyAsync(timeZone, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled LessonCue backup failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunPolicyAsync(string timeZone, CancellationToken ct, bool force = false)
    {
        if (!await gate.WaitAsync(0, ct))
            throw new InvalidOperationException("A scheduled backup is already running.");
        try
        {
            // Re-read after taking the same gate used by UpdateAsync. A policy
            // snapshot obtained by the scheduler or RunNow before the lock is
            // not authoritative and must never be written back over a newer
            // administrator change.
            var policy = Read();
            var now = DateTimeOffset.UtcNow;
            var boundary = LatestBoundary(policy, timeZone, now);
            var recentlyAttempted = policy.LastAttemptAt is not null &&
                                    policy.LastAttemptAt > now.AddHours(-1);
            if (!force && (!policy.Enabled || recentlyAttempted ||
                policy.LastSucceededAt is not null && policy.LastSucceededAt >= boundary)) return;
            if (string.IsNullOrEmpty(policy.ProtectedBackupPassword))
                throw new InvalidOperationException("Save a scheduled-backup password before running the policy.");

            running = true;
            policy = policy with { LastAttemptAt = now, LastError = null };
            try
            {
                await WriteAsync(policy, ct);
                var password = protector.Unprotect(policy.ProtectedBackupPassword);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
                var record = await backups.CreateAsync(
                    db,
                    ResolveMediaMode(policy) == "backup",
                    "scheduled-backup",
                    ct,
                    password,
                    policy.SecretHandling);
                var verification = await backups.VerifyStoredAsync(record, ct, password);
                var verifiedAt = DateTimeOffset.UtcNow;

                var remoteErrors = new List<string>();
                var destinations = policy.Destinations?.ToList() ?? [];
                for (var index = 0; index < destinations.Count; index++)
                {
                    var destination = destinations[index];
                    try
                    {
                        if (destination.Provider == "googledrive" &&
                            destination.GoogleDriveReauthorizationRequired)
                        {
                            remoteErrors.Add($"{destination.Provider}: {destination.LastError ??
                                "Reconnect Google Drive in Backup & Recovery."}");
                            continue;
                        }
                        GoogleDriveBackupClient.GoogleDriveSession? driveSession = null;
                        if (destination.Provider == "googledrive")
                        {
                            driveSession = await OpenGoogleDriveSessionAsync(destination, ct);
                            var prepared = await EnsureGoogleDriveDestinationAsync(
                                destination, driveSession, ct);
                            if (prepared.GoogleDriveFolderId != destination.GoogleDriveFolderId)
                            {
                                destination = prepared;
                                destinations[index] = destination;
                                policy = policy with { Destinations = destinations };
                                await WriteAsync(policy, ct);
                            }
                        }

                        await UploadRemoteAsync(destination, record, driveSession, ct);
                        destination = destination with
                        {
                            LastUploadedAt = DateTimeOffset.UtcNow,
                            LastUploadedFileName = record.FileName,
                            LastError = null
                        };
                        destinations[index] = destination;
                        policy = policy with { Destinations = destinations };
                        await WriteAsync(policy, ct);

                        var mediaSync = ResolveMediaMode(policy) == "sync"
                            ? await SyncMediaAsync(destination, driveSession, ct)
                            : null;
                        if (mediaSync is not null)
                        {
                            destination = destination with
                            {
                                LastMediaSyncAt = mediaSync.CompletedAt,
                                LastMediaSyncAdded = mediaSync.Added,
                                LastMediaSyncUpdated = mediaSync.Updated,
                                LastMediaSyncDeleted = mediaSync.Deleted
                            };
                            destinations[index] = destination;
                            policy = policy with { Destinations = destinations };
                            await WriteAsync(policy, ct);
                        }
                        var remaining = await PruneRemoteAsync(destination, driveSession, ct);
                        destinations[index] = destination with
                        {
                            RemoteBackupCount = remaining,
                            LastError = null
                        };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var safeError = SafeError(ex);
                        destinations[index] = destination with
                        {
                            LastError = safeError,
                            GoogleDriveReauthorizationRequired = destination.Provider == "googledrive" &&
                                ex is GoogleDriveAuthorizationException
                        };
                        remoteErrors.Add($"{destination.Provider}: {safeError}");
                    }
                }
                policy = policy with { Destinations = destinations };
                await WriteAsync(policy, ct);
                if (remoteErrors.Count > 0)
                    throw new IOException($"One or more off-site backup destinations failed: {string.Join("; ", remoteErrors)}");

                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = "system",
                    Action = "backup.schedule.run",
                    Object = record.Id.ToString(),
                    Summary = JsonSerializer.Serialize(new
                    {
                        record.FileName,
                        remote = destinations.Count,
                        mediaMode = ResolveMediaMode(policy),
                        verification.FileCount
                    })
                });
                await PruneAsync(db, policy, record.Id, ct);
                await db.SaveChangesAsync(ct);
                policy = policy with
                {
                    LastSucceededAt = DateTimeOffset.UtcNow,
                    LastVerifiedAt = verifiedAt,
                    LastBackupFileName = record.FileName,
                    LastError = null
                };
                await WriteAsync(policy, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                policy = policy with { LastError = SafeError(ex) };
                await WriteAsync(policy, CancellationToken.None);
                throw;
            }
            finally
            {
                running = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GoogleDriveBackupClient.GoogleDriveSession> OpenGoogleDriveSessionAsync(
        StoredBackupDestination destination,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destination.GoogleDriveOAuthClientId) ||
            string.IsNullOrEmpty(destination.ProtectedGoogleDriveOAuthClientSecret) ||
            string.IsNullOrEmpty(destination.ProtectedGoogleDriveRefreshToken))
            throw new InvalidOperationException(
                "Google Drive is not connected. Save its OAuth settings and use Connect Google Drive in Backup & Recovery.");
        return await googleDrive.CreateSessionAsync(
            destination.GoogleDriveOAuthClientId,
            protector.Unprotect(destination.ProtectedGoogleDriveOAuthClientSecret),
            protector.Unprotect(destination.ProtectedGoogleDriveRefreshToken),
            ct);
    }

    private static async Task<StoredBackupDestination> EnsureGoogleDriveDestinationAsync(
        StoredBackupDestination destination,
        GoogleDriveBackupClient.GoogleDriveSession session,
        CancellationToken ct)
    {
        var folderName = destination.FolderName ?? "LessonCue";
        var rootFolders = await session.ListChildrenAsync("root", ct);
        var root = rootFolders.FirstOrDefault(item =>
            item.Id == destination.GoogleDriveFolderId &&
            item.MimeType == "application/vnd.google-apps.folder" &&
            item.ManagedType == "backup-root") ??
            rootFolders.FirstOrDefault(item =>
                item.Name == folderName &&
                item.MimeType == "application/vnd.google-apps.folder" &&
                item.ManagedType == "backup-root");
        if (root is null)
        {
            var folderId = await session.GetOrCreateFolderAsync(
                "root", folderName, "backup-root", ct);
            root = new GoogleDriveItem(
                folderId, folderName, "application/vnd.google-apps.folder", null, null, null,
                "backup-root", folderName);
        }
        return destination with { GoogleDriveFolderId = root.Id };
    }

    private async Task UploadRemoteAsync(
        StoredBackupDestination destination,
        BackupRecord record,
        GoogleDriveBackupClient.GoogleDriveSession? driveSession,
        CancellationToken ct)
    {
        var path = backups.Resolve(record.FileName)
                   ?? throw new FileNotFoundException("The scheduled backup file is missing.");
        if (destination.Provider == "googledrive")
        {
            if (driveSession is null || string.IsNullOrEmpty(destination.GoogleDriveFolderId))
                throw new InvalidOperationException("Google Drive destination is not ready.");
            await using var driveStream = File.OpenRead(path);
            await driveSession.UploadFileAsync(
                destination.GoogleDriveFolderId,
                record.FileName,
                "application/vnd.lessoncue.backup",
                "backup",
                driveStream,
                driveStream.Length,
                null,
                ct);
            return;
        }
        var baseUri = new Uri(destination.WebDavUrl!, UriKind.Absolute);
        await EnsureRemoteCollectionAsync(destination, baseUri, ct);
        var target = RemoteUri(baseUri, record.FileName);
        using var request = new HttpRequestMessage(HttpMethod.Put, target);
        var secret = string.IsNullOrEmpty(destination.ProtectedSecret)
            ? null
            : protector.Unprotect(destination.ProtectedSecret);
        AddRemoteAuthorization(request, destination, secret);
        await using var stream = File.OpenRead(path);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/vnd.lessoncue.backup");
        request.Content.Headers.ContentLength = stream.Length;
        var response = await clients.CreateClient("backup-offsite")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new IOException(
                    $"The {destination.Provider} WebDAV target rejected the backup ({(int)response.StatusCode}).");
        }
    }

    private async Task<MediaSyncResult> SyncMediaAsync(
        StoredBackupDestination destination,
        GoogleDriveBackupClient.GoogleDriveSession? driveSession,
        CancellationToken ct)
    {
        if (destination.Provider == "googledrive")
        {
            if (driveSession is null || string.IsNullOrEmpty(destination.GoogleDriveFolderId))
                throw new InvalidOperationException("Google Drive destination is not ready.");
            return await SyncGoogleDriveMediaAsync(destination, driveSession, ct);
        }
        var localRoot = Path.Combine(dataPath, "media");
        var baseUri = new Uri(destination.WebDavUrl!, UriKind.Absolute);
        await EnsureRemoteCollectionAsync(destination, baseUri, ct);
        var mediaUri = RemoteUri(baseUri, "media/");
        await EnsureRemoteCollectionAsync(destination, mediaUri, ct);

        var remote = await ReadRemoteMediaFilesAsync(destination, mediaUri, ct);
        var previous = await ReadMediaSyncManifestAsync(destination, mediaUri, ct);
        var local = await BuildLocalMediaManifestAsync(localRoot, ct);
        var useOwnCloudChunking = IsOwnCloudProvider(destination.Provider) &&
                                  local.Files.Values.Any(file => file.Bytes > OwnCloudMediaChunkBytes) &&
                                  await OwnCloudSupportsChunkingAsync(destination, baseUri, ct);

        var added = 0;
        var updated = 0;
        var deleted = 0;

        var directorySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filePath in local.Files.Keys)
        {
            var parent = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
            while (!string.IsNullOrWhiteSpace(parent))
            {
                directorySet.Add(parent);
                parent = Path.GetDirectoryName(parent)?.Replace('\\', '/') ?? "";
            }
        }
        var directories = directorySet
            .OrderBy(path => path.Count(value => value == '/'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();
        foreach (var directory in directories)
        {
            if (remote.Directories.Contains(directory)) continue;
            await EnsureRemoteCollectionAsync(
                destination,
                RemoteUri(mediaUri, directory + "/"),
                ct);
            remote.Directories.Add(directory);
        }

        foreach (var pair in local.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var relativePath = pair.Key;
            var expected = pair.Value;
            var unchanged = previous?.Files.TryGetValue(relativePath, out var previousFile) == true &&
                            previousFile == expected &&
                            remote.Files.ContainsKey(relativePath);
            if (unchanged) continue;

            var sourcePath = Path.Combine(
                localRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var fileInfo = new FileInfo(sourcePath);
            await PutRemoteFileAsync(
                destination,
                RemoteUri(mediaUri, relativePath),
                sourcePath,
                relativePath,
                fileInfo.Length,
                "application/octet-stream",
                useOwnCloudChunking,
                ct);
            if (remote.Files.ContainsKey(relativePath)) updated++;
            else added++;
            remote.Files[relativePath] = new RemoteMediaFile(relativePath, expected.Bytes);
        }

        if (previous is not null)
        {
            foreach (var relativePath in previous.Files.Keys
                         .Except(local.Files.Keys, StringComparer.Ordinal)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (!remote.Files.ContainsKey(relativePath)) continue;
                await DeleteRemotePathAsync(
                    destination,
                    RemoteUri(mediaUri, relativePath),
                    "media cleanup",
                    ct);
                remote.Files.Remove(relativePath);
                deleted++;
            }
        }

        var manifest = new MediaSyncManifest(
            "LessonCue",
            1,
            DateTimeOffset.UtcNow,
            local.Files);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await PutRemoteContentAsync(
            destination,
            RemoteUri(mediaUri, MediaSyncManifestName),
            manifestBytes,
            "application/json",
            "media sync manifest",
            ct);

        return new MediaSyncResult(DateTimeOffset.UtcNow, added, updated, deleted);
    }

    private async Task<MediaSyncResult> SyncGoogleDriveMediaAsync(
        StoredBackupDestination destination,
        GoogleDriveBackupClient.GoogleDriveSession session,
        CancellationToken ct)
    {
        var localRoot = Path.Combine(dataPath, "media");
        var driveRoot = destination.GoogleDriveFolderId!;
        var mediaFolderId = await session.GetOrCreateFolderAsync(
            driveRoot, "media", "media-root", ct);
        var remote = await ReadGoogleDriveMediaFilesAsync(session, mediaFolderId, ct);
        var previous = await ReadGoogleDriveMediaSyncManifestAsync(
            session, remote.ManifestId, ct);
        var local = await BuildLocalMediaManifestAsync(localRoot, ct);
        var added = 0;
        var updated = 0;
        var deleted = 0;

        var directorySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filePath in local.Files.Keys)
        {
            var parent = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
            while (!string.IsNullOrWhiteSpace(parent))
            {
                directorySet.Add(parent);
                parent = Path.GetDirectoryName(parent)?.Replace('\\', '/') ?? "";
            }
        }
        var directories = directorySet
            .OrderBy(path => path.Count(value => value == '/'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();
        var directoryIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [""] = mediaFolderId
        };
        foreach (var directory in directories)
        {
            var parentPath = Path.GetDirectoryName(directory)?.Replace('\\', '/') ?? "";
            var parentId = directoryIds.GetValueOrDefault(parentPath);
            if (parentId is null)
                throw new IOException($"Google Drive media folder hierarchy is incomplete at '{directory}'.");
            var segment = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(segment))
                throw new IOException("A local media folder has an invalid name.");
            var id = remote.Directories.GetValueOrDefault(directory);
            if (id is null)
                id = await session.GetOrCreateFolderAsync(
                    parentId, segment, "media-directory", ct);
            directoryIds[directory] = id;
        }

        foreach (var pair in local.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var relativePath = pair.Key;
            var expected = pair.Value;
            var existing = remote.Files.GetValueOrDefault(relativePath);
            var unchanged = previous?.Files.TryGetValue(relativePath, out var previousFile) == true &&
                            previousFile == expected && existing is not null;
            if (unchanged) continue;

            var sourcePath = Path.Combine(
                localRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var parentPath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
            if (!directoryIds.TryGetValue(parentPath, out var parentId))
                throw new IOException($"Google Drive media folder hierarchy is incomplete at '{relativePath}'.");
            await using var stream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var uploaded = await session.UploadFileAsync(
                parentId,
                Path.GetFileName(relativePath),
                "application/octet-stream",
                "media-file",
                stream,
                stream.Length,
                existing?.Id,
                ct);
            if (existing is null) added++;
            else updated++;
            remote.Files[relativePath] = uploaded;
        }

        if (previous is not null)
        {
            foreach (var relativePath in previous.Files.Keys
                         .Except(local.Files.Keys, StringComparer.Ordinal)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (!remote.Files.TryGetValue(relativePath, out var stale)) continue;
                await session.DeleteFileAsync(stale.Id, ct);
                remote.Files.Remove(relativePath);
                deleted++;
            }
        }

        var manifest = new MediaSyncManifest("LessonCue", 1, DateTimeOffset.UtcNow, local.Files);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await using var manifestStream = new MemoryStream(manifestBytes, writable: false);
        await session.UploadFileAsync(
            mediaFolderId,
            MediaSyncManifestName,
            "application/json",
            "media-manifest",
            manifestStream,
            manifestBytes.Length,
            remote.ManifestId,
            ct);
        return new MediaSyncResult(DateTimeOffset.UtcNow, added, updated, deleted);
    }

    private static async Task<GoogleDriveMediaState> ReadGoogleDriveMediaFilesAsync(
        GoogleDriveBackupClient.GoogleDriveSession session,
        string mediaFolderId,
        CancellationToken ct)
    {
        var files = new Dictionary<string, GoogleDriveItem>(StringComparer.Ordinal);
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        string? manifestId = null;
        var pending = new Queue<(string Id, string RelativePath)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue((mediaFolderId, ""));
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current.Id)) continue;
            foreach (var item in await session.ListChildrenAsync(current.Id, ct))
            {
                if (item.ManagedType == "media-directory" &&
                    item.MimeType == "application/vnd.google-apps.folder")
                {
                    var relativePath = CombineRemoteMediaPath(current.RelativePath, item.Name);
                    if (relativePath is null) continue;
                    directories.TryAdd(relativePath, item.Id);
                    pending.Enqueue((item.Id, relativePath));
                    continue;
                }

                if (item.ManagedType == "media-manifest" &&
                    current.RelativePath.Length == 0 &&
                    item.MimeType != "application/vnd.google-apps.folder" &&
                    item.Name == MediaSyncManifestName)
                {
                    manifestId = item.Id;
                    continue;
                }

                if (item.ManagedType != "media-file" ||
                    item.MimeType == "application/vnd.google-apps.folder")
                    continue;
                var filePath = CombineRemoteMediaPath(current.RelativePath, item.Name);
                if (filePath is null) continue;
                files.TryAdd(filePath, item with { ManagedPath = filePath });
            }
        }
        return new GoogleDriveMediaState(files, directories, manifestId);
    }

    private static async Task<MediaSyncManifest?> ReadGoogleDriveMediaSyncManifestAsync(
        GoogleDriveBackupClient.GoogleDriveSession session,
        string? manifestId,
        CancellationToken ct)
    {
        if (manifestId is null) return null;
        var bytes = await session.DownloadFileAsync(manifestId, ct);
        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            var manifest = await JsonSerializer.DeserializeAsync<MediaSyncManifest>(
                stream, JsonOptions, ct);
            if (manifest is null ||
                !string.Equals(manifest.Product, "LessonCue", StringComparison.Ordinal) ||
                manifest.FormatVersion != 1 ||
                manifest.Files is null ||
                manifest.Files.Count > 100_000 ||
                manifest.Files.Any(pair =>
                    !IsSafeMediaRelativePath(pair.Key) ||
                    pair.Value is null ||
                    pair.Value.Bytes < 0 || pair.Value.Sha256 is null ||
                    pair.Value.Sha256.Length != 64))
                return null;
            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task EnsureRemoteCollectionAsync(
        StoredBackupDestination destination,
        Uri uri,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(MkColMethod, uri);
        AddRemoteAuthorization(request, destination, UnprotectSecret(destination));
        var response = await clients.CreateClient("backup-offsite").SendAsync(request, ct);
        var createStatus = response.StatusCode;
        using (response)
        {
            if (response.IsSuccessStatusCode ||
                response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                return;
        }

        // Some ownCloud installations answer MKCOL with 400 when the
        // collection already exists. Verify the collection with the WebDAV
        // operation that is safe for an existing folder before reporting the
        // create failure. A failed verification keeps both statuses so bad
        // credentials, a wrong path, and a genuinely missing parent remain
        // actionable rather than being silently accepted.
        var verificationStatus = await ProbeRemoteCollectionAsync(destination, uri, ct);
        if (verificationStatus is not null &&
            (int)verificationStatus >= 200 && (int)verificationStatus < 300)
            return;

        var verification = verificationStatus is null
            ? "no response"
            : ((int)verificationStatus).ToString(CultureInfo.InvariantCulture);
        throw new IOException(
            $"The {destination.Provider} WebDAV folder could not be created ({(int)createStatus}) " +
            $"or verified as an existing collection ({verification}). Check the WebDAV URL, " +
            "parent folder, and credentials.");
    }

    private async Task<System.Net.HttpStatusCode?> ProbeRemoteCollectionAsync(
        StoredBackupDestination destination,
        Uri uri,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(PropFindMethod, uri);
        request.Headers.Add("Depth", "0");
        request.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype /></d:prop></d:propfind>",
            Encoding.UTF8,
            "application/xml");
        AddRemoteAuthorization(request, destination, UnprotectSecret(destination));
        using var response = await clients.CreateClient("backup-offsite").SendAsync(request, ct);
        return response.StatusCode;
    }

    private async Task<RemoteMediaState> ReadRemoteMediaFilesAsync(
        StoredBackupDestination destination,
        Uri mediaUri,
        CancellationToken ct)
    {
        var files = new Dictionary<string, RemoteMediaFile>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(Uri Uri, string RelativePath)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((mediaUri, ""));

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current.Uri.AbsoluteUri)) continue;
            var resources = await ReadRemoteResourcesAsync(
                destination, current.Uri, mediaUri, ct);
            foreach (var resource in resources)
            {
                if (resource.RelativePath.Length == 0 ||
                    string.Equals(resource.RelativePath, MediaSyncManifestName, StringComparison.Ordinal))
                    continue;
                if (resource.IsCollection)
                {
                    directories.Add(resource.RelativePath);
                    pending.Enqueue((resource.Uri, resource.RelativePath));
                }
                else
                {
                    files[resource.RelativePath] = new RemoteMediaFile(
                        resource.RelativePath,
                        resource.Bytes);
                }
            }
        }
        return new RemoteMediaState(files, directories);
    }

    private async Task<List<RemoteResource>> ReadRemoteResourcesAsync(
        StoredBackupDestination destination,
        Uri parentUri,
        Uri mediaUri,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(PropFindMethod, parentUri);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype /><d:getcontentlength /></d:prop></d:propfind>",
            Encoding.UTF8,
            "application/xml");
        AddRemoteAuthorization(request, destination, UnprotectSecret(destination));
        var response = await clients.CreateClient("backup-offsite")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return [];
            if ((int)response.StatusCode is not (200 or 207))
                throw new IOException(
                    $"The {destination.Provider} media folder could not be listed ({(int)response.StatusCode}).");
            await using var content = await response.Content.ReadAsStreamAsync(ct);
            return await ParseRemoteResourcesAsync(content, parentUri, mediaUri, ct);
        }
    }

    private static async Task<List<RemoteResource>> ParseRemoteResourcesAsync(
        Stream content,
        Uri parentUri,
        Uri mediaUri,
        CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000
        };
        using var reader = XmlReader.Create(content, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var resources = new List<RemoteResource>();
        foreach (var response in document.Descendants(DavNamespace + "response"))
        {
            var href = (string?)response.Element(DavNamespace + "href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            Uri? uri = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                ? absolute
                : Uri.TryCreate(parentUri, href, out var relative) ? relative : null;
            if (uri is null) continue;
            var relativePath = RelativeRemotePath(mediaUri, uri);
            if (relativePath is null) continue;
            var isCollection = response
                .Descendants(DavNamespace + "resourcetype")
                .Elements(DavNamespace + "collection")
                .Any();
            var lengthText = (string?)response
                .Descendants(DavNamespace + "getcontentlength")
                .FirstOrDefault();
            long? length = long.TryParse(
                lengthText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedLength)
                ? parsedLength
                : null;
            resources.Add(new RemoteResource(relativePath, uri, isCollection, length));
        }
        return resources
            .GroupBy(resource => resource.RelativePath, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<MediaSyncManifest?> ReadMediaSyncManifestAsync(
        StoredBackupDestination destination,
        Uri mediaUri,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            RemoteUri(mediaUri, MediaSyncManifestName));
        AddRemoteAuthorization(request, destination, UnprotectSecret(destination));
        var response = await clients.CreateClient("backup-offsite")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new IOException(
                    $"The {destination.Provider} media sync manifest could not be read ({(int)response.StatusCode}).");
            await using var content = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                var manifest = await JsonSerializer.DeserializeAsync<MediaSyncManifest>(
                    content, JsonOptions, ct);
                if (manifest is null ||
                    !string.Equals(manifest.Product, "LessonCue", StringComparison.Ordinal) ||
                    manifest.FormatVersion != 1 ||
                    manifest.Files is null ||
                    manifest.Files.Count > 100_000 ||
                    manifest.Files.Any(pair =>
                        !IsSafeMediaRelativePath(pair.Key) ||
                        pair.Value is null ||
                        pair.Value.Bytes < 0 || pair.Value.Sha256 is null ||
                        pair.Value.Sha256.Length != 64))
                    return null;
                return manifest;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static async Task<MediaSyncManifest> BuildLocalMediaManifestAsync(
        string localRoot,
        CancellationToken ct)
    {
        var files = new Dictionary<string, MediaSyncManifestFile>(StringComparer.Ordinal);
        if (!Directory.Exists(localRoot)) return new MediaSyncManifest(
            "LessonCue", 1, DateTimeOffset.UtcNow, files);

        foreach (var path in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(localRoot, path).Replace('\\', '/');
            if (!IsSafeMediaRelativePath(relativePath) ||
                relativePath.Equals("temporary", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("temporary/", StringComparison.OrdinalIgnoreCase))
                continue;
            var info = new FileInfo(path);
            var hash = await HashFileAsync(path, ct);
            files[relativePath] = new MediaSyncManifestFile(
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant());
        }
        return new MediaSyncManifest("LessonCue", 1, DateTimeOffset.UtcNow, files);
    }

    private async Task PutRemoteFileAsync(
        StoredBackupDestination destination,
        Uri target,
        string sourcePath,
        string relativePath,
        long fileBytes,
        string contentType,
        bool useOwnCloudChunking,
        CancellationToken ct)
    {
        if (useOwnCloudChunking && fileBytes > OwnCloudMediaChunkBytes)
        {
            await PutRemoteFileChunkedAsync(
                destination, target, sourcePath, relativePath, fileBytes, contentType, ct);
            return;
        }

        await SendRemoteMediaPutAsync(
            destination,
            target,
            relativePath,
            fileBytes,
            fileBytes,
            contentType,
            () => new StreamContent(File.OpenRead(sourcePath)),
            chunked: false,
            chunkIndex: null,
            chunkCount: null,
            ct);
    }

    private async Task<bool> OwnCloudSupportsChunkingAsync(
        StoredBackupDestination destination,
        Uri webDavUri,
        CancellationToken ct)
    {
        var path = webDavUri.AbsolutePath;
        var remotePhpIndex = path.IndexOf("/remote.php", StringComparison.OrdinalIgnoreCase);
        if (remotePhpIndex < 0)
        {
            logger.LogWarning(
                "Could not determine the ownCloud OCS capabilities endpoint for {Host}; using ordinary WebDAV media uploads.",
                webDavUri.Host);
            return false;
        }

        var installationPath = path[..(remotePhpIndex + 1)];
        var capabilityUri = new UriBuilder(webDavUri)
        {
            Path = $"{installationPath}ocs/v1.php/cloud/capabilities",
            Query = "format=json",
            Fragment = string.Empty
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, capabilityUri);
        request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddRemoteAuthorization(request, destination, UnprotectSecret(destination));
        using var response = await clients.CreateClient("backup-offsite")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Could not read ownCloud chunking capability for {Host} (HTTP {StatusCode}, CF-Ray {CloudflareRay}); using ordinary WebDAV media uploads.",
                webDavUri.Host,
                (int)response.StatusCode,
                HeaderValue(response, "CF-Ray"));
            return false;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("ocs", out var ocs) ||
            !ocs.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("capabilities", out var capabilities))
            return false;

        var supported = capabilities.TryGetProperty("files", out var files) &&
                        files.TryGetProperty("bigfilechunking", out var bigFileChunking) &&
                        IsJsonTrue(bigFileChunking);
        if (!supported)
            logger.LogWarning(
                "ownCloud at {Host} does not advertise files.bigfilechunking; large media will use ordinary WebDAV PUT.",
                webDavUri.Host);
        return supported;
    }

    private async Task PutRemoteFileChunkedAsync(
        StoredBackupDestination destination,
        Uri target,
        string sourcePath,
        string relativePath,
        long fileBytes,
        string contentType,
        CancellationToken ct)
    {
        var chunkCount = checked((int)Math.Ceiling(fileBytes / (double)OwnCloudMediaChunkBytes));
        var transferId = RandomNumberGenerator.GetInt32(1, int.MaxValue)
            .ToString(CultureInfo.InvariantCulture);
        var chunkUris = Enumerable.Range(0, chunkCount)
            .Select(index => new Uri(
                $"{target.AbsoluteUri}-chunking-{transferId}-{chunkCount}-{index.ToString(CultureInfo.InvariantCulture)}"))
            .ToArray();
        var attemptedChunks = new List<Uri>(chunkCount);

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                OwnCloudMediaChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (var index = 0; index < chunkCount; index++)
            {
                var length = (int)Math.Min(OwnCloudMediaChunkBytes, fileBytes - source.Position);
                var bytes = new byte[length];
                await source.ReadExactlyAsync(bytes, ct);
                var chunkUri = chunkUris[index];
                attemptedChunks.Add(chunkUri);
                await SendRemoteMediaPutAsync(
                    destination,
                    chunkUri,
                    relativePath,
                    fileBytes,
                    length,
                    contentType,
                    () => new ByteArrayContent(bytes),
                    chunked: true,
                    index + 1,
                    chunkCount,
                    ct);
            }
        }
        catch
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var chunkUri in attemptedChunks)
            {
                if (cleanupTimeout.IsCancellationRequested) break;
                try
                {
                    await DeleteRemotePathAsync(
                        destination, chunkUri, "incomplete ownCloud media-upload chunk", cleanupTimeout.Token);
                }
                catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested) { break; }
                catch (Exception cleanupError)
                {
                    logger.LogWarning(
                        cleanupError,
                        "Could not remove an incomplete ownCloud upload chunk for {MediaPath} ({FileBytes} bytes).",
                        relativePath,
                        fileBytes);
                }
            }
            throw;
        }
    }

    private async Task SendRemoteMediaPutAsync(
        StoredBackupDestination destination,
        Uri target,
        string relativePath,
        long fileBytes,
        long transferBytes,
        string contentType,
        Func<HttpContent> createContent,
        bool chunked,
        int? chunkIndex,
        int? chunkCount,
        CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        var client = clients.CreateClient("backup-offsite");
        for (var attempt = 1; attempt <= MediaUploadAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, target)
            {
                Content = createContent()
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Content.Headers.ContentLength = transferBytes;
            if (chunked) request.Headers.TryAddWithoutValidation("OC-Chunked", "1");
            AddRemoteAuthorization(request, destination, UnprotectSecret(destination));

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt < MediaUploadAttempts)
            {
                var delay = TimeSpan.FromSeconds(2 * attempt);
                logger.LogWarning(
                    ex,
                    "WebDAV media upload transport failed for {Provider} {MediaPath}, file {FileBytes} bytes, chunk {ChunkIndex}/{ChunkCount}; retry {Attempt}/{MaxAttempts} in {RetryDelaySeconds}s.",
                    destination.Provider,
                    relativePath,
                    fileBytes,
                    chunkIndex,
                    chunkCount,
                    attempt,
                    MediaUploadAttempts,
                    delay.TotalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new IOException(
                    $"WebDAV media upload transport failed. Provider={destination.Provider}; MediaPath={relativePath}; FileBytes={fileBytes}; TransferBytes={transferBytes}; Chunk={FormatChunk(chunkIndex, chunkCount)}; Attempts={attempt}; ElapsedMs={elapsed.ElapsedMilliseconds}; Error={ex.Message}",
                    ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode) return;

                var status = (int)response.StatusCode;
                var server = response.Headers.Server.ToString();
                var cloudflareRay = HeaderValue(response, "CF-Ray");
                var retryAfter = RetryAfter(response);
                var body = await ReadResponsePreviewAsync(response.Content, ct);
                var failure =
                    $"WebDAV media upload failed. Provider={destination.Provider}; MediaPath={relativePath}; FileBytes={fileBytes}; TransferBytes={transferBytes}; Chunk={FormatChunk(chunkIndex, chunkCount)}; HTTP={status}; Server={server}; CF-Ray={cloudflareRay}; RetryAfter={retryAfter?.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) ?? "none"}s; ElapsedMs={elapsed.ElapsedMilliseconds}; Response={body}";
                if (attempt < MediaUploadAttempts && IsTransientMediaUploadStatus(status))
                {
                    var delay = retryAfter ?? TimeSpan.FromSeconds(2 * attempt);
                    logger.LogWarning(
                        "{Failure}; retry {Attempt}/{MaxAttempts} in {RetryDelaySeconds}s.",
                        failure,
                        attempt,
                        MediaUploadAttempts,
                        delay.TotalSeconds);
                    response.Dispose();
                    await Task.Delay(delay, ct);
                    continue;
                }

                throw new IOException(failure);
            }
        }

        throw new IOException(
            $"WebDAV media upload failed after {MediaUploadAttempts} attempts. Provider={destination.Provider}; MediaPath={relativePath}; FileBytes={fileBytes}; TransferBytes={transferBytes}; Chunk={FormatChunk(chunkIndex, chunkCount)}; ElapsedMs={elapsed.ElapsedMilliseconds}.");
    }

    private static string FormatChunk(int? index, int? count) =>
        index is null || count is null ? "none" : $"{index}/{count}";

    private static bool IsOwnCloudProvider(string provider) =>
        string.Equals(provider, "owncloud", StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonTrue(JsonElement value) =>
        value.ValueKind == JsonValueKind.True ||
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransientMediaUploadStatus(int status) =>
        status is 408 or 429 || status >= 500;

    private static string HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : "none";

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter?.Delta;
        if (value is null && response.Headers.RetryAfter?.Date is { } retryDate)
            value = retryDate - DateTimeOffset.UtcNow;
        if (value is null || value < TimeSpan.Zero) return null;
        return value > MaximumRetryAfter ? MaximumRetryAfter : value;
    }

    private static async Task<string> ReadResponsePreviewAsync(
        HttpContent content,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[512];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct);
            if (read == 0) break;
            count += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, count)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private async Task PutRemoteContentAsync(
        StoredBackupDestination destination,
        Uri target,
        byte[] content,
        string contentType,
        string description,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, target)
        {
            Content = new ByteArrayContent(content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content.Headers.ContentLength = content.Length;
        AddRemoteAuthorization(request, destination, UnprotectSecret(destination));
        var response = await clients.CreateClient("backup-offsite")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new IOException(
                    $"The {destination.Provider} WebDAV target rejected the {description} ({(int)response.StatusCode}).");
        }
    }

    private async Task DeleteRemotePathAsync(
        StoredBackupDestination destination,
        Uri target,
        string description,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, target);
        AddRemoteAuthorization(request, destination, UnprotectSecret(destination));
        var response = await clients.CreateClient("backup-offsite").SendAsync(request, ct);
        using (response)
        {
            if (!response.IsSuccessStatusCode &&
                response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new IOException(
                    $"The {destination.Provider} WebDAV target rejected {description} ({(int)response.StatusCode}).");
        }
    }

    private string? UnprotectSecret(StoredBackupDestination destination) =>
        string.IsNullOrEmpty(destination.ProtectedSecret)
            ? null
            : protector.Unprotect(destination.ProtectedSecret);

    private static Uri RemoteUri(Uri baseUri, string relativePath)
    {
        var parts = relativePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var relative = string.Join('/', parts);
        if (relativePath.EndsWith("/", StringComparison.Ordinal)) relative += "/";
        return new Uri(baseUri, relative);
    }

    private static string? RelativeRemotePath(Uri baseUri, Uri candidate)
    {
        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != baseUri.Port ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
            return null;
        var basePath = baseUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? baseUri.AbsolutePath
            : baseUri.AbsolutePath + "/";
        if (!candidate.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal)) return null;
        var relative = Uri.UnescapeDataString(candidate.AbsolutePath[basePath.Length..]).Trim('/');
        return IsSafeMediaRelativePath(relative) || relative.Length == 0 ? relative : null;
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    private static bool IsSafeMediaRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith("/", StringComparison.Ordinal) &&
        !path.Contains('\\') &&
        !path.Split('/').Any(part => part is "" or "." or "..") &&
        !path.Equals(MediaSyncManifestName, StringComparison.Ordinal);

    private static string? CombineRemoteMediaPath(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(child) || child is "." or ".." ||
            child.Contains('/') || child.Contains('\\'))
            return null;
        var path = parent.Length == 0 ? child : $"{parent}/{child}";
        return IsSafeMediaRelativePath(path) ? path : null;
    }

    private async Task<int> PruneRemoteAsync(
        StoredBackupDestination destination,
        GoogleDriveBackupClient.GoogleDriveSession? driveSession,
        CancellationToken ct)
    {
        if (destination.Provider == "googledrive")
        {
            if (driveSession is null || string.IsNullOrEmpty(destination.GoogleDriveFolderId))
                throw new InvalidOperationException("Google Drive destination is not ready.");
            var driveFiles = (await driveSession.ListChildrenAsync(destination.GoogleDriveFolderId, ct))
                .Where(item => item.ManagedType == "backup" &&
                               item.MimeType != "application/vnd.google-apps.folder" &&
                               item.Name.StartsWith("lessoncue-", StringComparison.OrdinalIgnoreCase) &&
                               item.Name.EndsWith(".lcbak", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ModifiedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-destination.RetentionDays);
            var keepDrive = driveFiles.Take(destination.RetentionCount)
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in driveFiles)
            {
                if (keepDrive.Contains(item.Id) &&
                    (item.ModifiedAt is null || item.ModifiedAt >= cutoffDate))
                    continue;
                await driveSession.DeleteFileAsync(item.Id, ct);
            }
            return driveFiles.Count(item => keepDrive.Contains(item.Id) &&
                                            (item.ModifiedAt is null || item.ModifiedAt >= cutoffDate));
        }

        var baseUri = new Uri(destination.WebDavUrl!, UriKind.Absolute);
        using var request = new HttpRequestMessage(PropFindMethod, baseUri);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getlastmodified /></d:prop></d:propfind>",
            Encoding.UTF8,
            "application/xml");
        var secret = string.IsNullOrEmpty(destination.ProtectedSecret)
            ? null
            : protector.Unprotect(destination.ProtectedSecret);
        AddRemoteAuthorization(request, destination, secret);
        var response = await clients.CreateClient("backup-offsite")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        List<WebDavEntry> entries;
        using (response)
        {
            if ((int)response.StatusCode is not (200 or 207))
                throw new IOException(
                    $"The {destination.Provider} WebDAV folder could not be listed ({(int)response.StatusCode}).");
            await using var content = await response.Content.ReadAsStreamAsync(ct);
            entries = await ReadRemoteEntriesAsync(content, baseUri, ct);
        }
        var candidates = entries
            .Where(entry => entry.FileName.StartsWith("lessoncue-", StringComparison.OrdinalIgnoreCase) &&
                            entry.FileName.EndsWith(".lcbak", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.LastModified ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-destination.RetentionDays);
        var keep = candidates.Take(destination.RetentionCount)
            .Select(entry => entry.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in candidates)
        {
            if (keep.Contains(entry.FileName) &&
                (entry.LastModified is null || entry.LastModified >= cutoff))
                continue;
            await DeleteRemoteAsync(destination, baseUri, entry.FileName, ct);
        }
        return candidates.Count(entry => keep.Contains(entry.FileName) &&
                                         (entry.LastModified is null || entry.LastModified >= cutoff));
    }

    private async Task DeleteRemoteAsync(
        StoredBackupDestination destination,
        Uri baseUri,
        string fileName,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(baseUri, Uri.EscapeDataString(fileName)));
        var secret = string.IsNullOrEmpty(destination.ProtectedSecret)
            ? null
            : protector.Unprotect(destination.ProtectedSecret);
        AddRemoteAuthorization(request, destination, secret);
        var response = await clients.CreateClient("backup-offsite").SendAsync(request, ct);
        using (response)
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new IOException(
                    $"The {destination.Provider} WebDAV target rejected backup cleanup ({(int)response.StatusCode}).");
        }
    }

    private static void AddRemoteAuthorization(
        HttpRequestMessage request,
        StoredBackupDestination destination,
        string? secret)
    {
        if (destination.Authentication == "basic")
        {
            var raw = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{destination.Username}:{secret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        else if (destination.Authentication == "bearer")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    private static async Task<List<WebDavEntry>> ReadRemoteEntriesAsync(
        Stream content,
        Uri baseUri,
        CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000
        };
        using var reader = XmlReader.Create(content, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        return document.Descendants(DavNamespace + "response")
            .Select(response =>
            {
                var href = (string?)response.Element(DavNamespace + "href");
                if (string.IsNullOrWhiteSpace(href)) return null;
                Uri? uri = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                    ? absolute
                    : Uri.TryCreate(baseUri, href, out var relative) ? relative : null;
                if (uri is null) return null;
                if (!string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
                    uri.Port != baseUri.Port ||
                    !string.IsNullOrEmpty(uri.Query) ||
                    !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
                    return null;
                var relativePath = uri.AbsolutePath[baseUri.AbsolutePath.Length..].Trim('/');
                if (relativePath.Length == 0 || relativePath.Contains('/')) return null;
                var fileName = Uri.UnescapeDataString(relativePath);
                if (string.IsNullOrWhiteSpace(fileName)) return null;
                var modifiedValue = (string?)response
                    .Descendants(DavNamespace + "getlastmodified")
                    .FirstOrDefault();
                DateTimeOffset? modified = DateTimeOffset.TryParse(
                    modifiedValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                    ? parsed
                    : null;
                return new WebDavEntry(fileName, modified);
            })
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .GroupBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task PruneAsync(
        LessonCueDb db,
        StoredBackupPolicy policy,
        Guid currentId,
        CancellationToken ct)
    {
        var records = (await db.BackupRecords
            .Where(x => x.CreatedBy == "scheduled-backup")
            .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.RetentionDays);
        foreach (var record in records
                     .Where((record, index) =>
                         record.Id != currentId &&
                         (index >= policy.RetentionCount || record.CreatedAt < cutoff))
                     .ToList())
        {
            var path = backups.Resolve(record.FileName);
            if (path is not null) TryDelete(path);
            db.BackupRecords.Remove(record);
        }
    }

    private BackupPolicyStatus Public(StoredBackupPolicy policy, DateTimeOffset? next)
    {
        var mediaMode = ResolveMediaMode(policy);
        var interval = policy.Frequency == "weekly"
            ? TimeSpan.FromDays(8)
            : TimeSpan.FromHours(30);
        var overdue = policy.Enabled &&
                      (policy.LastSucceededAt is not null
                          ? policy.LastSucceededAt < DateTimeOffset.UtcNow - interval
                          : policy.EnabledAt is not null &&
                            policy.EnabledAt < DateTimeOffset.UtcNow - interval);
        var destinations = (policy.Destinations ?? []).Select(destination =>
            new BackupDestinationStatus(
                destination.Provider,
                destination.Provider == "googledrive"
                    ? !string.IsNullOrEmpty(destination.GoogleDriveOAuthClientId)
                    : !string.IsNullOrEmpty(destination.WebDavUrl),
                destination.WebDavRootUrl ?? destination.WebDavUrl,
                destination.Authentication,
                destination.Username,
                destination.Provider == "googledrive"
                    ? !string.IsNullOrEmpty(destination.ProtectedGoogleDriveRefreshToken)
                    : !string.IsNullOrEmpty(destination.ProtectedSecret),
                destination.RetentionCount,
                destination.RetentionDays,
                destination.LastUploadedAt,
                destination.LastUploadedFileName,
                destination.RemoteBackupCount,
                destination.LastError,
                destination.WebDavRootUrl ?? destination.WebDavUrl,
                destination.FolderName,
                destination.LastMediaSyncAt,
                destination.LastMediaSyncAdded,
                destination.LastMediaSyncUpdated,
                destination.LastMediaSyncDeleted,
                destination.GoogleDriveOAuthClientId,
                !string.IsNullOrEmpty(destination.ProtectedGoogleDriveRefreshToken),
                !string.IsNullOrEmpty(destination.ProtectedGoogleDriveOAuthClientSecret),
                destination.GoogleDriveReauthorizationRequired)).ToArray();
        var legacy = (policy.Destinations ?? []).FirstOrDefault(destination =>
            string.Equals(destination.Provider, "webdav", StringComparison.OrdinalIgnoreCase));
        return new BackupPolicyStatus(
            policy.Enabled,
            policy.Frequency,
            policy.HourLocal,
            policy.WeeklyDay,
            mediaMode == "backup",
            policy.RetentionCount,
            policy.RetentionDays,
            policy.SecretHandling,
            !string.IsNullOrEmpty(policy.ProtectedBackupPassword),
            legacy?.WebDavRootUrl ?? legacy?.WebDavUrl ?? policy.RemoteWebDavUrl,
            legacy?.Authentication ?? policy.RemoteAuthentication,
            legacy?.Username ?? policy.RemoteUsername,
            !string.IsNullOrEmpty(legacy?.ProtectedSecret ?? policy.ProtectedRemoteSecret),
            policy.LastAttemptAt,
            policy.LastSucceededAt,
            policy.LastVerifiedAt,
            policy.LastBackupFileName,
            policy.LastError,
            next,
            overdue,
            running,
            destinations,
            mediaMode);
    }

    private StoredBackupPolicy Read()
    {
        try
        {
            var policy = File.Exists(policyPath)
                ? JsonSerializer.Deserialize<StoredBackupPolicy>(
                      File.ReadAllText(policyPath), JsonOptions) ??
                  new StoredBackupPolicy()
                : new StoredBackupPolicy();
            if ((policy.Destinations is null || policy.Destinations.Count == 0) &&
                !string.IsNullOrWhiteSpace(policy.RemoteWebDavUrl))
            {
                policy = policy with
                {
                    Destinations =
                    [
                        new StoredBackupDestination
                        {
                            Provider = "webdav",
                            WebDavRootUrl = policy.RemoteWebDavUrl,
                            WebDavUrl = policy.RemoteWebDavUrl,
                            Authentication = policy.RemoteAuthentication,
                            Username = policy.RemoteUsername,
                            ProtectedSecret = policy.ProtectedRemoteSecret,
                            RetentionCount = policy.RetentionCount,
                            RetentionDays = policy.RetentionDays
                        }
                    ]
                };
            }
            return policy;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LessonCue could not read the scheduled-backup policy");
            return new StoredBackupPolicy
            {
                LastError = "The scheduled-backup policy is unreadable. Save it again."
            };
        }
    }

    private async Task WriteAsync(StoredBackupPolicy policy, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        var temporary = $"{policyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary, JsonSerializer.Serialize(policy, JsonOptions), ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, policyPath, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static DateTimeOffset? Schedule(
        StoredBackupPolicy policy,
        string timeZone,
        DateTimeOffset now)
    {
        var latest = LatestBoundary(policy, timeZone, now);
        if (policy.LastSucceededAt is null || policy.LastSucceededAt < latest)
            return latest;
        return policy.Frequency == "weekly"
            ? latest.AddDays(7)
            : latest.AddDays(1);
    }

    private static DateTimeOffset LatestBoundary(
        StoredBackupPolicy policy,
        string timeZone,
        DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch { zone = TimeZoneInfo.Utc; }
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var date = DateOnly.FromDateTime(localNow.Date);
        if (policy.Frequency == "weekly")
        {
            var target = (DayOfWeek)(policy.WeeklyDay ?? 0);
            var daysBack = ((int)localNow.DayOfWeek - (int)target + 7) % 7;
            date = date.AddDays(-daysBack);
        }
        var boundary = LocalBoundary(date, policy.HourLocal, zone);
        if (boundary > now)
            boundary = LocalBoundary(
                date.AddDays(policy.Frequency == "weekly" ? -7 : -1),
                policy.HourLocal,
                zone);
        return boundary;
    }

    private static DateTimeOffset LocalBoundary(
        DateOnly date,
        int hour,
        TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        var offsets = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local)
            : [zone.GetUtcOffset(local)];
        return new DateTimeOffset(local, offsets.Max()).ToUniversalTime();
    }

    private static string NormalizeRemoteUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(
                "Enter an HTTPS WebDAV folder URL without a query or fragment.");
        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri.AbsoluteUri;
    }

    private static string? NormalizeFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var folder = value.Trim();
        if (folder.Length > 128 || folder is "." or ".." ||
            folder.Contains('/') || folder.Contains('\\') ||
            folder.Any(char.IsControl))
            throw new ArgumentException(
                "Folder names must be one safe path segment of 1–128 characters.");
        return folder;
    }

    private static string AppendRemoteFolder(string rootUrl, string? folderName) =>
        folderName is null
            ? rootUrl
            : RemoteUri(new Uri(rootUrl, UriKind.Absolute), folderName + "/").AbsoluteUri;

    private static string NormalizeMediaMode(string? value, bool includeMedia)
    {
        var mode = string.IsNullOrWhiteSpace(value)
            ? includeMedia ? "backup" : "exclude"
            : value.Trim().ToLowerInvariant();
        return mode is "backup" or "sync" or "exclude"
            ? mode
            : throw new ArgumentException(
                "Choose whether media is included in backups, synced to WebDAV, or excluded.");
    }

    private static string ResolveMediaMode(StoredBackupPolicy policy) =>
        NormalizeMediaMode(policy.MediaMode, policy.IncludeMedia);

    private static bool HasGoogleDriveReauthorizationRequired(StoredBackupPolicy policy) =>
        (policy.Destinations ?? []).Any(destination =>
            destination.Provider == "googledrive" && destination.GoogleDriveReauthorizationRequired);

    private static string NormalizeProvider(string value)
    {
        var provider = value.Trim().ToLowerInvariant();
        return provider is "nextcloud" or "owncloud" or "webdav" or "googledrive"
            ? provider
            : throw new ArgumentException("Choose Google Drive, Nextcloud, ownCloud, or another WebDAV destination.");
    }

    private static IReadOnlyList<BackupDestinationInput> LegacyDestination(BackupPolicyInput input) =>
        string.IsNullOrWhiteSpace(input.RemoteWebDavUrl)
            ? []
            :
            [
                new BackupDestinationInput(
                    "webdav",
                    input.RemoteWebDavUrl,
                    input.RemoteAuthentication,
                    input.RemoteUsername,
                    input.RemoteSecret,
                    input.RetentionCount,
                    input.RetentionDays)
            ];

    private static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private sealed record StoredBackupPolicy
    {
        public bool Enabled { get; init; }
        public DateTimeOffset? EnabledAt { get; init; }
        public string Frequency { get; init; } = "daily";
        public int HourLocal { get; init; } = 2;
        public int? WeeklyDay { get; init; }
        public bool IncludeMedia { get; init; } = true;
        public string? MediaMode { get; init; }
        public int RetentionCount { get; init; } = 7;
        public int RetentionDays { get; init; } = 30;
        public string SecretHandling { get; init; } = "exclude";
        public string? ProtectedBackupPassword { get; init; }
        public string? RemoteWebDavUrl { get; init; }
        public string RemoteAuthentication { get; init; } = "none";
        public string? RemoteUsername { get; init; }
        public string? ProtectedRemoteSecret { get; init; }
        public List<StoredBackupDestination> Destinations { get; init; } = [];
        public DateTimeOffset? LastAttemptAt { get; init; }
        public DateTimeOffset? LastSucceededAt { get; init; }
        public DateTimeOffset? LastVerifiedAt { get; init; }
        public string? LastBackupFileName { get; init; }
        public string? LastError { get; init; }
    }

    private sealed record StoredBackupDestination
    {
        public string Provider { get; init; } = "webdav";
        public string? WebDavRootUrl { get; init; }
        public string? WebDavUrl { get; init; }
        public string? FolderName { get; init; }
        public string Authentication { get; init; } = "none";
        public string? Username { get; init; }
        public string? ProtectedSecret { get; init; }
        public string? GoogleDriveOAuthClientId { get; init; }
        public string? ProtectedGoogleDriveOAuthClientSecret { get; init; }
        public string? ProtectedGoogleDriveRefreshToken { get; init; }
        public string? GoogleDriveOAuthStateProtected { get; init; }
        public DateTimeOffset? GoogleDriveOAuthStateExpiresAt { get; init; }
        public string? GoogleDriveOAuthRedirectUri { get; init; }
        public string? GoogleDriveFolderId { get; init; }
        public bool GoogleDriveReauthorizationRequired { get; init; }
        public int RetentionCount { get; init; } = 7;
        public int RetentionDays { get; init; } = 30;
        public DateTimeOffset? LastUploadedAt { get; init; }
        public string? LastUploadedFileName { get; init; }
        public int? RemoteBackupCount { get; init; }
        public string? LastError { get; init; }
        public DateTimeOffset? LastMediaSyncAt { get; init; }
        public int? LastMediaSyncAdded { get; init; }
        public int? LastMediaSyncUpdated { get; init; }
        public int? LastMediaSyncDeleted { get; init; }
    }

    private sealed record WebDavEntry(string FileName, DateTimeOffset? LastModified);
    private sealed record MediaSyncManifest(
        string Product,
        int FormatVersion,
        DateTimeOffset GeneratedAt,
        Dictionary<string, MediaSyncManifestFile> Files);
    private sealed record MediaSyncManifestFile(long Bytes, string Sha256);
    private sealed record RemoteMediaFile(string RelativePath, long? Bytes);
    private sealed record RemoteMediaState(
        Dictionary<string, RemoteMediaFile> Files,
        HashSet<string> Directories);
    private sealed record GoogleDriveMediaState(
        Dictionary<string, GoogleDriveItem> Files,
        Dictionary<string, string> Directories,
        string? ManifestId);
    private sealed record RemoteResource(
        string RelativePath,
        Uri Uri,
        bool IsCollection,
        long? Bytes);
    private sealed record MediaSyncResult(
        DateTimeOffset CompletedAt,
        int Added,
        int Updated,
        int Deleted);
}
