using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace LessonCue.Server;

public sealed record LessonCueUpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    DateTimeOffset? LastCheckedAt,
    string? ReleaseUrl,
    string? ReleaseNotes,
    string? Error,
    bool AutomaticInstallSupported,
    bool Installing,
    bool? LastInstallSucceeded,
    DateTimeOffset? LastInstallAt,
    string? LastInstallVersion,
    string? LastInstallMessage,
    bool RollbackSnapshotAvailable,
    string? RollbackTargetVersion);

public sealed record LessonCueUpdateResult(
    bool Success,
    string? Version,
    string? Message,
    DateTimeOffset? CompletedAt);

public sealed record LessonCueUpdateOperationResult(
    bool Success,
    string Message,
    string? FailureCode = null);

public sealed class UpdateService(
    IHttpClientFactory clients,
    ILogger<UpdateService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private LessonCueUpdateStatus _status = InitialStatus();

    public LessonCueUpdateStatus Status
    {
        get
        {
            RefreshInstallResult();
            return _status;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(false, stoppingToken);
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    public async Task<LessonCueUpdateStatus> CheckAsync(bool force, CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct);
        try
        {
            if (!force && _status.LastCheckedAt is not null && DateTimeOffset.UtcNow - _status.LastCheckedAt < TimeSpan.FromHours(23))
                return _status;
            try
            {
                var client = clients.CreateClient("updates");
                using var response = await client.GetAsync("https://api.github.com/repos/ProbablyModded/lessoncue/releases/latest", ct);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = document.RootElement;
                var tag = root.GetProperty("tag_name").GetString();
                var latest = tag?.TrimStart('v', 'V');
                var releaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
                var releaseNotes = root.TryGetProperty("body", out var body)
                    ? UserReleaseNotes(body.GetString())
                    : null;
                _status = _status with
                {
                    LatestVersion = latest,
                    UpdateAvailable = IsNewer(latest, _status.CurrentVersion),
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    ReleaseUrl = releaseUrl,
                    ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes,
                    Error = null,
                    AutomaticInstallSupported = AutomaticInstallSupported(),
                    RollbackSnapshotAvailable = RollbackSnapshotAvailable()
                };
                logger.LogInformation(
                    "LessonCue update check completed. Current version {CurrentVersion}; latest version {LatestVersion}; update available {UpdateAvailable}",
                    _status.CurrentVersion, _status.LatestVersion ?? "unknown", _status.UpdateAvailable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not check for a LessonCue update");
                _status = _status with { LastCheckedAt = DateTimeOffset.UtcNow, Error = "The update service could not be reached." };
            }
            return _status;
        }
        finally { _checkGate.Release(); }
    }

    public async Task<LessonCueUpdateOperationResult> StartInstallAsync(CancellationToken ct = default)
    {
        RefreshInstallResult();
        return await SignalProtectedOperationAsync(
            BuildUpdateRequest(_status.LatestVersion, DateTimeOffset.UtcNow),
            "update",
            ct);
    }

    public async Task<LessonCueUpdateOperationResult> StartRollbackAsync(CancellationToken ct = default)
    {
        RefreshInstallResult();
        if (!RollbackSnapshotAvailable())
        {
            const string message = "No verified last-known-good update snapshot is available.";
            logger.LogWarning("Protected rollback rejected: {Message}", message);
            return new LessonCueUpdateOperationResult(false, message, "rollback-snapshot-unavailable");
        }
        return await SignalProtectedOperationAsync(
            $"rollback:{DateTimeOffset.UtcNow:O}",
            "rollback",
            ct);
    }

    public static bool IsNewer(string? candidate, string current) =>
        Version.TryParse(candidate, out var latest) && Version.TryParse(current, out var installed) && latest > installed;

    public static string BuildUpdateRequest(string? targetVersion, DateTimeOffset requestedAt)
    {
        var normalized = targetVersion?.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out _)
            ? $"update:v{normalized}:{requestedAt:O}"
            : $"update:{requestedAt:O}";
    }

    public static string InstalledVersion() =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    private static string? UserReleaseNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        const string heading = "### User changes";
        var start = body.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return body.Trim();

        start += heading.Length;
        while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
        var end = body.IndexOf("\n### ", start, StringComparison.OrdinalIgnoreCase);
        var notes = (end >= 0 ? body[start..end] : body[start..]).Trim();
        return string.IsNullOrWhiteSpace(notes) ? null : notes;
    }

    private const string UpdateRequestPath = "/var/lib/lessoncue/config/update-request";
    private const string UpdateResultPath = "/var/lib/lessoncue/config/update-result.json";
    private const string UpdateTransactionPath = "/var/lib/lessoncue/update-transaction";
    private const string RollbackSnapshotPath = "/var/lib/lessoncue/update-rollback";

    private static bool AutomaticInstallSupported() => AutomaticInstallSupportError() is null;

    private static string? AutomaticInstallSupportError()
    {
        if (!OperatingSystem.IsLinux()) return "Protected server updates are available only on the native Linux installation.";
        if (!Directory.Exists(Path.GetDirectoryName(UpdateRequestPath)))
            return "The LessonCue configuration directory is missing.";
        if (!File.Exists("/etc/systemd/system/lessoncue-update.service") ||
            !File.Exists("/etc/systemd/system/lessoncue-update.path"))
            return "The protected LessonCue update service is not installed. Run the current Linux installer once.";
        return null;
    }

    private static bool RollbackSnapshotAvailable() =>
        OperatingSystem.IsLinux() &&
        Directory.Exists(RollbackSnapshotPath) &&
        Directory.Exists("/opt/lessoncue.previous") &&
        File.Exists(Path.Combine(RollbackSnapshotPath, "data", "database", "lessoncue.db"));

    private static LessonCueUpdateStatus InitialStatus()
    {
        var result = ReadInstallResult();
        return new LessonCueUpdateStatus(
            InstalledVersion(), null, false, null, null, null,
            result is { Success: false } ? result.Message : null,
            AutomaticInstallSupported(), false,
            result?.Success, result?.CompletedAt, result?.Version, result?.Message,
            RollbackSnapshotAvailable(), RollbackTargetVersion());
    }

    private void RefreshInstallResult()
    {
        var result = ReadInstallResult();
        if (result is null)
        {
            if (_status.Installing &&
                !File.Exists(UpdateRequestPath) &&
                !File.Exists(UpdateTransactionPath) &&
                !ProtectedUpdaterActive())
            {
                const string message = "The previous protected LessonCue operation stopped before recording a result. It is safe to try again.";
                logger.LogWarning("Clearing stale protected operation state: {Message}", message);
                _status = _status with { Installing = false, Error = message };
            }
            _status = _status with
            {
                AutomaticInstallSupported = AutomaticInstallSupported(),
                RollbackSnapshotAvailable = RollbackSnapshotAvailable(),
                RollbackTargetVersion = RollbackTargetVersion()
            };
            return;
        }

        _status = _status with
        {
            Installing = false,
            LastInstallSucceeded = result.Success,
            LastInstallAt = result.CompletedAt,
            LastInstallVersion = result.Version,
            LastInstallMessage = result.Message,
            Error = result.Success ? _status.Error : result.Message,
            AutomaticInstallSupported = AutomaticInstallSupported(),
            RollbackSnapshotAvailable = RollbackSnapshotAvailable(),
            RollbackTargetVersion = RollbackTargetVersion()
        };
    }

    private static bool ProtectedUpdaterActive()
    {
        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/systemctl",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("is-active");
            process.StartInfo.ArgumentList.Add("--quiet");
            process.StartInfo.ArgumentList.Add("lessoncue-update.service");
            if (!process.Start()) return true;
            if (!process.WaitForExit(1000))
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            // If the service state cannot be read, keep the lock fail-closed.
            return true;
        }
    }

    private async Task<LessonCueUpdateOperationResult> SignalProtectedOperationAsync(
        string request,
        string operation,
        CancellationToken ct)
    {
        if (!await _operationGate.WaitAsync(0, ct))
        {
            const string message = "Another protected LessonCue operation is already in progress.";
            logger.LogWarning("Protected {Operation} rejected: {Message}", operation, message);
            return new LessonCueUpdateOperationResult(false, message, "operation-in-progress");
        }

        try
        {
            RefreshInstallResult();
            if (_status.Installing)
            {
                const string message = "Another protected LessonCue operation is already in progress.";
                logger.LogWarning("Protected {Operation} rejected: {Message}", operation, message);
                return new LessonCueUpdateOperationResult(false, message, "operation-in-progress");
            }

            var supportError = AutomaticInstallSupportError();
            if (supportError is not null)
            {
                logger.LogWarning("Protected {Operation} rejected: {Reason}", operation, supportError);
                return new LessonCueUpdateOperationResult(false, supportError, "protected-operation-unavailable");
            }

            if (File.Exists(UpdateResultPath))
            {
                File.Delete(UpdateResultPath);
            }

            await File.WriteAllTextAsync(UpdateRequestPath, request, ct);
            _status = _status with { Installing = true, Error = null };
            logger.LogInformation("Queued protected LessonCue {Operation} in {RequestPath}", operation, UpdateRequestPath);
            return new LessonCueUpdateOperationResult(true, $"The protected LessonCue {operation} has been queued.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"LessonCue could not queue the protected {operation}. Run the current Linux installer once to repair updater permissions, then try again.";
            logger.LogError(ex, "Could not queue protected LessonCue {Operation} in {RequestPath}", operation, UpdateRequestPath);
            _status = _status with { Error = message };
            return new LessonCueUpdateOperationResult(false, message, "request-file-write-failed");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string? RollbackTargetVersion()
    {
        var path = Path.Combine(RollbackSnapshotPath, "from-version");
        if (!File.Exists(path)) return null;
        try
        {
            var value = File.ReadAllText(path).Trim().TrimStart('v', 'V');
            return string.IsNullOrWhiteSpace(value) || value == "unknown" ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static LessonCueUpdateResult? ReadInstallResult()
    {
        if (!File.Exists(UpdateResultPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<LessonCueUpdateResult>(
                File.ReadAllText(UpdateResultPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
