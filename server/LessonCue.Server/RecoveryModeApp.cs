using System.Net;

namespace LessonCue.Server;

public static class RecoveryModeApp
{
    public static async Task RunAsync(
        string[] args,
        int port,
        string dataPath,
        string serverId,
        Exception startupFailure)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var useDefaultHttpBinding = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (useDefaultHttpBinding) options.ListenAnyIP(port);
        });
        var app = builder.Build();
        app.Logger.LogCritical(
            startupFailure,
            "LessonCue entered database recovery mode; normal application routes are disabled");

        var status = BuildStatus(dataPath, serverId, startupFailure);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            await next();
        });
        app.MapGet("/health/live", () => Results.Ok(new
        {
            status = "alive",
            safeMode = true,
            serverId
        }));
        app.MapGet("/health/ready", () => Results.Json(
            status,
            statusCode: StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/health", () => Results.Json(
            status,
            statusCode: StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/recovery/status", () => Results.Json(
            status,
            statusCode: StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/.well-known/lessoncue", () => new
        {
            product = "LessonCue",
            serverId,
            apiVersion = 1,
            safeMode = true,
            ready = false
        });
        app.MapGet("/", () => Results.Content(
            RecoveryPage(status),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable));
        app.MapFallback(() => Results.Redirect("/"));
        await app.RunAsync();
    }

    public static RecoveryModeStatus BuildStatus(
        string dataPath,
        string serverId,
        Exception failure)
    {
        var backupPath = Path.Combine(dataPath, "backups");
        string[] backups;
        try
        {
            backups = Directory.Exists(backupPath)
                ? Directory.EnumerateFiles(backupPath)
                    .Where(path =>
                        Path.GetExtension(path).Equals(
                            ".lcbak", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(path).Equals(
                            ".zip", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null)
                    .Cast<string>()
                    .Take(10)
                    .ToArray()
                : [];
        }
        catch
        {
            backups = [];
        }

        return new RecoveryModeStatus(
            "recovery",
            false,
            true,
            serverId,
            failure is Microsoft.Data.Sqlite.SqliteException
                ? "database"
                : "startup",
            "LessonCue could not open or upgrade its database. Normal routes and background work are disabled to prevent further changes.",
            backups.Length,
            backups.FirstOrDefault(),
            [
                "Keep the server and /var/lib/lessoncue data directory intact.",
                "Inspect journalctl -u lessoncue and verify available disk space.",
                "If an update was interrupted, run the protected update-recovery service.",
                "Restore only a verified backup using the same or a newer LessonCue release."
            ]);
    }

    private static string RecoveryPage(RecoveryModeStatus status)
    {
        var latest = string.IsNullOrEmpty(status.LatestBackupFileName)
            ? "No local LessonCue backup was discovered."
            : $"Newest local backup: <strong>{WebUtility.HtmlEncode(status.LatestBackupFileName)}</strong>";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>LessonCue recovery mode</title>
              <style>
                :root { color-scheme: light; font-family: system-ui,sans-serif; background:#f4f1e8; color:#182a26; }
                body { margin:0; min-height:100vh; display:grid; place-items:center; padding:24px; box-sizing:border-box; }
                main { width:min(720px,100%); padding:32px; border:1px solid #d9caa7; border-radius:16px; background:#fff; box-shadow:0 18px 60px #1b332b1f; }
                .mark { display:grid; place-items:center; width:54px; height:54px; border-radius:14px; background:#df941b; color:#fff; font-size:28px; font-weight:800; }
                h1 { margin:18px 0 8px; font-family:Georgia,serif; font-size:36px; }
                p,li { line-height:1.55; } .muted { color:#5b6e68; }
                pre { overflow:auto; padding:16px; border-radius:10px; background:#12241f; color:#e9f2ee; }
                a { color:#17684f; font-weight:700; }
              </style>
            </head>
            <body><main>
              <div class="mark">!</div>
              <h1>LessonCue is protecting your data</h1>
              <p>LessonCue could not safely open or upgrade its database, so it started a read-only recovery page instead of the normal application.</p>
              <p class="muted">{{latest}} Local backups found: {{status.LocalBackupCount}}.</p>
              <ol>
                <li>Do not delete or replace <code>/var/lib/lessoncue</code>.</li>
                <li>From SSH, inspect the service and disk before attempting recovery:</li>
              </ol>
              <pre>sudo systemctl status lessoncue --no-pager
              sudo journalctl -u lessoncue -n 200 --no-pager
              df -h /var/lib/lessoncue
              sudo systemctl start lessoncue-update-recovery.service</pre>
              <p>Use a verified backup with the same or a newer LessonCue version. See the <a href="https://github.com/ProbablyModded/lessoncue/blob/main/docs/troubleshooting.md#lessoncue-started-in-recovery-mode">recovery instructions</a> from another device.</p>
            </main></body></html>
            """;
    }
}

public sealed record RecoveryModeStatus(
    string Status,
    bool Ready,
    bool SafeMode,
    string ServerId,
    string FailureArea,
    string Message,
    int LocalBackupCount,
    string? LatestBackupFileName,
    string[] NextSteps);
