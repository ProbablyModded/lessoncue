using System.IO.Compression;
using System.Text.Json;
using LessonCue.Server.Shortener;
using Microsoft.EntityFrameworkCore;

namespace LessonCue.Server;

/// <summary>
/// Builds the same failures-only evidence used by the daily AI troubleshooting
/// review. Keeping this outside the HTTP endpoint means scheduled email and
/// manual downloads cannot drift apart.
/// </summary>
public sealed class TroubleshootingReportBuilder(
    TroubleshootingLog log,
    MediaStoragePaths paths,
    YouTubeRuntimeUpdateService youtubeRuntime,
    ShortenerService shortener,
    BackupPolicyService backupPolicy)
{
    private static readonly TimeSpan ShortenerDiagnosticBudget = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<TroubleshootingReport> BuildAsync(
        LessonCueDb db, int requestedLimit = 2_000, bool failuresOnly = true,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(requestedLimit, 1, failuresOnly ? 10_000 : 2_000);
        var diagnosticErrors = new List<string>();
        IReadOnlyList<AuditEvent> audit;
        try
        {
            var auditQuery = db.AuditEvents.AsNoTracking();

            var auditCandidates = await auditQuery.OrderByDescending(x => x.Id)
                // SQLite cannot translate DateTimeOffset comparisons. Scan a
                // bounded recent tail, then apply the seven-day cutoff in
                // memory so the report remains portable across providers.
                .Take(failuresOnly ? Math.Max(limit, 10_000) : limit)
                .ToListAsync(ct);
            var retainedAudit = failuresOnly
                ? ApplyFailureAuditFilter(auditCandidates, DateTimeOffset.UtcNow).Take(limit)
                : auditCandidates;
            audit = retainedAudit
                .OrderByDescending(x => x.Timestamp)
                .ToArray();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordIssue("audit", error, diagnosticErrors);
            audit = [];
        }

        IReadOnlyList<MediaDiagnosticSnapshot> media;
        try { media = await MediaDiagnostics.BuildAsync(db, paths, Math.Min(limit, 100), ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordIssue("media", error, diagnosticErrors);
            media = [];
        }

        IReadOnlyList<TroubleshootingScreenDiagnostic> screens;
        try
        {
            screens = (await db.Screens.AsNoTracking().Select(x => new
            {
                x.Id, x.Name, x.Platform, x.AppVersion, x.DeviceModel, x.OsVersion, x.LastSeenAt,
                x.LastIpAddress, x.FailedDownloads, x.CachedItems, x.TotalItems, x.PlaybackState,
                x.PlaybackError, x.CacheInventoryJson, x.DownloadQueueJson, x.RecentErrorsJson,
                x.ConnectionDiagnosticsJson, x.DiagnosticsUpdatedAt
            }).ToListAsync(ct))
                .OrderByDescending(x => x.LastSeenAt)
                .Take(100)
                .Select(x => new TroubleshootingScreenDiagnostic(
                    x.Id, x.Name, x.Platform, x.AppVersion, x.DeviceModel, x.OsVersion, x.LastSeenAt,
                    x.LastIpAddress, x.FailedDownloads, x.CachedItems, x.TotalItems, x.PlaybackState,
                    x.PlaybackError, x.CacheInventoryJson, x.DownloadQueueJson, x.RecentErrorsJson,
                    x.ConnectionDiagnosticsJson, x.DiagnosticsUpdatedAt))
                .ToArray();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordIssue("screens", error, diagnosticErrors);
            screens = [];
        }

        object mediaDependencies;
        try
        {
            // Keep the existing dependency shape stable for the daily review
            // while adding the independent runtime state beside it.
            mediaDependencies = MediaDependencyDiagnostics.Build(paths, youtubeRuntime.Status);
        }
        catch (Exception error)
        {
            RecordIssue("media dependencies", error, diagnosticErrors);
            mediaDependencies = new { error = "dependency diagnostics unavailable" };
        }

        TroubleshootingBackupDiagnostic? backupDiagnostics = null;
        try
        {
            var timeZone = await db.Organizations.AsNoTracking()
                .OrderBy(item => item.Id)
                .Select(item => item.TimeZone)
                .FirstOrDefaultAsync(ct) ?? "UTC";
            var status = backupPolicy.GetStatus(timeZone);
            backupDiagnostics = new TroubleshootingBackupDiagnostic(
                status.Enabled,
                status.Frequency,
                status.HourLocal,
                status.WeeklyDay,
                status.MediaMode,
                status.LastAttemptAt,
                status.LastSucceededAt,
                status.LastVerifiedAt,
                status.LastBackupFileName,
                status.LastError,
                status.NextRunAt,
                status.Overdue,
                status.Running,
                (status.Destinations ?? []).Select(destination => new TroubleshootingBackupDestinationDiagnostic(
                    destination.Provider,
                    destination.Enabled,
                    Uri.TryCreate(destination.WebDavUrl, UriKind.Absolute, out var endpoint)
                        ? endpoint.Host
                        : null,
                    destination.FolderName,
                    destination.LastUploadedAt,
                    destination.LastUploadedFileName,
                    destination.RemoteBackupCount,
                    destination.LastError,
                    destination.LastMediaSyncAt,
                    destination.LastMediaSyncAdded,
                    destination.LastMediaSyncUpdated,
                    destination.LastMediaSyncDeleted)).ToArray());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordIssue("backup policy", error, diagnosticErrors);
        }

        object shortenerDiagnostics;
        using var shortenerBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shortenerBudget.CancelAfter(ShortenerDiagnosticBudget);
        try
        {
            var status = await shortener.StatusAsync(shortenerBudget.Token);
            var checks = await shortener.ProbeAsync(shortenerBudget.Token);
            shortenerDiagnostics = new
            {
                state = status.State.ToString(),
                status.Enabled,
                status.Domain,
                status.PublicUrl,
                status.AdminUrl,
                status.PoolTotal,
                status.PoolPresent,
                status.PoolActive,
                status.Detail,
                missing = status.Missing,
                conflicts = status.Conflicts,
                failures = status.Failures,
                checks,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timeout = new TimeoutException($"shortener diagnostics exceeded {ShortenerDiagnosticBudget.TotalSeconds:0} seconds");
            RecordIssue("shortener", timeout, diagnosticErrors);
            shortenerDiagnostics = new { state = "unavailable", error = "timeout" };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordIssue("shortener", error, diagnosticErrors);
            shortenerDiagnostics = new { state = "unavailable", error = error.GetType().Name };
        }

        var runtime = log.GetRecent(limit, failuresOnly);
        var issues = TroubleshootingIssueBuilder.Build(
            runtime, audit, media, screens, diagnosticErrors);

        return new TroubleshootingReport(
            DateTimeOffset.UtcNow,
            runtime,
            audit,
            media,
            mediaDependencies,
            backupDiagnostics,
            shortenerDiagnostics,
            screens,
            new TroubleshootingRetentionDiagnostic(
                2_000, TroubleshootingLog.FailureRetentionDays,
                "routine events: last 4 MB plus the prior rotated file; runtime and audit failures: separate seven-day store"),
            diagnosticErrors,
            issues);
    }

    private static void RecordIssue(string component, Exception error, List<string> issues)
    {
        issues.Add($"{component}: unavailable ({error.GetType().Name})");
    }

    public static byte[] ToJson(TroubleshootingReport report) =>
        JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);

    public static byte[] ToGzip(TroubleshootingReport report)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(gzip, report, JsonOptions);
        return output.ToArray();
    }

    internal static IEnumerable<AuditEvent> ApplyFailureAuditFilter(
        IEnumerable<AuditEvent> events, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-TroubleshootingLog.FailureRetentionDays);
        // Audit summaries are evidence, not status. A successful event can
        // legitimately contain words such as "Failures-only" in its summary,
        // so Result is the authoritative success signal here. Apply the same
        // detailed problem classifier used to build the report issues in
        // memory. This also avoids treating a successful event whose summary
        // contains "Failures-only" as a failure.
        return events.Where(item => item.Timestamp >= cutoff &&
            !string.Equals(item.Result, "success", StringComparison.OrdinalIgnoreCase) &&
            TroubleshootingIssueBuilder.IsProblem(item));
    }
}

