using LessonCue.Server;
using Xunit;

namespace LessonCue.Server.Tests;

public sealed class YouTubeMediaTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    public void ProducesEmbedUrlForSupportedYouTubeLinks(string value)
    {
        Assert.Equal("https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1&controls=1&rel=0", YouTubeMedia.EmbedUrl(value));
    }

    [Theory]
    [InlineData("https://youtube.com.example.test/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.test/youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("http://127.0.0.1/watch?v=dQw4w9WgXcQ")]
    public void RejectsLookalikeAndNonYouTubeHosts(string value)
    {
        Assert.Null(YouTubeMedia.EmbedUrl(value));
        Assert.False(YouTubeMedia.IsYouTubeUrl(new Uri(value)));
    }

    [Fact]
    public void RecognizesAValidSourceAsAStreamingFallback()
    {
        Assert.True(YouTubeMedia.HasStreamingFallback("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
        Assert.False(YouTubeMedia.HasStreamingFallback("https://example.test/video.mp4"));
    }

    [Fact]
    public void DownloadArgumentsConfigureTheBundledDenoRuntime()
    {
        var arguments = YouTubeImportService.BuildDownloadArguments(
            "/var/lib/lessoncue/temporary/%(id)s.%(ext)s",
            123456,
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "/opt/lessoncue/deno");

        var runtimeIndex = Array.IndexOf(arguments.ToArray(), "--js-runtimes");
        Assert.True(runtimeIndex >= 0);
        Assert.Equal("deno:/opt/lessoncue/deno", arguments[runtimeIndex + 1]);
        Assert.Contains("123456", arguments);
        Assert.Contains("--no-config", arguments);
        Assert.Contains("--no-playlist", arguments);
    }

    [Fact]
    public void DownloadArgumentsPreferTheHighestAvailableOriginalQualityUpTo4k()
    {
        var arguments = YouTubeImportService.BuildDownloadArguments(
            "/var/lib/lessoncue/temporary/%(id)s.%(ext)s",
            123456,
            "https://www.youtube.com/watch?v=c4PmpM058is",
            "/opt/lessoncue/deno");

        Assert.Contains("bestvideo+bestaudio/best", arguments);
        var sortIndex = Array.IndexOf(arguments.ToArray(), "-S");
        Assert.True(sortIndex >= 0);
        Assert.Equal("res:2160", arguments[sortIndex + 1]);
        var mergeIndex = Array.IndexOf(arguments.ToArray(), "--merge-output-format");
        Assert.True(mergeIndex >= 0);
        Assert.Equal("mkv", arguments[mergeIndex + 1]);
        Assert.DoesNotContain("--recode-video", arguments);
        Assert.DoesNotContain("--convert-video", arguments);
        Assert.Contains("3", arguments);
    }

    [Fact]
    public void FallbackDownloadArgumentsTryAndroidAdaptiveStreamsAtOriginalQuality()
    {
        var arguments = YouTubeImportService.BuildFallbackDownloadArguments(
            "/var/lib/lessoncue/temporary/%(id)s.%(ext)s",
            123456,
            "https://www.youtube.com/watch?v=c4PmpM058is",
            "/opt/lessoncue/deno");

        Assert.Contains("bestvideo+bestaudio/best", arguments);
        var extractorIndex = Array.IndexOf(arguments.ToArray(), "--extractor-args");
        Assert.True(extractorIndex >= 0);
        Assert.Equal("youtube:player_client=android", arguments[extractorIndex + 1]);
        var sortIndex = Array.IndexOf(arguments.ToArray(), "-S");
        Assert.Equal("res:2160", arguments[sortIndex + 1]);
        var mergeIndex = Array.IndexOf(arguments.ToArray(), "--merge-output-format");
        Assert.True(mergeIndex >= 0);
        Assert.Equal("mkv", arguments[mergeIndex + 1]);
        Assert.Contains("deno:/opt/lessoncue/deno", arguments);
    }

    [Fact]
    public void LastResortDownloadProfileKeepsAnAndroidProgressiveFallback()
    {
        var arguments = YouTubeImportService.BuildProgressiveFallbackDownloadArguments(
            "/var/lib/lessoncue/temporary/%(id)s.%(ext)s",
            123456,
            "https://www.youtube.com/watch?v=c4PmpM058is",
            "/opt/lessoncue/deno");

        Assert.Contains("best[ext=mp4]", arguments);
        Assert.Contains("youtube:player_client=android", arguments);
        Assert.DoesNotContain("--merge-output-format", arguments);
    }

    [Fact]
    public void NoJavaScriptFallbackAvoidsTheDenoRuntime()
    {
        var arguments = YouTubeImportService.BuildNoJavaScriptFallbackDownloadArguments(
            "/var/lib/lessoncue/temporary/%(id)s.%(ext)s",
            123456,
            "https://www.youtube.com/watch?v=c4PmpM058is",
            "/opt/lessoncue/deno");

        Assert.Contains("best[ext=mp4]/best", arguments);
        Assert.Contains("youtube:player_client=android", arguments);
        Assert.DoesNotContain("--js-runtimes", arguments);
        Assert.DoesNotContain("--merge-output-format", arguments);
    }

    [Fact]
    public void StreamingFallbackMessagePreservesTheRecoveryReason()
    {
        var message = YouTubeImportService.StreamingFallbackMessage("fatal process out of memory");

        Assert.Contains("streaming", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("out of memory", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fatal process out of memory: Oilpan: CagedHeap reservation failed")]
    [InlineData("The media worker exceeded its memory limit")]
    [InlineData("requested format is not available")]
    public void RetriesWithSaferYouTubeProfileAfterRecoverableDownloaderFailure(string message)
    {
        Assert.True(YouTubeImportService.ShouldTryAlternateProfile(
            new InvalidOperationException(message)));
    }

    [Fact]
    public void DoesNotRetryClearlyInvalidMediaInput()
    {
        Assert.False(YouTubeImportService.ShouldTryAlternateProfile(
            new InvalidOperationException("The URL is not a supported YouTube source.")));
    }
}
