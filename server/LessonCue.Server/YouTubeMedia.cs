using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace LessonCue.Server;

public static partial class YouTubeMedia
{
    public static bool IsYouTubeUrl(Uri uri) =>
        HostIs(uri.Host, "youtube.com") || HostIs(uri.Host, "youtu.be") || HostIs(uri.Host, "youtube-nocookie.com");

    public static bool HasStreamingFallback(string? value) => EmbedUrl(value) is not null;

    public static string? EmbedUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsYouTubeUrl(uri)) return null;
        string? id = null;
        if (HostIs(uri.Host, "youtu.be")) id = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        else
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] is "embed" or "shorts" or "live") id = parts[1];
            if (id is null && uri.AbsolutePath.TrimEnd('/').Equals("/watch", StringComparison.OrdinalIgnoreCase))
                id = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('=', 2)).FirstOrDefault(x => x.Length == 2 && x[0] == "v")?.ElementAtOrDefault(1);
        }
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = Uri.UnescapeDataString(id);
        return VideoId().IsMatch(id) ? $"https://www.youtube.com/embed/{id}?autoplay=1&controls=1&rel=0" : null;
    }

    private static bool HostIs(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9_-]{6,20}$")]
    private static partial Regex VideoId();
}

public sealed class YouTubeImportService(
    IServiceScopeFactory scopes,
    MediaStoragePaths paths,
    StorageService storage,
    YouTubeRuntimeUpdateService runtimeUpdates,
    ILogger<YouTubeImportService> logger) : BackgroundService
{
    private sealed record DownloadProfile(string Name, string Format, string? ExtractorArguments,
        string? MergeOutputFormat, string FormatSort, bool UseJavaScriptRuntime = true);

    private static readonly DownloadProfile[] DownloadProfiles =
    [
        new("adaptive-original-up-to-4k", "bestvideo+bestaudio/best", null, "mkv", "res:2160"),
        new("android-adaptive-original-up-to-4k", "bestvideo+bestaudio/best",
            "youtube:player_client=android", "mkv", "res:2160"),
        new("android-progressive-fallback", "best[ext=mp4]",
            "youtube:player_client=android", null, "res:2160"),
        // Deno's challenge solver can exhaust its caged heap on some videos.
        // Keep one final bounded download attempt that does not start Deno;
        // valid YouTube links still have the streaming fallback below when
        // the provider requires a JavaScript challenge.
        new("android-progressive-no-js-fallback", "best[ext=mp4]/best",
            "youtube:player_client=android", null, "res:1080", false)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recoveryPending = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (recoveryPending)
                {
                    await RecoverStreamingFallbacksAsync(stoppingToken);
                    recoveryPending = false;
                }
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
                var item = await db.MediaAssets.FirstOrDefaultAsync(
                    x => x.SourceKind == "youtube-download" && x.ProcessingStatus == "downloading", stoppingToken);
                if (item is null) { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); continue; }
                await DownloadAsync(item, db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "YouTube import loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DownloadAsync(MediaAsset item, LessonCueDb db, CancellationToken ct)
    {
        var temporary = Path.Combine(paths.DataPath, "media", "temporary", "youtube-" + item.Id.ToString("N"));
        try
        {
            if (!Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var uri) || !YouTubeMedia.IsYouTubeUrl(uri))
                throw new InvalidOperationException("Only YouTube URLs can be downloaded by this importer.");
            var executable = FindExecutable();
            string? denoExecutable = null;
            try { denoExecutable = YouTubeRuntimeLocator.ResolveDenoExecutable(); }
            catch (Exception ex) { logger.LogWarning(ex, "Deno is unavailable; the YouTube importer will try its no-JavaScript fallback"); }
            var snapshot = await storage.GetSnapshotAsync(db, ct);
            if (snapshot.RemainingBytes < 1024 * 1024)
                throw new InvalidOperationException("The LessonCue storage allocation is full.");

            Directory.CreateDirectory(temporary);
            var outputTemplate = Path.Combine(temporary, "%(title).150B [%(id)s].%(ext)s");
            string? downloaded = null;
            string? selectedProfile = null;
            for (var profileIndex = 0; profileIndex < DownloadProfiles.Length; profileIndex++)
            {
                var profile = DownloadProfiles[profileIndex];
                if (profileIndex > 0) ResetStagingDirectory(temporary);
                try
                {
                    var stdout = await ConstrainedProcessRunner.RunAsync(executable,
                        BuildDownloadArguments(outputTemplate, snapshot.RemainingBytes, item.SourceUrl!, denoExecutable, profile),
                        ConstrainedProcessOptions.Download(temporary, snapshot.RemainingBytes), ct);
                    downloaded = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(Path.GetFullPath).LastOrDefault(File.Exists);
                    // The path comes back on the downloader's stdout, so it is
                    // only trusted once it is shown to be inside the working
                    // directory the downloader was confined to.
                    if (!ContainedPath.IsInside(temporary, downloaded))
                        throw new InvalidOperationException("The downloader did not produce a valid local file.");
                    selectedProfile = profile.Name;
                    logger.LogInformation("YouTube media download succeeded for {MediaId} using profile {Profile}", item.Id, profile.Name);
                    break;
                }
                catch (Exception ex) when (profileIndex + 1 < DownloadProfiles.Length && ShouldTryAlternateProfile(ex))
                {
                    logger.LogWarning(ex,
                        "YouTube media download profile {Profile} failed for {MediaId}; trying the bounded fallback profile",
                        profile.Name, item.Id);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"YouTube download failed with profile '{profile.Name}': {ex.Message}", ex);
                }
            }

            if (downloaded is null)
                throw new InvalidOperationException("The downloader did not produce a valid local file after all supported profiles.");
            var info = new FileInfo(downloaded);
            if (info.Length > snapshot.RemainingBytes)
                throw new InvalidOperationException("The downloaded video exceeds the available LessonCue storage.");

            var extension = Path.GetExtension(downloaded).ToLowerInvariant();
            if (!MediaFormatCatalog.IsVideo(extension))
                throw new InvalidOperationException(
                    $"YouTube did not provide a supported video container ({extension}).");
            MediaContentInspector.RequireValid(downloaded, downloaded);
            var storedName = item.Id + extension;
            var destination = Path.Combine(paths.Originals, storedName);
            Directory.CreateDirectory(paths.Originals);
            File.Move(downloaded, destination, true);
            await using var stream = File.OpenRead(destination);
            item.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            item.RelativePath = storedName;
            item.FileName = Path.GetFileName(downloaded);
            item.ContentType = MediaFormatCatalog.ContentType(extension);
            item.SizeBytes = info.Length;
            item.OfflineEligible = true;
            item.LinkKind = "youtube-local";
            item.ProcessingStatus = "pending";
            item.ProcessingError = null;
            db.AuditEvents.Add(new AuditEvent { Actor = "system", Action = "media.youtube.download",
                Object = item.Id.ToString(), Summary = $"Downloaded {item.FileName} to local storage using profile {selectedProfile}." });
            await db.SaveChangesAsync(ct);
            runtimeUpdates.RecordYouTubeSuccess();
        }
        catch (Exception ex)
        {
            var streamingFallback = string.IsNullOrWhiteSpace(item.RelativePath) &&
                YouTubeMedia.HasStreamingFallback(item.SourceUrl);
            if (streamingFallback)
            {
                item.ProcessingStatus = "ready";
                item.ProcessingError = StreamingFallbackMessage(ex.Message);
                item.CompatibilityStatus = "not-needed";
                item.CompatibilityError = null;
                item.OfflineEligible = false;
                item.LinkKind = "youtube";
                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = "system", Action = "media.youtube.streaming-fallback", Object = item.Id.ToString(),
                    Result = "fallback", Summary = $"{item.FileName}: {item.ProcessingError}"
                });
            }
            else
            {
                item.ProcessingStatus = "failed";
                item.ProcessingError = Concise(ex.Message);
            }
            await db.SaveChangesAsync(CancellationToken.None);
            runtimeUpdates.RecordYouTubeFailure(ex);
            logger.LogWarning(ex, streamingFallback
                ? "Could not import YouTube media {MediaId}; keeping its source URL as a streaming fallback"
                : "Could not import YouTube media {MediaId}", item.Id);
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch (Exception ex) { logger.LogDebug(ex, "Could not remove YouTube staging directory"); }
        }
    }

    internal static IReadOnlyList<string> BuildDownloadArguments(
        string outputTemplate, long availableBytes, string sourceUrl, string denoExecutable) =>
        BuildDownloadArguments(outputTemplate, availableBytes, sourceUrl, denoExecutable, DownloadProfiles[0]);

    internal static IReadOnlyList<string> BuildFallbackDownloadArguments(
        string outputTemplate, long availableBytes, string sourceUrl, string denoExecutable) =>
        BuildDownloadArguments(outputTemplate, availableBytes, sourceUrl, denoExecutable, DownloadProfiles[1]);

    internal static IReadOnlyList<string> BuildProgressiveFallbackDownloadArguments(
        string outputTemplate, long availableBytes, string sourceUrl, string denoExecutable) =>
        BuildDownloadArguments(outputTemplate, availableBytes, sourceUrl, denoExecutable, DownloadProfiles[2]);

    internal static IReadOnlyList<string> BuildNoJavaScriptFallbackDownloadArguments(
        string outputTemplate, long availableBytes, string sourceUrl, string denoExecutable) =>
        BuildDownloadArguments(outputTemplate, availableBytes, sourceUrl, denoExecutable, DownloadProfiles[3]);

    private static IReadOnlyList<string> BuildDownloadArguments(
        string outputTemplate, long availableBytes, string sourceUrl, string? denoExecutable,
        DownloadProfile profile)
    {
        var arguments = new List<string>
        {
            "--no-config", "--no-playlist", "--newline", "--restrict-filenames",
            "--retries", "3", "--fragment-retries", "3",
            "--max-filesize", availableBytes.ToString(CultureInfo.InvariantCulture),
            "-f", profile.Format,
            "-S", profile.FormatSort
        };
        if (profile.UseJavaScriptRuntime)
        {
            if (string.IsNullOrWhiteSpace(denoExecutable))
                throw new InvalidOperationException("The bundled Deno runtime is unavailable for this YouTube download profile.");
            arguments.Insert(4, "--js-runtimes");
            arguments.Insert(5, $"deno:{denoExecutable}");
        }
        if (profile.ExtractorArguments is not null)
        {
            arguments.Add("--extractor-args");
            arguments.Add(profile.ExtractorArguments);
        }
        if (profile.MergeOutputFormat is not null)
        {
            arguments.Add("--merge-output-format");
            arguments.Add(profile.MergeOutputFormat);
        }
        arguments.Add("-o");
        arguments.Add(outputTemplate);
        arguments.Add("--print");
        arguments.Add("after_move:filepath");
        arguments.Add(sourceUrl);
        return arguments;
    }

    internal static bool ShouldTryAlternateProfile(Exception error)
    {
        var message = error.ToString().ToLowerInvariant();
        return message.Contains("403") || message.Contains("forbidden") ||
            message.Contains("unable to download video data") ||
            message.Contains("requested format") || message.Contains("no video formats") ||
            message.Contains("page needs to be reloaded") || message.Contains("sabr") ||
            message.Contains("out of memory") || message.Contains("cagedheap") ||
            message.Contains("memory limit") || message.Contains("reservation") ||
            message.Contains("did not produce a valid local file") ||
            message.Contains("bundled deno runtime") || message.Contains("javascript runtime") ||
            message.Contains("deno is unavailable");
    }

    internal static string StreamingFallbackMessage(string? error)
    {
        var detail = Concise(error);
        return string.IsNullOrWhiteSpace(detail)
            ? "Offline download is unavailable; the original YouTube link remains available for streaming."
            : $"Offline download is unavailable; the original YouTube link remains available for streaming. {detail}";
    }

    private async Task RecoverStreamingFallbacksAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
        var candidates = await db.MediaAssets
            .Where(x => x.SourceKind == "youtube-download" && x.ProcessingStatus == "failed")
            .ToListAsync(ct);
        var recoverable = candidates.Where(x => string.IsNullOrWhiteSpace(x.RelativePath) &&
            YouTubeMedia.HasStreamingFallback(x.SourceUrl)).ToList();
        foreach (var item in recoverable)
        {
            item.ProcessingStatus = "ready";
            item.ProcessingError = StreamingFallbackMessage(item.ProcessingError);
            item.CompatibilityStatus = "not-needed";
            item.CompatibilityError = null;
            item.OfflineEligible = false;
            item.LinkKind = "youtube";
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = "system", Action = "media.youtube.streaming-fallback.recovered", Object = item.Id.ToString(),
                Result = "fallback", Summary = $"Recovered {item.FileName} as a streaming-backed YouTube item."
            });
        }
        if (recoverable.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Recovered {Count} failed YouTube item(s) as streaming fallbacks", recoverable.Count);
        }
    }

    private static string Concise(string? message, int maxLength = 700)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static void ResetStagingDirectory(string path)
    {
        Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("LESSONCUE_YTDLP_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        return File.Exists(bundled) ? bundled : OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
    }
}

