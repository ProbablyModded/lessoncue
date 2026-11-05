using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LessonCue.Server;

public static class PresentationConversion
{
    public static bool IsConvertible(string fileName) =>
        MediaFormatCatalog.IsConvertibleDocument(Path.GetExtension(fileName));

    public static bool IsPresentationExtension(string? extension) => extension?.ToLowerInvariant() is
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".pot" or ".potx" or
        ".odp" or ".odg" or ".otp" or ".fodp" or ".key";

    public static string LibreOfficePdfFilter(string? extension) =>
        IsPresentationExtension(extension) ? "pdf:impress_pdf_Export" : "pdf";

    public static bool TryGoogleSlidesExport(Uri source, out Uri export)
    {
        export = source;
        if (!source.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !source.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var presentation = Array.FindIndex(segments, value =>
            value.Equals("presentation", StringComparison.OrdinalIgnoreCase));
        if (presentation < 0 || presentation + 2 >= segments.Length ||
            !segments[presentation + 1].Equals("d", StringComparison.OrdinalIgnoreCase))
            return false;
        var id = segments[presentation + 2];
        if (id.Length is < 8 or > 180 || id.Any(character =>
            !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        export = new Uri($"https://docs.google.com/presentation/d/{id}/export/pdf");
        return true;
    }

    public static List<Guid> SlideIds(MediaAsset source)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(source.ConvertedSlidesJson) ?? []; }
        catch (JsonException) { return []; }
    }

    public static async Task<int> AddToLessonAsync(LessonCueDb db, MediaAsset source, Lesson lesson,
        int? imageDurationSeconds, string actor, CancellationToken ct = default)
    {
        var ids = SlideIds(source);
        if (source.ConversionStatus != "ready" || ids.Count == 0)
            throw new InvalidOperationException("Convert this presentation before adding its slides to a lesson.");
        var slides = await db.MediaAssets.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var byId = slides.ToDictionary(x => x.Id);
        if (ids.Any(id => !byId.ContainsKey(id)))
            throw new InvalidOperationException("One or more converted slide files are missing. Convert the source again.");
        var position = await db.PlaylistItems.Where(x => x.LessonId == lesson.Id)
            .Select(x => (decimal?)x.Position).MaxAsync(ct) ?? 0;
        var seconds = imageDurationSeconds is > 0
            ? Math.Clamp(imageDurationSeconds.Value, 1, 3600)
            : (int?)null;
        foreach (var id in ids)
        {
            var slide = byId[id];
            position += 1000;
            db.PlaylistItems.Add(new PlaylistItem
            {
                LessonId = lesson.Id,
                Title = slide.FileName,
                Type = "image",
                Role = "lesson",
                Position = position,
                MediaAssetId = slide.Id,
                DurationMs = seconds is int value ? value * 1000L : null,
                ImageDurationSeconds = seconds
            });
            if (slide.StoragePolicy == MediaRetention.LessonScoped) MediaRetention.KeepForLesson(slide, lesson);
        }
        lesson.Version++;
        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "presentation.add-to-lesson", Object = lesson.Id.ToString(),
            Summary = $"Added {ids.Count} slides from {source.FileName}." });
        await db.SaveChangesAsync(ct);
        return ids.Count;
    }
}

