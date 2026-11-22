using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LessonCue.Server;

/// <summary>
/// The independently managed YouTube downloader runtime. The web service only
/// queues a request; the root-owned systemd command below performs the
/// download, verification, and atomic replacement.
/// </summary>
public sealed record YouTubeRuntimeUpdateStatus(
    bool Supported,
    bool AutomaticUpdatesEnabled,
    string? InstalledVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    bool UpdateNoticeVisible,
    int ConsecutiveFailures,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastUpdatedAt,
    bool? LastUpdateSucceeded,
    string? LastUpdateMessage,
    string? Error,
    bool OperationPending);

public sealed record YouTubeRuntimeOperationResult(
    bool Success,
    bool Queued,
    string Message,
    string? FailureCode,
    YouTubeRuntimeUpdateStatus Status);

public sealed record YouTubeRuntimeHealthState(
    int ConsecutiveFailures,
    DateTimeOffset? LastFailureAt,
    string? LastFailureMessage,
    DateTimeOffset? LastSuccessAt);

internal sealed record YouTubeRuntimeCommandResult(
    string Operation,
    bool Success,
    string? InstalledVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? Message,
    string? FailureCode,
    DateTimeOffset CompletedAt,
    DateTimeOffset? LastUpdatedAt,
    bool? LastUpdateSucceeded,
    string? LastUpdateMessage);

internal sealed record YouTubeReleaseAsset(
    string Version,
    string Name,
    Uri DownloadUri,
    string Sha256);

