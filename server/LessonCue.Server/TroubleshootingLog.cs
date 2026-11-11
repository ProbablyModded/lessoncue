using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LessonCue.Server;

/// <summary>
/// A deliberately small local log for the Settings troubleshooting view. It is separate from
/// the system journal so Service Admins can diagnose a server from the browser without giving
/// other roles access to credentials or shell commands.
/// </summary>
public sealed class TroubleshootingLog : ILoggerProvider
{
    private const int MaximumEntries = 2_000;
    private const int MaximumFailureEntries = 10_000;
    private const long MaximumFileBytes = 4 * 1024 * 1024;
    private const long MaximumFailureFileBytes = 16 * 1024 * 1024;
    internal const int FailureRetentionDays = 7;
    private static readonly TimeSpan FailureRetention = TimeSpan.FromDays(FailureRetentionDays);
    private const int MaximumDetailsLength = 12_000;
    private static readonly Regex CredentialPattern = new(
        """(?ix)(?<prefix>["']?(?:authorization|api[ _-]?key|token|password|secret)["']?\s*[:=]\s*["']?)(?:(?:bearer|basic)\s+)?(?<secret>[^"'\s,;}&]+)|(?<prefix>\b(?:authorization|api[ _-]?key|token|password|secret)\s+)(?:(?:bearer|basic)\s+)?(?<secret>[^"'\s,;}&]+)""",
        RegexOptions.Compiled);
    private readonly ConcurrentQueue<TroubleshootingLogEntry> entries = new();
    private readonly ConcurrentQueue<TroubleshootingLogEntry> failures = new();
    private readonly object fileLock = new();
    private readonly string filePath;
    private readonly string failureFilePath;

    public TroubleshootingLog(string dataPath)
    {
        var logPath = Path.Combine(dataPath, "logs");
        Directory.CreateDirectory(logPath);
        filePath = Path.Combine(logPath, "troubleshooting.jsonl");
        failureFilePath = Path.Combine(logPath, "troubleshooting-failures.jsonl");
        LoadRecentEntries();
    }

    public ILogger CreateLogger(string categoryName) => new TroubleshootingLogger(this, categoryName);

    public IReadOnlyList<TroubleshootingLogEntry> GetRecent(int limit, bool failuresOnly = false)
    {
        var safeLimit = Math.Clamp(limit, 1, failuresOnly ? MaximumFailureEntries : MaximumEntries);
        if (failuresOnly)
        {
            lock (fileLock) PruneFailuresNoLock(DateTimeOffset.UtcNow);
            return failures.OrderByDescending(entry => entry.Timestamp).Take(safeLimit).ToArray();
        }
        return entries.Reverse().Take(safeLimit).ToArray();
    }

    public void Dispose() { }

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        if (level < LogLevel.Information || category.StartsWith("Microsoft.", StringComparison.Ordinal) && level < LogLevel.Warning)
            return;
        if (level == LogLevel.Information && IsRoutineHttpInformation(category, message))
            return;

        var isFailure = IsFailure(level, exception);
        var error = isFailure
            ? TroubleshootingErrorCatalog.ForRuntime(category, eventId.Name ?? eventId.Id.ToString(), message, exception)
            : null;
        var entry = new TroubleshootingLogEntry(
            DateTimeOffset.UtcNow,
            level.ToString(),
            category.Replace("LessonCue.Server.", "", StringComparison.Ordinal),
            eventId.Name ?? eventId.Id.ToString(),
            Redact(message),
            exception is null ? null : Redact(exception.Message),
            exception?.GetType().FullName,
            exception is null ? null : Redact(exception.ToString(), MaximumDetailsLength),
            isFailure,
            error?.Code,
            error?.Description);
        entries.Enqueue(entry);
        while (entries.Count > MaximumEntries && entries.TryDequeue(out _)) { }

        try
        {
            lock (fileLock)
            {
                if (File.Exists(filePath) && new FileInfo(filePath).Length > MaximumFileBytes)
                    File.Move(filePath, filePath + ".previous", true);
                File.AppendAllText(filePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
                if (isFailure)
                {
                    failures.Enqueue(entry);
                    PruneFailuresNoLock(entry.Timestamp);
                    PersistFailuresNoLock();
                }
            }
        }
        catch
        {
            // A full or read-only disk must not take down LessonCue merely because diagnostics cannot be saved.
        }
    }

