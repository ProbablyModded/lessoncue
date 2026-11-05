using System.Text.Json;
using LessonCue.Server;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LessonCue.Server.Tests;

public sealed class PresentationConversionTests
{
    [Fact]
    public async Task ConvertedSlidesAreAddedInOrderAndExtendLessonRetention()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<LessonCueDb>().UseSqlite(connection).Options;
        await using var db = new LessonCueDb(options);
        await db.Database.EnsureCreatedAsync(ct);
        var lessonClass = new LessonClass { Name = "Training" };
        var lesson = new Lesson { ClassId = lessonClass.Id, Date = new DateOnly(2026, 9, 10), Title = "Orientation" };
        var first = new MediaAsset { FileName = "Deck — Slide 1", RelativePath = "slide-1.png",
            SourceKind = "presentation-slide", StoragePolicy = MediaRetention.LessonScoped };
        var second = new MediaAsset { FileName = "Deck — Slide 2", RelativePath = "slide-2.png",
            SourceKind = "presentation-slide", StoragePolicy = MediaRetention.LessonScoped };
        var source = new MediaAsset { FileName = "Deck.pdf", RelativePath = "deck.pdf", ConversionStatus = "ready",
            ConvertedSlidesJson = JsonSerializer.Serialize(new[] { first.Id, second.Id }) };
        db.AddRange(lessonClass, lesson, first, second, source);
        await db.SaveChangesAsync(ct);

        var added = await PresentationConversion.AddToLessonAsync(db, source, lesson, 12, "owner", ct);

        Assert.Equal(2, added);
        var items = await db.PlaylistItems.OrderBy(x => x.Position).ToListAsync(ct);
        Assert.Equal(new Guid?[] { first.Id, second.Id }, items.Select(x => x.MediaAssetId).ToArray());
        Assert.All(items, item => Assert.Equal(12_000, item.DurationMs));
        Assert.All(items, item => Assert.Equal(12, item.ImageDurationSeconds));
        Assert.Equal(MediaRetention.DeleteAfterFor(lesson.Date), first.DeleteAfter);
        Assert.Equal(MediaRetention.DeleteAfterFor(lesson.Date), second.DeleteAfter);
        Assert.Equal(2, lesson.Version);
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "presentation.add-to-lesson", ct));
    }

    [Fact]
    public async Task MissingSlideDurationCreatesUntimedStillCues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<LessonCueDb>().UseSqlite(connection).Options;
        await using var db = new LessonCueDb(options);
        await db.Database.EnsureCreatedAsync(ct);
        var lessonClass = new LessonClass { Name = "Training" };
        var lesson = new Lesson { ClassId = lessonClass.Id, Date = new DateOnly(2026, 9, 11), Title = "Untimed deck" };
        var slide = new MediaAsset { FileName = "Deck — Slide 1", RelativePath = "slide-1.png",
            SourceKind = "presentation-slide", StoragePolicy = MediaRetention.LessonScoped };
        var source = new MediaAsset { FileName = "Deck.pdf", RelativePath = "deck.pdf", ConversionStatus = "ready",
            ConvertedSlidesJson = JsonSerializer.Serialize(new[] { slide.Id }) };
        db.AddRange(lessonClass, lesson, slide, source);
        await db.SaveChangesAsync(ct);

        await PresentationConversion.AddToLessonAsync(db, source, lesson, null, "owner", ct);

        var item = await db.PlaylistItems.SingleAsync(ct);
        Assert.Null(item.DurationMs);
        Assert.Null(item.ImageDurationSeconds);
        Assert.Equal("pause", item.EndBehavior);
    }

    [Theory]
    [InlineData("slides.pdf")]
    [InlineData("legacy.ppt")]
    [InlineData("slides.pptx")]
    [InlineData("show.ppsx")]
    [InlineData("template.potx")]
    [InlineData("slides.odp")]
    [InlineData("keynote.key")]
    [InlineData("handout.doc")]
    [InlineData("handout.docx")]
    [InlineData("macro.pptm")]
    [InlineData("workbook.xlsx")]
    [InlineData("workbook.xlsm")]
    [InlineData("notes.odt")]
    [InlineData("table.ods")]
    [InlineData("notes.rtf")]
    [InlineData("notes.txt")]
    [InlineData("sheet.csv")]
    [InlineData("apple.pages")]
    public void SupportedDocumentsAreRecognized(string fileName) => Assert.True(PresentationConversion.IsConvertible(fileName));

    [Theory]
    [InlineData(".ppt", true, "pdf:impress_pdf_Export")]
    [InlineData(".pptx", true, "pdf:impress_pdf_Export")]
    [InlineData(".odp", true, "pdf:impress_pdf_Export")]
    [InlineData(".docx", false, "pdf")]
    [InlineData(".xlsx", false, "pdf")]
    public void LibreOfficeUsesThePresentationFilterForSlideDecks(string extension, bool presentation, string filter)
    {
        Assert.Equal(presentation, PresentationConversion.IsPresentationExtension(extension));
        Assert.Equal(filter, PresentationConversion.LibreOfficePdfFilter(extension));
    }

    [Theory]
    [InlineData("https://docs.google.com/presentation/d/1AbCdEfGhIjKlMnOpQrStUvWxYz_123-456/edit",
        "https://docs.google.com/presentation/d/1AbCdEfGhIjKlMnOpQrStUvWxYz_123-456/export/pdf")]
    [InlineData("https://docs.google.com/presentation/d/12345678/preview",
        "https://docs.google.com/presentation/d/12345678/export/pdf")]
    public void GoogleSlidesLinksBecomeBoundedPdfExports(string source, string expected)
    {
        Assert.True(PresentationConversion.TryGoogleSlidesExport(new Uri(source), out var export));
        Assert.Equal(expected, export.ToString());
    }

    [Theory]
    [InlineData("http://docs.google.com/presentation/d/12345678/edit")]
    [InlineData("https://example.org/presentation/d/12345678/edit")]
    [InlineData("https://docs.google.com/document/d/12345678/edit")]
    public void NonGoogleSlidesLinksAreRejected(string source) =>
        Assert.False(PresentationConversion.TryGoogleSlidesExport(new Uri(source), out _));
}