public sealed class PresentationConversionService(
    IServiceScopeFactory scopes,
    MediaStoragePaths paths,
    IHubContext<SyncHub> hub,
    ILogger<PresentationConversionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
                var source = await db.MediaAssets.FirstOrDefaultAsync(x => x.ConversionStatus == "pending" || x.ConversionStatus == "converting", stoppingToken);
                if (source is null) { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); continue; }
                await ConvertAsync(source, db, scope.ServiceProvider.GetRequiredService<StorageService>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Presentation conversion loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConvertAsync(MediaAsset source, LessonCueDb db, StorageService storage, CancellationToken ct)
    {
        source.ConversionStatus = "converting";
        source.ConversionError = null;
        await db.SaveChangesAsync(ct);
        var work = Path.Combine(Path.GetTempPath(), $"lessoncue-convert-{Guid.NewGuid():N}");
        var installed = new List<string>();
        try
        {
            var original = ResolveStoredFile(paths.Originals, source.RelativePath)
                ?? throw new InvalidOperationException("The local presentation source file is missing.");
            var extension = Path.GetExtension(source.RelativePath).ToLowerInvariant();
            if (!PresentationConversion.IsConvertible(source.RelativePath))
                throw new InvalidOperationException("Convert supports PDF, PowerPoint, OpenDocument, Keynote, Word, Excel, text, and other document files accepted by LessonCue.");
            MediaContentInspector.RequireValid(original, source.RelativePath);
            var pdftoppm = FindExecutable("LESSONCUE_PDFTOPPM_PATH", "pdftoppm",
                @"C:\Program Files\poppler\Library\bin\pdftoppm.exe")
                ?? throw new InvalidOperationException("PDF rendering is unavailable. Install poppler-utils (pdftoppm) on the LessonCue server.");
            Directory.CreateDirectory(work);
            var input = Path.Combine(work, "source" + extension);
            File.Copy(original, input);
            var pdf = input;
            if (extension != ".pdf")
            {
                var libreOffice = FindExecutable("LESSONCUE_LIBREOFFICE_PATH", "libreoffice",
                    @"C:\Program Files\LibreOffice\program\soffice.exe")
                    ?? FindExecutable("LESSONCUE_LIBREOFFICE_PATH", "soffice")
                    ?? throw new InvalidOperationException("Document conversion is unavailable. Install LibreOffice on the LessonCue server.");
                var profile = Path.Combine(work, "libreoffice-profile");
                var filter = PresentationConversion.LibreOfficePdfFilter(extension);
                var converterOutput = "";
                try
                {
                    converterOutput = await RunAsync(libreOffice,
                        ["--headless", "--invisible", "--nologo", "--nodefault", "--norestore", "--nolockcheck", "--nofirststartwizard",
                         $"-env:UserInstallation={new Uri(profile).AbsoluteUri}", "--convert-to", filter, "--outdir", work, input],
                        work, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    converterOutput = ex.Message;
                }
                pdf = Path.Combine(work, "source.pdf");
                if (!HasContent(pdf))
                {
                    // LibreOffice can leave a profile or lock behind after a
                    // crash, especially for very large presentations. Retry
                    // once with a fresh profile and a fresh output directory so
                    // a transient office-process failure does not strand the
                    // upload in a permanent conversion error.
                    var retryWork = Path.Combine(work, "libreoffice-retry");
                    Directory.CreateDirectory(retryWork);
                    var retryInput = Path.Combine(retryWork, "retry" + extension);
                    File.Copy(original, retryInput);
                    var retryProfile = Path.Combine(retryWork, "profile");
                    var retryOutput = "";
                    try
                    {
                        retryOutput = await RunAsync(libreOffice,
                            ["--headless", "--invisible", "--nologo", "--nodefault", "--norestore", "--nolockcheck", "--nofirststartwizard",
                             $"-env:UserInstallation={new Uri(retryProfile).AbsoluteUri}", "--convert-to", filter,
                             "--outdir", retryWork, retryInput], retryWork, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        retryOutput = ex.Message;
                    }
                    var retryPdf = Path.Combine(retryWork, "retry.pdf");
                    if (HasContent(retryPdf)) pdf = retryPdf;
                    else throw new InvalidOperationException(
                        $"LibreOffice did not produce a PDF for this document after two attempts. {ConciseConversionOutput(converterOutput + " " + retryOutput)}");
                }
            }
            var prefix = Path.Combine(work, "slide");
            var pdfinfo = FindExecutable("LESSONCUE_PDFINFO_PATH", "pdfinfo",
                Path.Combine(Path.GetDirectoryName(pdftoppm) ?? "", OperatingSystem.IsWindows() ? "pdfinfo.exe" : "pdfinfo"))
                ?? throw new InvalidOperationException("PDF preflight is unavailable. Install the complete Poppler utilities package on the LessonCue server.");
            var info = await RunAsync(pdfinfo, [pdf], work, ct);
            var pageMatch = Regex.Match(info, @"(?im)^Pages:\s*(\d+)\s*$");
            if (!pageMatch.Success || !int.TryParse(pageMatch.Groups[1].Value, out var pageCount) || pageCount < 1)
                throw new InvalidOperationException("LessonCue could not determine the document page count safely.");
            if (pageCount > 500) throw new InvalidOperationException("Presentations are limited to 500 slides per conversion.");
            await RunAsync(pdftoppm,
                ["-f", "1", "-l", "500", "-png", "-r", "144", "-scale-to", "1920", pdf, prefix],
                work, ct);
            var pages = Directory.EnumerateFiles(work, "slide-*.png").OrderBy(PageNumber).ToList();
            if (pages.Count == 0) throw new InvalidOperationException("The document did not produce any readable slides.");
            if (pages.Count != pageCount)
                throw new InvalidOperationException($"The document declared {pageCount} pages, but the local renderer produced {pages.Count}. No partial slide set was saved.");
            if (pages.Count > 500) throw new InvalidOperationException("Presentations are limited to 500 slides per conversion.");
            foreach (var page in pages)
                MediaContentInspector.RequireValid(page, Path.GetFileName(page));
            var totalBytes = pages.Sum(path => new FileInfo(path).Length);
            if (await storage.EnsureAvailableAsync(db, totalBytes, ct) is null)
                throw new InvalidOperationException("There is not enough LessonCue storage available for the converted slides.");

            Directory.CreateDirectory(paths.Originals);
            var sourceTitle = Path.GetFileNameWithoutExtension(source.FileName);
            var taxonomy = MediaTaxonomy.Read(await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(ct));
            var requestedFolder = MediaTaxonomy.NormalizeFolder(string.IsNullOrWhiteSpace(source.Folder) ? sourceTitle : $"{source.Folder}/{sourceTitle}");
            var folder = taxonomy.Folders.FirstOrDefault(value => value.Equals(requestedFolder, StringComparison.OrdinalIgnoreCase)) ?? source.Folder;
            var tags = taxonomy.Tags.Any(value => value.Equals("converted slide", StringComparison.OrdinalIgnoreCase))
                ? AppendTag(source.TagsCsv, "converted slide") : source.TagsCsv;
            var slideIds = new List<Guid>();
            for (var index = 0; index < pages.Count; index++)
            {
                var id = Guid.NewGuid();
                var storedName = id.ToString("N") + ".png";
                var destination = Path.Combine(paths.Originals, storedName);
                File.Move(pages[index], destination);
                installed.Add(destination);
                string sha;
                await using (var stream = File.OpenRead(destination))
                    sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                var slide = new MediaAsset
                {
                    Id = id,
                    FileName = $"{sourceTitle} — Slide {index + 1}",
                    ContentType = "image/png",
                    RelativePath = storedName,
                    Sha256 = sha,
                    SizeBytes = new FileInfo(destination).Length,
                    OfflineEligible = true,
                    ProcessingStatus = "pending",
                    SourceKind = "presentation-slide",
                    SourceUrl = source.Id.ToString(),
                    StoragePolicy = source.StoragePolicy,
                    OriginLessonId = source.OriginLessonId,
                    DeleteAfter = source.DeleteAfter,
                    RetentionDateIsManual = source.RetentionDateIsManual,
                    Folder = folder.Length <= 120 ? folder : folder[..120],
                    TagsCsv = tags
                };
                db.MediaAssets.Add(slide);
                slideIds.Add(slide.Id);
            }
            source.ConvertedSlidesJson = JsonSerializer.Serialize(slideIds);
            source.ConversionStatus = "ready";
            source.ConversionError = null;
            source.ConvertedAt = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent { Actor = "system", Action = "presentation.convert", Object = source.Id.ToString(),
                Summary = $"Converted {source.FileName} into {slideIds.Count} slides." });
            await db.SaveChangesAsync(ct);
            installed.Clear();
            if (source.ConversionLessonId is Guid targetLessonId)
            {
                source.ConversionLessonId = null;
                try
                {
                    var lesson = await db.Lessons.SingleOrDefaultAsync(x => x.Id == targetLessonId && !x.Archived, ct)
                        ?? throw new InvalidOperationException("The target lesson no longer exists.");
                    await PresentationConversion.AddToLessonAsync(db, source, lesson,
                        source.ConversionSlideDurationSeconds, "system", ct);
                }
                catch (Exception addError)
                {
                    source.ConversionError = $"Slides were converted, but could not be added automatically: {addError.Message}";
                    db.AuditEvents.Add(new AuditEvent
                    {
                        Actor = "system", Action = "presentation.add-to-lesson", Object = targetLessonId.ToString(),
                        Result = "failed", Summary = source.ConversionError
                    });
                    await db.SaveChangesAsync(ct);
                    logger.LogWarning(addError, "Converted presentation {Presentation} could not be added to lesson {Lesson}",
                        source.FileName, targetLessonId);
                }
            }
            try { await hub.Clients.All.SendAsync("ManifestInvalidated", new { type = "MANIFEST_INVALIDATED" }, ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not signal completed presentation conversion"); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            foreach (var path in installed) TryDelete(path);
            db.ChangeTracker.Entries<MediaAsset>().Where(entry => entry.State == EntityState.Added).ToList()
                .ForEach(entry => entry.State = EntityState.Detached);
            source.ConversionStatus = "failed";
            source.ConvertedSlidesJson = "[]";
            source.ConvertedAt = null;
            source.ConversionError = SafeConversionError(ex, work);
            await db.SaveChangesAsync(ct);
            try { await hub.Clients.All.SendAsync("ManifestInvalidated", new { type = "MANIFEST_INVALIDATED" }, ct); }
            catch (Exception signalError) { logger.LogDebug(signalError, "Could not signal failed presentation conversion"); }
            logger.LogWarning(ex, "Could not convert presentation {Presentation}", source.FileName);
        }
        finally { TryDeleteDirectory(work); }
    }

    private static Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        string work, CancellationToken ct) =>
        ConstrainedProcessRunner.RunAsync(executable, arguments,
            ConstrainedProcessOptions.Document(work), ct);

    private static string? FindExecutable(string environmentName, string command, params string[] candidates)
    {
        var configured = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory, OperatingSystem.IsWindows() ? command + ".exe" : command);
            if (File.Exists(path)) return path;
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static int PageNumber(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var page) ? page : int.MaxValue;
    }

    private static string SafeConversionError(Exception error, string work)
    {
        var message = error.Message.Replace(work, "the temporary conversion area", StringComparison.OrdinalIgnoreCase);
        message = Regex.Replace(message, @"(?i)(password|token|secret|key)\s*[=:]\s*\S+", "$1=[redacted]");
        if (message.Contains("Syntax Error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid page", StringComparison.OrdinalIgnoreCase))
            message = "The local PDF renderer could not read this document. Verify that it opens normally, export it as a standard PDF, then retry.";
        return message.Length > 900 ? message[..900] : message;
    }

    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static string ConciseConversionOutput(string output)
    {
        var value = Regex.Replace(output.Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
        return value.Length > 700 ? value[..700] : value;
    }

    private static string AppendTag(string existing, string tag)
    {
        var tags = existing.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        var value = string.Join(", ", tags);
        return value.Length <= 500 ? value : value[..500].TrimEnd(' ', ',');
    }

    private static string? ResolveStoredFile(string root, string relative)
    {
        return ContainedPath.ResolveExistingFile(root, relative);
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