    private void LoadRecentEntries()
    {
        try
        {
            var recent = ReadEntries(filePath).TakeLast(MaximumEntries).ToArray();
            foreach (var entry in recent)
            {
                entries.Enqueue(Normalize(entry));
            }

            var loadedFailures = ReadEntries(failureFilePath)
                .Select(Normalize)
                .Where(entry => entry.IsFailure)
                .ToArray();
            var failureEntries = loadedFailures
                .Concat(recent.Select(Normalize).Where(entry => entry.IsFailure))
                .GroupBy(FailureKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(entry => entry.Timestamp);
            foreach (var entry in failureEntries) failures.Enqueue(entry);

            lock (fileLock)
            {
                PruneFailuresNoLock(DateTimeOffset.UtcNow);
                PersistFailuresNoLock();
            }
        }
        catch
        {
            // A corrupt diagnostic file is not application data and can safely be ignored.
        }
    }

    private static IEnumerable<TroubleshootingLogEntry> ReadEntries(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        {
            TroubleshootingLogEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<TroubleshootingLogEntry>(line); }
            catch { /* Ignore an individual corrupt diagnostic entry. */ }
            if (entry is not null) yield return entry;
        }
    }

    private void PruneFailuresNoLock(DateTimeOffset now)
    {
        var cutoff = now - FailureRetention;
        var retained = failures
            .Where(entry => entry.Timestamp >= cutoff)
            .OrderBy(entry => entry.Timestamp)
            .ToArray();
        while (failures.TryDequeue(out _)) { }
        foreach (var entry in retained) failures.Enqueue(entry);
    }

    private void PersistFailuresNoLock()
    {
        var serialized = failures
            .Select(entry => JsonSerializer.Serialize(entry) + Environment.NewLine)
            .ToList();
        var bytes = serialized.Sum(line => Encoding.UTF8.GetByteCount(line));
        while (serialized.Count > 1 && bytes > MaximumFailureFileBytes)
        {
            bytes -= Encoding.UTF8.GetByteCount(serialized[0]);
            serialized.RemoveAt(0);
            failures.TryDequeue(out _);
        }

        if (serialized.Count == 0)
        {
            try { File.Delete(failureFilePath); } catch { }
            return;
        }
        File.WriteAllText(failureFilePath, string.Concat(serialized));
    }

    private static TroubleshootingLogEntry Normalize(TroubleshootingLogEntry entry) =>
        entry with
        {
            IsFailure = IsFailure(entry)
        };

    private static bool IsFailure(TroubleshootingLogEntry entry) =>
        Enum.TryParse<LogLevel>(entry.Level, true, out var level) && level >= LogLevel.Error ||
        entry.Exception is not null;

    private static bool IsFailure(LogLevel level, Exception? exception) =>
        level >= LogLevel.Error || exception is not null;

    private static bool IsRoutineHttpInformation(string category, string message)
    {
        if (!category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal) &&
            !category.StartsWith("Microsoft.Extensions.Http.Logging.", StringComparison.Ordinal))
            return false;

        // Keep non-success response lines: they are often the only clue that
        // an integration is failing. The request-start/request-end lines for
        // ordinary 2xx traffic are routine and drown out the useful log.
        var status = Regex.Match(message, @"(?:-\s*|status(?: code)?\s*[:=]?\s*)(?<status>[3-5]\d{2})\b",
            RegexOptions.IgnoreCase);
        return !status.Success || int.Parse(status.Groups["status"].Value) < 300;
    }

    private static string FailureKey(TroubleshootingLogEntry entry) =>
        $"{entry.Timestamp:O}|{entry.Category}|{entry.Event}|{entry.Message}";

    private static string Redact(string value, int maximumLength = 2_000)
    {
        var trimmed = value.Length > maximumLength ? value[..maximumLength] + "…" : value;
        return CredentialPattern.Replace(trimmed, match => $"{match.Groups["prefix"].Value}[redacted]");
    }

    private sealed class TroubleshootingLogger(TroubleshootingLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => owner.Write(logLevel, category, eventId, formatter(state, exception), exception);
    }
}

public sealed record TroubleshootingLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Event,
    string Message,
    string? Exception,
    string? ExceptionType = null,
    string? Details = null,
    bool IsFailure = false,
    string? ErrorCode = null,
    string? ErrorDescription = null);
