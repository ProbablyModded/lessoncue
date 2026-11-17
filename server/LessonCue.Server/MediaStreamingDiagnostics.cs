using System.Net.Sockets;

namespace LessonCue.Server;

/// <summary>
/// Identifies transport failures that happen after a media response has
/// started. A display, browser, or proxy can legitimately abandon a large
/// range request, so these must not be recorded as server application faults.
/// </summary>
internal static class MediaStreamingDiagnostics
{
    private static readonly SocketError[] DownstreamSocketErrors =
    [
        SocketError.TimedOut,
        SocketError.ConnectionReset,
        SocketError.ConnectionAborted,
        SocketError.OperationAborted,
        SocketError.Shutdown,
        SocketError.NotConnected,
        SocketError.NetworkReset
    ];

    public static bool IsMediaResponsePath(string path) =>
        path.StartsWith("/api/v1/media/", StringComparison.Ordinal) &&
        (path.EndsWith("/file", StringComparison.Ordinal) ||
         path.EndsWith("/playback", StringComparison.Ordinal) ||
         path.Contains("/transcodes/", StringComparison.Ordinal) ||
         path.EndsWith("/thumbnail", StringComparison.Ordinal) ||
         path.EndsWith("/filmstrip", StringComparison.Ordinal) ||
         path.EndsWith("/waveform", StringComparison.Ordinal));

    public static bool IsDownstreamDisconnect(HttpContext context, Exception error)
    {
        if (!IsMediaResponsePath(context.Request.Path.Value ?? "")) return false;
        if (context.RequestAborted.IsCancellationRequested) return true;

        return HasDownstreamTransportError(error);
    }

    /// <summary>
    /// Kestrel can report a response-body disconnect from its own
    /// TimingPipeFlusher after the endpoint pipeline has already returned.
    /// There is no HttpContext available at that logging boundary, so use the
    /// Kestrel output-pipeline marker plus a transport exception to distinguish
    /// an abandoned media stream from an application failure.
    /// </summary>
    public static bool IsExpectedKestrelDisconnect(
        string category, string message, Exception? error) =>
        category.Equals("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal) &&
        IsKestrelOutputPipelineMessage(message, error) &&
        error is not null && HasDownstreamTransportError(error);

    public static bool IsExpectedKestrelDisconnect(TroubleshootingLogEntry entry) =>
        entry.Category.Equals("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal) &&
        IsKestrelOutputPipelineMessage(entry.Message, entry.Details) &&
        IsRecordedTransportError(entry);

    private static bool HasDownstreamTransportError(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && DownstreamSocketErrors.Contains(socket.SocketErrorCode))
                return true;
            if (current is IOException && IsTransportMessage(current.Message))
                return true;
        }

        return false;
    }

    private static bool IsRecordedTransportError(TroubleshootingLogEntry entry) =>
        entry.ExceptionType == typeof(SocketException).FullName &&
        IsTransportMessage(entry.Exception) ||
        IsTransportMessage(entry.Exception) ||
        IsTransportMessage(entry.Details);

    private static bool IsKestrelOutputPipelineMessage(string message, Exception? error) =>
        IsKestrelOutputPipelineMessage(message, error?.ToString());

    private static bool IsKestrelOutputPipelineMessage(string message, string? details)
    {
        var text = $"{message}\n{details}";
        return text.Contains("TimingPipeFlusher", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("PipeWriter", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("OutputProducer", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SendFile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransportMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("connection timed out", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase));
}