public sealed class YouTubeRuntimeUpdateService(
    string dataPath,
    ILogger<YouTubeRuntimeUpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan BackgroundRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly string _configPath = Path.Combine(dataPath, "config");
    private readonly string _requestPath = Path.Combine(dataPath, "config", "ytdlp-update-request");
    private readonly string _resultPath = Path.Combine(dataPath, "config", "ytdlp-update-result.json");
    private readonly string _healthPath = Path.Combine(dataPath, "config", "ytdlp-runtime-health.json");
    private YouTubeRuntimeUpdateStatus _status = InitialStatus(dataPath);

    public YouTubeRuntimeUpdateStatus Status
    {
        get
        {
            RefreshStatus();
            return _status;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                RefreshStatus();
                // The native Linux timer normally performs this work. This
                // fallback keeps the feature self-healing after a repaired or
                // partially upgraded installation, without creating an update
                // notice in the UI.
                if (_status.Supported &&
                    !_status.OperationPending &&
                    (_status.LastCheckedAt is null || DateTimeOffset.UtcNow - _status.LastCheckedAt >= AutomaticCheckInterval))
                {
                    await QueueAsync("update", stoppingToken);
                }
                await Task.Delay(BackgroundRefreshInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<YouTubeRuntimeOperationResult> QueueCheckAsync(CancellationToken ct = default) =>
        await QueueAsync("check", ct);

    public async Task<YouTubeRuntimeOperationResult> QueueUpdateAsync(CancellationToken ct = default) =>
        await QueueAsync("update", ct);

    public async Task<YouTubeRuntimeOperationResult> QueueUpdateAndWaitAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var before = Status;
        var queued = await QueueUpdateAsync(ct);
        if (!queued.Success || !queued.Queued) return queued;

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var status = Status;
            if (status.LastCheckedAt is not { } completedAt ||
                before.LastCheckedAt is { } previousAt && completedAt <= previousAt)
                continue;

            var success = status.Error is null;
            return new YouTubeRuntimeOperationResult(
                success,
                true,
                success
                    ? status.LastUpdateMessage ?? "The yt-dlp update check completed."
                    : status.Error!,
                success ? null : "runtime-update-failed",
                status);
        }

        var finalStatus = Status;
        return new YouTubeRuntimeOperationResult(
            false,
            true,
            "The yt-dlp update did not finish before the automatic retry window expired.",
            "operation-timeout",
            finalStatus);
    }

    public void RecordYouTubeFailure(Exception error)
    {
        var current = ReadHealth();
        var message = error.Message.Length > 700 ? error.Message[..700] : error.Message;
        var next = current with
        {
            ConsecutiveFailures = Math.Min(current.ConsecutiveFailures + 1, 100),
            LastFailureAt = DateTimeOffset.UtcNow,
            LastFailureMessage = message
        };
        WriteHealth(next);
        RefreshStatus();
        logger.LogWarning("YouTube runtime health recorded a failed download. Consecutive failures: {Failures}",
            next.ConsecutiveFailures);
    }

    public void RecordYouTubeSuccess()
    {
        var current = ReadHealth();
        if (current.ConsecutiveFailures == 0 && current.LastFailureMessage is null) return;
        WriteHealth(current with
        {
            ConsecutiveFailures = 0,
            LastFailureMessage = null,
            LastSuccessAt = DateTimeOffset.UtcNow
        });
        RefreshStatus();
    }

    internal static bool ShouldShowNotice(bool updateAvailable, int consecutiveFailures) =>
        updateAvailable && consecutiveFailures > 0;

    internal static string? ExpectedAssetName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "yt-dlp_linux",
        Architecture.Arm64 => "yt-dlp_linux_aarch64",
        _ => null
    };

    private async Task<YouTubeRuntimeOperationResult> QueueAsync(string operation, CancellationToken ct)
    {
        RefreshStatus();
        var supportError = SupportError();
        if (supportError is not null)
        {
            return new YouTubeRuntimeOperationResult(false, false, supportError, "unsupported", _status);
        }

        if (!await _requestGate.WaitAsync(0, ct))
        {
            return new YouTubeRuntimeOperationResult(false, false,
                "A yt-dlp update operation is already being queued.", "operation-in-progress", _status);
        }

        try
        {
            if (File.Exists(_requestPath))
            {
                _status = _status with { OperationPending = true };
                return new YouTubeRuntimeOperationResult(true, true,
                    "The yt-dlp operation is already queued.", null, _status);
            }

            Directory.CreateDirectory(_configPath);
            var temporary = _requestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, operation + "\n", ct);
            File.Move(temporary, _requestPath);
            _status = _status with { OperationPending = true, Error = null };
            logger.LogInformation("Queued independent yt-dlp {Operation} through {RequestPath}", operation, _requestPath);
            return new YouTubeRuntimeOperationResult(true, true,
                operation == "check"
                    ? "A yt-dlp update check has been queued."
                    : "The yt-dlp update has been queued.", null, _status);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            const string message = "LessonCue could not queue the independent yt-dlp operation. Run the current Linux installer once to repair the updater services.";
            logger.LogError(error, "Could not queue independent yt-dlp {Operation}", operation);
            _status = _status with { Error = message };
            return new YouTubeRuntimeOperationResult(false, false, message, "request-file-write-failed", _status);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void RefreshStatus()
    {
        var installedVersion = YouTubeRuntime.TryReadVersion(BundledPath());
        var result = ReadCommandResult();
        var health = ReadHealth();
        var supported = SupportError() is null;
        var automatic = supported && File.Exists("/etc/systemd/system/lessoncue-ytdlp-update.timer");
        var updateAvailable = result?.UpdateAvailable == true ||
            YouTubeRuntime.IsNewer(result?.LatestVersion, installedVersion);
        _status = new YouTubeRuntimeUpdateStatus(
            supported,
            automatic,
            installedVersion ?? result?.InstalledVersion,
            result?.LatestVersion,
            updateAvailable,
            ShouldShowNotice(updateAvailable, health.ConsecutiveFailures),
            health.ConsecutiveFailures,
            health.LastFailureAt,
            result?.CompletedAt,
            result?.LastUpdatedAt,
            result?.LastUpdateSucceeded,
            result?.LastUpdateMessage,
            result is { Success: false } ? result.Message : null,
            File.Exists(_requestPath));
    }

    private string? SupportError()
    {
        if (!OperatingSystem.IsLinux()) return "Independent yt-dlp updates are available on the native Linux server installation.";
        if (ExpectedAssetName(RuntimeInformation.ProcessArchitecture) is null)
            return $"Independent yt-dlp updates do not support this server architecture ({RuntimeInformation.ProcessArchitecture}).";
        if (!File.Exists(BundledPath())) return "The bundled yt-dlp executable is missing from the LessonCue installation.";
        if (!File.Exists("/etc/systemd/system/lessoncue-ytdlp-update.service") ||
            !File.Exists("/etc/systemd/system/lessoncue-ytdlp-update.path"))
            return "The independent yt-dlp updater is not installed. Run the current Linux installer once.";
        return null;
    }

    private string BundledPath() => Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    private YouTubeRuntimeCommandResult? ReadCommandResult()
    {
        try
        {
            if (!File.Exists(_resultPath)) return null;
            return JsonSerializer.Deserialize<YouTubeRuntimeCommandResult>(
                File.ReadAllText(_resultPath), YouTubeRuntimeJson.Options);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug(error, "Could not read yt-dlp update result");
            return null;
        }
    }

    private YouTubeRuntimeHealthState ReadHealth()
    {
        try
        {
            if (!File.Exists(_healthPath)) return new YouTubeRuntimeHealthState(0, null, null, null);
            return JsonSerializer.Deserialize<YouTubeRuntimeHealthState>(
                File.ReadAllText(_healthPath), YouTubeRuntimeJson.Options)
                ?? new YouTubeRuntimeHealthState(0, null, null, null);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug(error, "Could not read yt-dlp runtime health");
            return new YouTubeRuntimeHealthState(0, null, null, null);
        }
    }

    private void WriteHealth(YouTubeRuntimeHealthState state)
    {
        try
        {
            Directory.CreateDirectory(_configPath);
            var temporary = _healthPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, YouTubeRuntimeJson.Options));
            File.Move(temporary, _healthPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not persist yt-dlp runtime health");
        }
    }

    private static YouTubeRuntimeUpdateStatus InitialStatus(string dataPath)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        string? installed = null;
        try { installed = YouTubeRuntime.TryReadVersion(bundledPath); }
        catch { /* status remains available even when the runtime is broken */ }
        return new YouTubeRuntimeUpdateStatus(
            OperatingSystem.IsLinux() && File.Exists(bundledPath),
            OperatingSystem.IsLinux() && File.Exists("/etc/systemd/system/lessoncue-ytdlp-update.timer"),
            installed, null, false, false, 0, null, null, null, null, null, null,
            File.Exists(Path.Combine(dataPath, "config", "ytdlp-update-request")));
    }
}