public sealed record TroubleshootingReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<TroubleshootingLogEntry> Runtime,
    IReadOnlyList<AuditEvent> Audit,
    IReadOnlyList<MediaDiagnosticSnapshot> Media,
    object MediaDependencies,
    TroubleshootingBackupDiagnostic? Backup,
    object Shortener,
    IReadOnlyList<TroubleshootingScreenDiagnostic> Screens,
    TroubleshootingRetentionDiagnostic Retention,
    IReadOnlyList<string> DiagnosticErrors,
    IReadOnlyList<TroubleshootingIssue> Issues)
{
    public int MediaAttentionCount => Media.Count(item =>
        !string.Equals(item.ProcessingStatus, "ready", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(item.ProcessingError) ||
        string.Equals(item.CompatibilityStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(item.CompatibilityError) ||
        item.OriginalFile?.SizeAndSha256Match == false ||
        item.CompatibilityFile?.SizeAndSha256Match == false ||
        MediaFormatCatalog.IsVideo(Path.GetExtension(item.RelativePath), item.ContentType) &&
        item.Transcodes.Any(variant => string.Equals(variant.Status, "failed", StringComparison.OrdinalIgnoreCase)));

    public int ScreenAttentionCount => Screens.Count(item =>
        TroubleshootingIssueBuilder.HasFreshScreenDiagnostics(item, DateTimeOffset.UtcNow) &&
        (item.FailedDownloads > 0 || !string.IsNullOrWhiteSpace(item.PlaybackError) ||
         !string.Equals(item.RecentErrorsJson, "[]", StringComparison.Ordinal)));
}

public sealed record TroubleshootingBackupDiagnostic(
    bool Enabled,
    string Frequency,
    int HourLocal,
    int? WeeklyDay,
    string MediaMode,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? LastVerifiedAt,
    string? LastBackupFileName,
    string? LastError,
    DateTimeOffset? NextRunAt,
    bool Overdue,
    bool Running,
    IReadOnlyList<TroubleshootingBackupDestinationDiagnostic> Destinations);

public sealed record TroubleshootingBackupDestinationDiagnostic(
    string Provider,
    bool Enabled,
    string? Host,
    string? FolderName,
    DateTimeOffset? LastUploadedAt,
    string? LastUploadedFileName,
    int? RemoteBackupCount,
    string? LastError,
    DateTimeOffset? LastMediaSyncAt,
    int? LastMediaSyncAdded,
    int? LastMediaSyncUpdated,
    int? LastMediaSyncDeleted);

public sealed record TroubleshootingScreenDiagnostic(
    Guid Id,
    string Name,
    string Platform,
    string AppVersion,
    string? DeviceModel,
    string? OsVersion,
    DateTimeOffset? LastSeenAt,
    string? LastIpAddress,
    int FailedDownloads,
    int CachedItems,
    int TotalItems,
    string PlaybackState,
    string? PlaybackError,
    string CacheInventoryJson,
    string DownloadQueueJson,
    string RecentErrorsJson,
    string ConnectionDiagnosticsJson,
    DateTimeOffset? DiagnosticsUpdatedAt);

public sealed record TroubleshootingRetentionDiagnostic(
    int RuntimeEntries,
    int FailureRetentionDays,
    string File);

public sealed record TroubleshootingIssue(
    string Code,
    string Severity,
    string Component,
    string Summary,
    string Description,
    string RecommendedAction,
    string Evidence,
    int Occurrences,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt);

public static class TroubleshootingIssueBuilder
{
    private sealed record Draft(
        string Code,
        string Severity,
        string Component,
        string Summary,
        string Description,
        string RecommendedAction,
        string Evidence,
        DateTimeOffset? FirstSeenAt,
        DateTimeOffset? LastSeenAt);

    public static IReadOnlyList<TroubleshootingIssue> Build(
        IReadOnlyList<TroubleshootingLogEntry> runtime,
        IReadOnlyList<AuditEvent> audit,
        IReadOnlyList<MediaDiagnosticSnapshot> media,
        IReadOnlyList<TroubleshootingScreenDiagnostic> screens,
        IReadOnlyList<string> diagnosticErrors)
    {
        var drafts = new List<Draft>();
        foreach (var entry in runtime.Where(item => item.IsFailure))
        {
            var definition = string.IsNullOrWhiteSpace(entry.ErrorCode)
                ? TroubleshootingErrorCatalog.ForRuntime(entry.Category, entry.Event, entry.Message)
                : new TroubleshootingErrorDefinition(
                    entry.ErrorCode!,
                    entry.ErrorDescription ?? "A runtime failure was recorded; inspect the attached evidence.",
                    "Inspect the recorded evidence and reproduce the failing subsystem before changing code or configuration.");
            var evidence = $"{entry.Timestamp:O} {entry.Category}/{entry.Event}: {entry.Message}"
                + (string.IsNullOrWhiteSpace(entry.Exception) ? "" : $" Exception: {entry.Exception}");
            drafts.Add(new Draft(
                definition.Code, Severity(entry.Level), entry.Category, entry.Message,
                definition.Description, definition.RecommendedAction, Limit(evidence, 4_000),
                entry.Timestamp, entry.Timestamp));
        }

        foreach (var entry in audit.Where(IsProblem))
        {
            var definition = TroubleshootingErrorCatalog.ForAudit(entry.Action, entry.Summary, entry.Result);
            drafts.Add(new Draft(
                definition.Code, entry.Result.Contains("defer", StringComparison.OrdinalIgnoreCase) ? "warning" : "error",
                "Audit", entry.Summary ?? entry.Action, definition.Description, definition.RecommendedAction,
                Limit($"{entry.Timestamp:O} {entry.Action} ({entry.Result}): {entry.Summary}", 4_000),
                entry.Timestamp, entry.Timestamp));
        }

        foreach (var error in diagnosticErrors)
        {
            var component = error.Split(':', 2)[0].Trim();
            drafts.Add(new Draft(
                $"LC.DIAGNOSTICS.{TroubleshootingErrorCatalog.ComponentSlug(component)}_UNAVAILABLE",
                "error", component, error,
                "A troubleshooting component could not collect its evidence, so the report may be incomplete.",
                "Repair the named diagnostic dependency or inspect the corresponding service journal before trusting an absence of evidence.",
                error, null, null));
        }

        foreach (var asset in media) AddMediaIssues(drafts, asset);
        var generatedAt = DateTimeOffset.UtcNow;
        foreach (var screen in screens) AddScreenIssues(drafts, screen, generatedAt);

        return drafts
            .GroupBy(item => $"{item.Code}|{item.Summary}", StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.FirstSeenAt).ToArray();
                var latest = ordered[^1];
                return new TroubleshootingIssue(
                    latest.Code, latest.Severity, latest.Component, latest.Summary,
                    latest.Description, latest.RecommendedAction, latest.Evidence,
                    ordered.Length, ordered[0].FirstSeenAt, latest.LastSeenAt);
            })
            .OrderByDescending(item => SeverityRank(item.Severity))
            .ThenByDescending(item => item.LastSeenAt)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddMediaIssues(List<Draft> drafts, MediaDiagnosticSnapshot asset)
    {
        var label = $"{asset.FileName} ({asset.Id})";
        if (asset.OriginalFile is { Exists: false } original)
        {
            Add(drafts, "LC.MEDIA.ORIGINAL.MISSING", "error", "Media", label,
                "The recorded original file does not exist at the expected path.",
                "Restore or repair the storage path only after preserving the MediaAsset record and checking the configured originals directory.",
                $"RelativePath={original.RelativePath}; ProcessingStatus={asset.ProcessingStatus}; ExpectedSizeBytes={asset.SizeBytes}; ExpectedSha256={asset.Sha256}", asset.CreatedAt);
        }
        else if (asset.OriginalFile?.SizeAndSha256Match == false)
        {
            Add(drafts, "LC.MEDIA.ORIGINAL.INTEGRITY_MISMATCH", "error", "Media", label,
                "The original file exists but its recorded size or SHA-256 does not match the database.",
                "Do not overwrite the file or re-upload automatically; preserve the evidence and determine whether storage or the upload pipeline changed it.",
                $"RelativePath={asset.RelativePath}; DiskSizeBytes={asset.OriginalFile.DiskSizeBytes}; ExpectedSizeBytes={asset.SizeBytes}; DiskSha256={asset.OriginalFile.DiskSha256}; ExpectedSha256={asset.Sha256}", asset.CreatedAt);
        }
        else if (!string.IsNullOrWhiteSpace(asset.OriginalFile?.CheckError))
        {
            Add(drafts, "LC.MEDIA.ORIGINAL.UNREADABLE", "error", "Media", label,
                "LessonCue could not read or verify the original file.",
                "Check service-user permissions, storage readability, and the exact file-system error before changing the asset.",
                $"RelativePath={asset.RelativePath}; CheckError={asset.OriginalFile.CheckError}", asset.CreatedAt);
        }

        if (string.Equals(asset.ProcessingStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            Add(drafts, "LC.MEDIA.PROCESSING.FAILED", "error", "Media", label,
                "Media processing is marked failed.",
                "Use the stored ProcessingError and original-file checks to distinguish corrupt media from a worker/runtime failure, then retry only if recoverable.",
                $"ProcessingStatus={asset.ProcessingStatus}; ProcessingError={asset.ProcessingError}; SourceKind={asset.SourceKind}", asset.CreatedAt);
        }
        else if (!string.IsNullOrWhiteSpace(asset.ProcessingError))
        {
            Add(drafts, "LC.MEDIA.PROCESSING.DEFERRED", "warning", "Media", label,
                "Media processing has a recorded error even though the asset may remain playable from its original.",
                "Read the exact ProcessingError and worker diagnostics; preserve the intact original and retry after infrastructure problems are corrected.",
                $"ProcessingStatus={asset.ProcessingStatus}; ProcessingError={asset.ProcessingError}; OfflineEligible={asset.OfflineEligible}", asset.CreatedAt);
        }
        else if (!string.Equals(asset.ProcessingStatus, "ready", StringComparison.OrdinalIgnoreCase))
        {
            Add(drafts, "LC.MEDIA.PROCESSING.NOT_READY", "warning", "Media", label,
                "Media processing has not reached the ready state.",
                "Check whether the worker is running, whether the job is queued or stuck, and whether the original file is readable.",
                $"ProcessingStatus={asset.ProcessingStatus}; CreatedAt={asset.CreatedAt:O}", asset.CreatedAt);
        }

        if (string.Equals(asset.CompatibilityStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(asset.CompatibilityError))
        {
            Add(drafts, "LC.MEDIA.COMPATIBILITY.FAILED", "error", "Media", label,
                "Compatibility processing is marked failed or has a recorded conversion error.",
                "Confirm the original is still playable, inspect converter/runtime diagnostics, and use the retry action for infrastructure failures.",
                $"CompatibilityStatus={asset.CompatibilityStatus}; CompatibilityError={asset.CompatibilityError}; CompatibilityPath={asset.CompatibilityPath}", asset.CreatedAt);
        }
        else if (asset.CompatibilityStatus is "pending" or "converting")
        {
            Add(drafts, "LC.MEDIA.COMPATIBILITY.NOT_READY", "warning", "Media", label,
                "Optional compatibility processing has not completed.",
                "The original may still be playable; inspect the queue and worker state before treating this as an upload failure.",
                $"CompatibilityStatus={asset.CompatibilityStatus}; CompatibilityPath={asset.CompatibilityPath}; OfflineEligible={asset.OfflineEligible}", asset.CreatedAt);
        }

        if (asset.CompatibilityFile?.SizeAndSha256Match == false)
        {
            Add(drafts, "LC.MEDIA.COMPATIBILITY.INTEGRITY_MISMATCH", "error", "Media", label,
                "The recorded compatibility file exists but its size or SHA-256 does not match.",
                "Discard only the derived copy through the existing retry/rebuild path; preserve the original and compare the recorded hashes first.",
                $"CompatibilityPath={asset.CompatibilityFile.RelativePath}; DiskSizeBytes={asset.CompatibilityFile.DiskSizeBytes}; ExpectedSizeBytes={asset.CompatibilitySizeBytes}; DiskSha256={asset.CompatibilityFile.DiskSha256}; ExpectedSha256={asset.CompatibilitySha256}", asset.CreatedAt);
        }

        if (!MediaFormatCatalog.IsVideo(Path.GetExtension(asset.RelativePath), asset.ContentType)) return;

        foreach (var variant in asset.Transcodes.Where(item =>
                     string.Equals(item.Status, "failed", StringComparison.OrdinalIgnoreCase)))
        {
            Add(drafts, "LC.MEDIA.TRANSCODE.VARIANT_FAILED", "error", "Media", label,
                $"The {variant.Profile} compatibility variant failed.",
                "Inspect the variant error and source version, then retry the variant after repairing the converter dependency.",
                $"VariantId={variant.Id}; Profile={variant.Profile}; Status={variant.Status}; Error={variant.Error}; Engine={variant.TranscodeEngine}",
                variant.QueuedAt);
        }
    }

    internal static bool HasFreshScreenDiagnostics(
        TroubleshootingScreenDiagnostic screen, DateTimeOffset now) =>
        (screen.DiagnosticsUpdatedAt ?? screen.LastSeenAt) is { } updatedAt &&
        updatedAt >= now - TimeSpan.FromHours(48);

    private static void AddScreenIssues(
        List<Draft> drafts, TroubleshootingScreenDiagnostic screen, DateTimeOffset now)
    {
        if (!HasFreshScreenDiagnostics(screen, now)) return;
        var label = $"{screen.Name} ({screen.Id})";
        if (screen.FailedDownloads > 0)
            Add(drafts, "LC.TV.DOWNLOADS.FAILED", "error", "TV", label,
                $"The TV reports {screen.FailedDownloads} failed media download(s).",
                "Compare the failed item IDs, exact media URL responses, cache state, checksum, and selected server endpoint.",
                $"AppVersion={screen.AppVersion}; Platform={screen.Platform}; FailedDownloads={screen.FailedDownloads}; DownloadQueue={Limit(screen.DownloadQueueJson, 2_000)}",
                screen.DiagnosticsUpdatedAt ?? screen.LastSeenAt);
        if (!string.IsNullOrWhiteSpace(screen.PlaybackError))
            Add(drafts, "LC.TV.PLAYBACK.FAILED", "error", "TV", label,
                "The TV reports a playback error.",
                "Compare the manifest support decision, selected URL, cached file checksum, codec capability, and playback error timestamp.",
                $"AppVersion={screen.AppVersion}; PlaybackState={screen.PlaybackState}; PlaybackError={screen.PlaybackError}",
                screen.DiagnosticsUpdatedAt ?? screen.LastSeenAt);
        if (!string.IsNullOrWhiteSpace(screen.RecentErrorsJson) && screen.RecentErrorsJson != "[]")
            Add(drafts, "LC.TV.DIAGNOSTICS.RECENT_ERRORS", "warning", "TV", label,
                "The TV has recent diagnostic errors.",
                "Inspect the structured recent errors alongside the selected endpoint, cache inventory, and server manifest.",
                $"AppVersion={screen.AppVersion}; RecentErrors={Limit(screen.RecentErrorsJson, 3_000)}",
                screen.DiagnosticsUpdatedAt ?? screen.LastSeenAt);
        if (ContainsConnectionFailure(screen.ConnectionDiagnosticsJson))
            Add(drafts, "LC.TV.DISCOVERY.OR_CONNECTION_FAILED", "error", "TV", label,
                "The TV diagnostics contain a discovery or connection failure.",
                "Compare every IPv4/IPv6 candidate, rejection reason, selected endpoint, and health verification result; an unusable IPv6 candidate must not block IPv4 fallback.",
                $"AppVersion={screen.AppVersion}; ConnectionDiagnostics={Limit(screen.ConnectionDiagnosticsJson, 3_000)}",
                screen.DiagnosticsUpdatedAt ?? screen.LastSeenAt);
    }

    private static void Add(List<Draft> drafts, string code, string severity, string component,
        string summary, string description, string action, string evidence, DateTimeOffset? at)
    {
        drafts.Add(new Draft(code, severity, component, summary, description, action, Limit(evidence, 4_000), at, at));
    }

    internal static bool IsProblem(AuditEvent item)
    {
        if (string.Equals(item.Result, "success", StringComparison.OrdinalIgnoreCase)) return false;
        return item.Result.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
               item.Result.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               item.Result.Contains("defer", StringComparison.OrdinalIgnoreCase) ||
               item.Action.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
               item.Action.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               item.Action.Contains("defer", StringComparison.OrdinalIgnoreCase) ||
               item.Summary?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true ||
               item.Summary?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ||
               item.Summary?.Contains("defer", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsConnectionFailure(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("unreachable", StringComparison.OrdinalIgnoreCase));

    private static string Severity(string level) =>
        level.Equals("Critical", StringComparison.OrdinalIgnoreCase) || level.Equals("Error", StringComparison.OrdinalIgnoreCase)
            ? "error"
            : "warning";

    private static int SeverityRank(string severity) => severity == "error" ? 2 : 1;

    private static string Limit(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Length <= maximum ? value : value[..maximum] + "…";
}
