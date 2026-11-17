using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace LessonCue.Server;

public sealed class MediaProcessingService(IServiceScopeFactory scopes, MediaStoragePaths paths,
    StorageService storage, HardwareAccelerationService hardware,
    ILogger<MediaProcessingService> logger) : BackgroundService
{
    private const string LocalMediaProtocols = "-protocol_whitelist file,pipe,crypto,data";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverInterruptedAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex) { logger.LogError(ex, "Could not recover interrupted media processing"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
                var item = await db.MediaAssets.FirstOrDefaultAsync(x => x.ProcessingStatus == "pending" ||
                    (x.ProcessingStatus == "ready" && x.CompatibilityStatus == "pending" && x.SourceKind != "link"),
                    stoppingToken);
                if (item is null) { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); continue; }
                await ProcessAsync(item, db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media processing loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
        var interrupted = await db.MediaAssets
            .Where(x => x.ProcessingStatus == "processing" || x.CompatibilityStatus == "converting")
            .ToListAsync(ct);
        foreach (var item in interrupted)
        {
            if (item.ProcessingStatus == "processing") item.ProcessingStatus = "pending";
            if (item.CompatibilityStatus == "converting") item.CompatibilityStatus = "pending";
            item.ProcessingError = null;
            item.CompatibilityError = null;
        }

        // A streaming-only link never has a local file or a compatibility
        // derivative. Older releases could leave its compatibility state at
        // pending after a restart, which made the troubleshooting report look
        // like a stuck conversion even though the link was usable.
        var streamingLinks = await db.MediaAssets
            .Where(x => x.SourceKind == "link" &&
                (x.CompatibilityStatus != "not-needed" || x.OfflineEligible))
            .ToListAsync(ct);
        foreach (var item in streamingLinks)
        {
            item.CompatibilityStatus = "not-needed";
            item.CompatibilityError = null;
            item.OfflineEligible = false;
        }

        if (interrupted.Count > 0 || streamingLinks.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task ProcessAsync(MediaAsset item, LessonCueDb db, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(Path.Combine(paths.Originals, item.RelativePath));

        // A source that has already been inspected and had its derivatives made
        // is playable now.  Compatibility conversion is deliberately a second
        // phase so a slow or unavailable encoder never hides the original file.
        if (item.ProcessingStatus == "ready" && item.CompatibilityStatus == "pending")
        {
            await AdaptiveTranscodeService.QueueReadyVideoProfilesAsync(db, ct);
            await ProcessCompatibilityAsync(item, db, fullPath, remuxOnly: false, ct);
            return;
        }

        item.ProcessingStatus = "processing";
        await db.SaveChangesAsync(ct);
        try
        {
            if (item.SourceKind == "link")
            {
                item.CompatibilityStatus = "not-needed";
                item.ProcessingStatus = "ready";
                await db.SaveChangesAsync(ct);
                return;
            }
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            // FileName is the user-facing title and may intentionally omit or change
            // an extension (for example, generated presentation slides). RelativePath
            // is the immutable, server-controlled storage name and therefore the
            // authoritative type declaration for files that have already been admitted.
            MediaContentInspector.RequireValid(fullPath, item.RelativePath);
            if (MediaFormatCatalog.IsConvertibleDocument(extension))
            {
                item.OfflineEligible = false;
                item.CompatibilityStatus = "not-needed";
                item.ProcessingStatus = "ready";
                item.ProcessingError = "Use Convert to slides in the Media Library to make this document screen-ready.";
                await db.SaveChangesAsync(ct);
                return;
            }

            var json = await RunAsync("ffprobe", $"{LocalMediaProtocols} -v error -show_streams -show_format -of json \"{Escape(fullPath)}\"", ct);
            using var document = JsonDocument.Parse(json);
            string? pixelFormat = null;
            string? formatName = null;
            int? h264Level = null;
            foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeValue) ? typeValue.GetString() : null;
                var codec = stream.TryGetProperty("codec_name", out var codecValue) ? codecValue.GetString() : null;
                if (type == "video")
                {
                    item.VideoCodec = codec;
                    item.Width = stream.TryGetProperty("width", out var width) ? width.GetInt32() : null;
                    item.Height = stream.TryGetProperty("height", out var height) ? height.GetInt32() : null;
                    pixelFormat = stream.TryGetProperty("pix_fmt", out var pixel) ? pixel.GetString() : null;
                    h264Level = stream.TryGetProperty("level", out var level) && level.TryGetInt32(out var parsedLevel) ? parsedLevel : null;
                }
                if (type == "audio" && item.AudioCodec is null) item.AudioCodec = codec;
            }
            if (document.RootElement.TryGetProperty("format", out var format))
            {
                formatName = format.TryGetProperty("format_name", out var formatValue) ? formatValue.GetString() : null;
                if (format.TryGetProperty("duration", out var duration) && double.TryParse(duration.GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) item.DurationMs = (long)(seconds * 1000);
            }
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct);
            var uploadPolicy = UploadQuotaPolicy.Read(organization);
            if (!uploadPolicy.Allows(item.VideoCodec, item.AudioCodec))
            {
                var rejected = new List<string>();
                if (item.VideoCodec is not null && uploadPolicy.AllowedVideoCodecs.Count > 0 &&
                    !uploadPolicy.AllowedVideoCodecs.Contains(item.VideoCodec))
                    rejected.Add($"video codec {item.VideoCodec}");
                if (item.AudioCodec is not null && uploadPolicy.AllowedAudioCodecs.Count > 0 &&
                    !uploadPolicy.AllowedAudioCodecs.Contains(item.AudioCodec))
                    rejected.Add($"audio codec {item.AudioCodec}");
                throw new InvalidDataException(
                    $"The server upload policy does not allow {string.Join(" and ", rejected)}. Ask a Service Admin to change the codec policy or upload a converted file.");
            }

            var isVideo = IsVideo(extension, item.ContentType);
            var needsCompatibility = false;
            var remuxOnly = false;
            if (isVideo)
            {
                var nativeEncoding = PlaybackCompatibility.HasUniversalEncoding(item.VideoCodec, item.AudioCodec,
                    pixelFormat, h264Level, item.Width, item.Height);
                var nativeContainer = PlaybackCompatibility.HasMp4Container(formatName);
                needsCompatibility = !nativeEncoding || !nativeContainer;
                remuxOnly = nativeEncoding && !nativeContainer;
                if (needsCompatibility)
                {
                    item.CompatibilityStatus = "pending";
                    item.CompatibilityError = null;
                }
                else
                {
                    item.CompatibilityStatus = "native";
                    item.CompatibilityError = null;
                    item.CompatibilityTranscodedAt = null;
                    item.CompatibilityTranscodeEngine = null;
                }
            }
            else item.CompatibilityStatus = "not-needed";

            if (item.VideoCodec is not null)
            {
                Directory.CreateDirectory(paths.Thumbnails);
                var relative = item.Id + ".jpg";
                var output = Path.Combine(paths.Thumbnails, relative);
                var seek = item.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "" : "-ss 0.1 ";
                await RunDerivativeAsync("ffmpeg", $"-nostdin -y {LocalMediaProtocols} {seek}-i \"{Escape(fullPath)}\" -frames:v 1 -vf scale=640:-2:out_range=full -pix_fmt yuvj420p \"{Escape(output)}\"", item.FileName, ct);
                if (File.Exists(output)) item.ThumbnailPath = relative;
            }

            // Thumbnail generation is the first derivative and the only one
            // required before the original can be used in a lesson. Persist
            // that ready state before starting any lower-priority analysis.
            item.ProcessingStatus = "ready";
            item.ProcessingError = null;
            item.OfflineEligible = !isVideo || item.CompatibilityStatus is "native" or "ready";
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "MediaAsset {MediaAssetId} reached ready state. FileName {FileName}; RelativePath {RelativePath}; ProcessingStatus {ProcessingStatus}; CompatibilityStatus {CompatibilityStatus}; OfflineEligible {OfflineEligible}",
                item.Id, item.FileName, item.RelativePath, item.ProcessingStatus, item.CompatibilityStatus, item.OfflineEligible);

            // Start the compatibility copy immediately after the thumbnail.
            // The playback endpoint serves the original while this optional
            // copy is being made, and adaptive profiles can transcode from the
            // already-validated original in parallel.
            await AdaptiveTranscodeService.QueueReadyVideoProfilesAsync(db, ct);
            if (needsCompatibility)
                await ProcessCompatibilityAsync(item, db, fullPath, remuxOnly, ct);

            // Filmstrips and waveform/loudness data improve the editor but are
            // intentionally after the ready state and compatibility trigger.
            if (item.VideoCodec is not null && item.DurationMs is > 0)
            {
                var filmstripRelative = item.Id + "-filmstrip.jpg";
                var filmstripOutput = Path.Combine(paths.Thumbnails, filmstripRelative);
                var interval = Math.Max(.1, item.DurationMs.Value / 6000d).ToString("0.###", CultureInfo.InvariantCulture);
                await RunDerivativeAsync("ffmpeg", $"-nostdin -y {LocalMediaProtocols} -i \"{Escape(fullPath)}\" -vf \"fps=1/{interval},scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2,tile=6x1\" -frames:v 1 -q:v 3 \"{Escape(filmstripOutput)}\"", item.FileName, ct);
                if (File.Exists(filmstripOutput)) item.FilmstripPath = filmstripRelative;
            }
            if (item.AudioCodec is not null)
            {
                try
                {
                    var loudness = await RunAsync("ffmpeg", $"-nostdin -hide_banner -nostats {LocalMediaProtocols} -i \"{Escape(fullPath)}\" -af loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json -f null -", ct);
                    var match = Regex.Match(loudness, "\\\"input_i\\\"\\s*:\\s*\\\"(?<value>-?[0-9.]+)\\\"");
                    if (match.Success && double.TryParse(match.Groups["value"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs)) item.LoudnessLufs = lufs;
                    Directory.CreateDirectory(paths.Thumbnails);
                    var waveformRelative = item.Id + "-waveform.png";
                    var waveformOutput = Path.Combine(paths.Thumbnails, waveformRelative);
                    await RunDerivativeAsync("ffmpeg", $"-nostdin -y {LocalMediaProtocols} -i \"{Escape(fullPath)}\" -filter_complex \"aformat=channel_layouts=mono,showwavespic=s=1200x140:colors=#d89127\" -frames:v 1 \"{Escape(waveformOutput)}\"", item.FileName, ct);
                    if (File.Exists(waveformOutput)) item.WaveformPath = waveformRelative;
                }
                catch (Exception ex) { logger.LogWarning(ex, "Could not analyze audio for {MediaFile}", item.FileName); }
            }
            await db.SaveChangesAsync(ct);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var source = await MediaRecovery.ValidateOriginalAsync(item, paths, ct);
            if (IsRecoverableInfrastructureFailure(ex) && source.Valid)
            {
                // Upload completion already validated the source signature and
                // checksum. A missing worker/sandbox must not turn that intact
                // source into an unavailable lesson. Keep it playable now,
                // leave compatibility pending for the worker to retry, and
                // retain the exact dependency error for diagnostics.
                var isVideo = IsVideo(Path.GetExtension(item.RelativePath).ToLowerInvariant(), item.ContentType);
                item.ProcessingStatus = "ready";
                item.ProcessingError = "Media analysis is waiting for the processing runtime. " + Concise(ex.Message, 700);
                item.CompatibilityStatus = isVideo ? "pending" : "not-needed";
                item.CompatibilityError = null;
                item.OfflineEligible = !isVideo;
                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = "system",
                    Action = "media.processing.deferred",
                    Object = item.Id.ToString(),
                    Result = "deferred",
                    Summary = $"{item.FileName}: {item.ProcessingError}"
                });
                logger.LogError(ex,
                    "MediaAsset {MediaAssetId} is playable from its original but processing was deferred. FileName {FileName}; RelativePath {RelativePath}; ExpectedSizeBytes {ExpectedSizeBytes}; ExpectedSha256 {ExpectedSha256}; ProcessingError {ProcessingError}",
                    item.Id, item.FileName, item.RelativePath, item.SizeBytes, item.Sha256, item.ProcessingError);
            }
            else
            {
                item.ProcessingStatus = "failed";
                item.ProcessingError = Concise(ex.Message);
                item.OfflineEligible = false;
                db.AuditEvents.Add(new AuditEvent
                {
                    Actor = "system",
                    Action = "media.processing.failed",
                    Object = item.Id.ToString(),
                    Result = "failed",
                    Summary = $"{item.FileName}: {item.ProcessingError}"
                });
                logger.LogError(ex,
                    "MediaAsset {MediaAssetId} failed processing. FileName {FileName}; RelativePath {RelativePath}; ExpectedSizeBytes {ExpectedSizeBytes}; ExpectedSha256 {ExpectedSha256}; ProcessingError {ProcessingError}",
                    item.Id, item.FileName, item.RelativePath, item.SizeBytes, item.Sha256, item.ProcessingError);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessCompatibilityAsync(MediaAsset item, LessonCueDb db, string source,
        bool remuxOnly, CancellationToken ct)
    {
        if (item.ProcessingStatus != "ready" || item.CompatibilityStatus != "pending") return;

        item.CompatibilityStatus = "converting";
        item.CompatibilityError = null;
        await db.SaveChangesAsync(ct);
        try
        {
            await CreateCompatibilityCopyAsync(item, db, source, remuxOnly, ct);
            item.OfflineEligible = true;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // The original remains a valid playback source even when the
            // optional compatibility copy cannot be created.
            item.CompatibilityStatus = "failed";
            item.CompatibilityError = "LessonCue could not create a TV-compatible H.264/AAC copy. " + Concise(ex.Message, 700);
            item.OfflineEligible = false;
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = "system",
                Action = "media.compatibility.failed",
                Object = item.Id.ToString(),
                Result = "failed",
                Summary = $"{item.FileName}: {item.CompatibilityError}"
            });
            await db.SaveChangesAsync(ct);
            logger.LogError(ex,
                "MediaAsset {MediaAssetId} compatibility processing failed. FileName {FileName}; CompatibilityStatus {CompatibilityStatus}; CompatibilityError {CompatibilityError}",
                item.Id, item.FileName, item.CompatibilityStatus, item.CompatibilityError);
        }

        // Queue adaptive copies after both successful and failed compatibility
        // attempts. A failed optional conversion still leaves the original as
        // a valid source for the adaptive worker to encode directly.
        await AdaptiveTranscodeService.QueueReadyVideoProfilesAsync(db, ct);
    }

    private async Task<string> CreateCompatibilityCopyAsync(MediaAsset item, LessonCueDb db, string source,
        bool remuxOnly, CancellationToken ct)
    {
        var workRoot = Path.Combine(paths.Temporary, $"compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var work = Path.Combine(workRoot, "output.mp4");
        try
        {
            if (remuxOnly)
            {
                await RunAsync("ffmpeg", $"-nostdin -hide_banner -loglevel error -nostats -y {LocalMediaProtocols} -i \"{Escape(source)}\" " +
                    $"-map 0:v:0 -map 0:a:0? -c copy -sn -dn -movflags +faststart \"{Escape(work)}\"", ct);
                await HardwareAccelerationService.ValidateMp4Async(work, ct);
                item.CompatibilityTranscodeEngine = "Remux";
            }
            else
            {
                const string filter = "scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2";
                var ending = $"-c:a aac -b:a 192k -ar 48000 -ac 2 -sn -dn -movflags +faststart \"{Escape(work)}\"";
                var hardwareArgs = $"-nostdin -hide_banner -loglevel error -nostats -y {LocalMediaProtocols} " +
                    $"{hardware.DeviceArguments} " +
                    $"-i \"{Escape(source)}\" -map 0:v:0 -map 0:a:0? " +
                    $"{hardware.BuildHardwareVideoArguments(filter, 20)} " +
                    $"-profile:v high -level:v 4.1 {ending}";
                var softwareArgs = $"-nostdin -hide_banner -loglevel error -nostats -y {LocalMediaProtocols} -i \"{Escape(source)}\" " +
                    $"-map 0:v:0 -map 0:a:0? -vf \"{filter}\" -c:v libx264 -preset medium -crf 20 " +
                    $"-profile:v high -level:v 4.1 -pix_fmt yuv420p -tag:v avc1 {ending}";
                var accelerationEnabled = await db.Organizations.AsNoTracking()
                    .OrderBy(item => item.Id).Select(x => x.HardwareAccelerationEnabled).FirstAsync(ct);
                var result = await hardware.RunTranscodeAsync(accelerationEnabled, hardwareArgs, softwareArgs, work, ct);
                item.CompatibilityTranscodeEngine = result.Engine;
            }
            var size = new FileInfo(work).Length;
            if (size == 0) throw new InvalidOperationException("FFmpeg created an empty compatibility copy.");
            if (await storage.EnsureAvailableAsync(db, size, ct) is null)
                throw new InvalidOperationException($"The TV-compatible copy needs {size} additional bytes, but the LessonCue storage allocation is full.");

            Directory.CreateDirectory(paths.Compatibility);
            var relative = $"{item.Id:N}-{Guid.NewGuid().ToString("N")[..8]}.mp4";
            var destination = Path.Combine(paths.Compatibility, relative);
            try { File.Move(work, destination); }
            catch (IOException) { File.Copy(work, destination); File.Delete(work); }
            await using var input = File.OpenRead(destination);
            item.CompatibilityPath = relative;
            item.CompatibilitySha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            item.CompatibilitySizeBytes = size;
            item.CompatibilityStatus = "ready";
            item.CompatibilityError = null;
            item.CompatibilityTranscodedAt = DateTimeOffset.UtcNow;
            return destination;
        }
        finally
        {
            TryDelete(work);
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true); } catch { }
        }
    }

    private static bool IsVideo(string extension, string contentType) =>
        MediaFormatCatalog.IsVideo(extension, contentType);

    internal static bool IsRecoverableInfrastructureFailure(Exception error)
    {
        if (error is InvalidDataException) return false;
        if (error is UnauthorizedAccessException or IOException) return true;
        var message = error.ToString().ToLowerInvariant();
        return message.Contains("media worker") || message.Contains("sandbox") ||
            message.Contains("bwrap") || message.Contains("namespace") ||
            message.Contains("setpriv") || message.Contains("capability isolation") ||
            message.Contains("permission denied") || message.Contains("no such file or directory") ||
            message.Contains("exceeded its") || message.Contains("timed out");
    }

    private Task<string> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Thumbnails);
        Directory.CreateDirectory(paths.Compatibility);
        Directory.CreateDirectory(paths.Temporary);
        return ConstrainedProcessRunner.RunAsync(fileName,
            ConstrainedProcessRunner.SplitArguments(arguments),
            ConstrainedProcessOptions.Media([paths.Thumbnails, paths.Compatibility, paths.Temporary]),
            ct);
    }

    private async Task RunDerivativeAsync(string fileName, string arguments, string mediaName, CancellationToken ct)
    {
        try { await RunAsync(fileName, arguments, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not create timeline derivative for {MediaFile}", mediaName); }
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
    private static string Concise(string value, int length = 900) => value.Length > length ? value[..length] : value;
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

public static class PlaybackCompatibility
{
    public static bool HasUniversalEncoding(string? videoCodec, string? audioCodec, string? pixelFormat,
        int? h264Level, int? width, int? height) =>
        videoCodec == "h264" && pixelFormat is "yuv420p" or "yuvj420p" &&
        audioCodec is null or "aac" && h264Level is null or <= 42 && width is null or <= 1920 && height is null or <= 1080;

    public static bool HasMp4Container(string? formatName) =>
        formatName?.Split(',').Contains("mp4", StringComparer.OrdinalIgnoreCase) == true;
}