internal static class YouTubeRuntimeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

/// <summary>
/// This command is intentionally invoked by a root-owned systemd unit. It is
/// not reachable over HTTP and only manages the bundled yt-dlp executable.
/// </summary>
public static class YouTubeRuntimeUpdateCommand
{
    private const string RepositoryApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const long MaximumDownloadBytes = 128L * 1024 * 1024;

    public static async Task<int> RunAsync(string dataPath, CancellationToken ct = default)
    {
        var configPath = Path.Combine(dataPath, "config");
        var requestPath = Path.Combine(configPath, "ytdlp-update-request");
        var resultPath = Path.Combine(configPath, "ytdlp-update-result.json");
        var operation = ReadOperation(requestPath);
        var previous = ReadResult(resultPath);
        try
        {
            var target = BundledPath();
            var supportError = SupportError(target);
            if (supportError is not null)
                return await WriteResultAsync(resultPath, Failure(operation, supportError, "unsupported", previous), ct);

            var release = await FetchLatestAsync(ct);
            var installed = YouTubeRuntime.TryReadVersion(target);
            var available = YouTubeRuntime.IsNewer(release.Version, installed);
            if (operation == "manual-check" || !available)
            {
                return await WriteResultAsync(resultPath, new YouTubeRuntimeCommandResult(
                    operation, true, installed, release.Version, available,
                    available ? "A newer yt-dlp release is available." : "yt-dlp is current.", null,
                    DateTimeOffset.UtcNow, previous?.LastUpdatedAt, previous?.LastUpdateSucceeded,
                    previous?.LastUpdateMessage), ct);
            }

            var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".yt-dlp-update-{Guid.NewGuid():N}");
            var swapped = false;
            try
            {
                await DownloadAndVerifyAsync(release, temporary, ct);
                InstallAtomically(temporary, target);
                swapped = true;
                var verified = YouTubeRuntime.TryReadVersion(target);
                if (!string.Equals(verified, release.Version, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The installed yt-dlp reported {verified ?? "no version"}; expected {release.Version}.");

                var completed = DateTimeOffset.UtcNow;
                return await WriteResultAsync(resultPath, new YouTubeRuntimeCommandResult(
                    operation, true, verified, release.Version, false,
                    $"yt-dlp was updated to {release.Version}.", null, completed, completed, true,
                    $"Updated yt-dlp to {release.Version}."), ct);
            }
            catch
            {
                if (swapped) RestorePrevious(target);
                throw;
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var failureCode = error switch
            {
                HttpRequestException => "release-fetch-failed",
                InvalidDataException => "release-metadata-invalid",
                UnauthorizedAccessException => "runtime-write-denied",
                _ => "runtime-update-failed"
            };
            return await WriteResultAsync(resultPath,
                Failure(operation, error.Message, failureCode, previous), CancellationToken.None);
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    private static YouTubeRuntimeCommandResult Failure(
        string operation, string message, string failureCode, YouTubeRuntimeCommandResult? previous) =>
        new(operation, false, YouTubeRuntime.TryReadVersion(BundledPath()), previous?.LatestVersion, previous?.UpdateAvailable ?? false,
            message.Length > 900 ? message[..900] : message, failureCode, DateTimeOffset.UtcNow,
            previous?.LastUpdatedAt, previous?.LastUpdateSucceeded, previous?.LastUpdateMessage);

    private static string ReadOperation(string requestPath)
    {
        try
        {
            if (!File.Exists(requestPath)) return "scheduled-update";
            var value = File.ReadAllText(requestPath).Trim().ToLowerInvariant();
            return value is "check" or "update" ? $"manual-{value}" : "scheduled-update";
        }
        catch { return "scheduled-update"; }
    }

    private static async Task<YouTubeReleaseAsset> FetchLatestAsync(CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LessonCue-yt-dlp-updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await client.GetAsync(RepositoryApi, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagProperty)
            ? tagProperty.GetString()?.TrimStart('v', 'V')
            : null;
        if (!YouTubeRuntime.IsValidVersion(tag))
            throw new InvalidDataException("The yt-dlp release did not contain a valid stable version.");

        var assetName = YouTubeRuntimeUpdateService.ExpectedAssetName(RuntimeInformation.ProcessArchitecture)
            ?? throw new InvalidDataException($"No yt-dlp asset is supported for {RuntimeInformation.ProcessArchitecture}.");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The yt-dlp release did not contain an asset list.");
        var asset = assets.EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("name", out var name) && name.GetString() == assetName);
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"The yt-dlp release did not contain {assetName}.");
        var download = asset.GetProperty("browser_download_url").GetString();
        var digest = asset.GetProperty("digest").GetString();
        if (!Uri.TryCreate(download, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !downloadUri.AbsolutePath.StartsWith("/yt-dlp/yt-dlp/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("The yt-dlp release asset URL was not an expected official GitHub URL.");
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            digest.Length != "sha256:".Length + 64 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("The yt-dlp release asset did not contain a valid SHA-256 digest.");
        return new YouTubeReleaseAsset(tag!, assetName, downloadUri, digest[7..].ToLowerInvariant());
    }

    private static async Task DownloadAndVerifyAsync(YouTubeReleaseAsset asset, string path, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LessonCue-yt-dlp-updater/1.0");
        using var response = await client.GetAsync(asset.DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            throw new InvalidDataException("The yt-dlp release asset exceeded the safe download size.");
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0) break;
                total += read;
                if (total > MaximumDownloadBytes) throw new InvalidDataException("The yt-dlp release asset exceeded the safe download size.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        await using var downloadedFile = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloadedFile, ct)).ToLowerInvariant();
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The yt-dlp release checksum did not match the GitHub digest (expected {asset.Sha256}, got {actual}).");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var version = YouTubeRuntime.TryReadVersion(path);
        if (!string.Equals(version, asset.Version, StringComparison.Ordinal))
            throw new InvalidDataException($"The downloaded yt-dlp reported {version ?? "no version"}; expected {asset.Version}.");
    }

    private static void InstallAtomically(string temporary, string target)
    {
        var targetInfo = new FileInfo(target);
        if (targetInfo.Exists && targetInfo.LinkTarget is not null)
            throw new InvalidDataException("The bundled yt-dlp path is a symbolic link and cannot be replaced safely.");
        var backup = target + ".previous";
        if (targetInfo.Exists)
        {
            File.Replace(temporary, target, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, target);
        }
    }

    private static void RestorePrevious(string target)
    {
        var backup = target + ".previous";
        if (!File.Exists(backup)) return;
        File.Replace(backup, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
    }

    private static string? SupportError(string target)
    {
        if (!OperatingSystem.IsLinux()) return "Independent yt-dlp updates are available on the native Linux server installation.";
        if (YouTubeRuntimeUpdateService.ExpectedAssetName(RuntimeInformation.ProcessArchitecture) is null)
            return $"Independent yt-dlp updates do not support this server architecture ({RuntimeInformation.ProcessArchitecture}).";
        if (!File.Exists(target)) return "The bundled yt-dlp executable is missing from the LessonCue installation.";
        return null;
    }

    private static string BundledPath() => Path.Combine(AppContext.BaseDirectory, "yt-dlp");

    private static YouTubeRuntimeCommandResult? ReadResult(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<YouTubeRuntimeCommandResult>(File.ReadAllText(path), YouTubeRuntimeJson.Options)
                : null;
        }
        catch { return null; }
    }

    private static async Task<int> WriteResultAsync(string path, YouTubeRuntimeCommandResult result, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result, YouTubeRuntimeJson.Options), ct);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporary, path, true);
            return result.Success ? 0 : 1;
        }
        finally { TryDelete(temporary); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

internal static class YouTubeRuntime
{
    public static bool IsValidVersion(string? version) =>
        Version.TryParse(version, out var parsed) && parsed.Build >= 0 && parsed.Revision < 0;

    public static bool IsNewer(string? candidate, string? current) =>
        Version.TryParse(candidate, out var latest) &&
        Version.TryParse(current, out var installed) && latest > installed;

    public static string? TryReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start()) return null;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (process.ExitCode != 0) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            return IsValidVersion(output) ? output : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }
}