/// <summary>
/// Locates the runtimes used by the explicitly requested local YouTube import.
/// The release bundles Deno beside yt-dlp, while environment overrides keep
/// development and existing managed installations configurable.
/// </summary>
internal static class YouTubeRuntimeLocator
{
    public static string ResolveDenoExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("LESSONCUE_DENO_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = FindConfigured(configured, "deno");
            if (resolved is not null) return resolved;
            throw new InvalidOperationException(
                $"YouTube local imports require Deno 2.3 or newer, but LESSONCUE_DENO_PATH does not point to an executable Deno runtime: {configured}");
        }

        return FindExecutable("LESSONCUE_DENO_PATH", "deno")
            ?? throw new InvalidOperationException(
                "YouTube local imports require Deno 2.3 or newer. The bundled Deno runtime is missing; install the latest LessonCue server release or set LESSONCUE_DENO_PATH.");
    }

    public static string? FindExecutable(string environmentName, string command)
    {
        var configured = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(configured))
            return FindConfigured(configured, command) ?? configured;

        var bundled = BundledPath(command);
        if (IsUsable(bundled)) return bundled;
        return FindOnPath(command);
    }

    public static string BundledPath(string command) =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? command + ".exe" : command);

    public static bool IsUsable(string path)
    {
        if (!File.Exists(path)) return false;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                if (!mode.HasFlag(UnixFileMode.UserExecute) &&
                    !mode.HasFlag(UnixFileMode.GroupExecute) &&
                    !mode.HasFlag(UnixFileMode.OtherExecute)) return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        return true;
    }

    private static string? FindConfigured(string value, string command)
    {
        if (Directory.Exists(value))
        {
            var inDirectory = Path.Combine(value, OperatingSystem.IsWindows() ? command + ".exe" : command);
            return IsUsable(inDirectory) ? inDirectory : null;
        }

        if (IsUsable(value)) return value;
        return Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)
            ? null
            : FindOnPath(value);
    }

    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var names = OperatingSystem.IsWindows() && Path.GetExtension(command).Length == 0
            ? new[] { command, command + ".exe" }
            : new[] { command };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var name in names)
        {
            var candidate = Path.Combine(directory, name);
            if (IsUsable(candidate)) return candidate;
        }
        return null;
    }
}
