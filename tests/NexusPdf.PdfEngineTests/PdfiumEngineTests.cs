using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Интеграционные проверки реального PDFium: это одновременно и «engine spike»
/// из ТЗ — фиксация фактических возможностей движка исполняемыми тестами.
/// </summary>
public sealed class PdfiumEngineTests : IAsyncLifetime
{
    private PdfiumRenderEngine _engine = null!;

    public Task InitializeAsync()
    {
        _engine = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    [Fact]
    public async Task Open_Reports_PageCount_And_Sizes()
    {
        var path = PdfFixture.WriteToTemp("simple.pdf",
            new PdfFixture.PageSpec(612, 792),
            new PdfFixture.PageSpec(500, 250));

        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        Assert.Equal(2, doc.Info.PageCount);
        Assert.Equal(612, doc.Info.Pages[0].WidthPoints, 1);
        Assert.Equal(792, doc.Info.Pages[0].HeightPoints, 1);
        Assert.Equal(500, doc.Info.Pages[1].WidthPoints, 1);
        Assert.Equal(250, doc.Info.Pages[1].HeightPoints, 1);
    }

    [Fact]
    public async Task Open_Accounts_For_Page_Rotation_In_Size()
    {
        var path = PdfFixture.WriteToTemp("rotated.pdf",
            new PdfFixture.PageSpec(612, 792, Rotate: 90));

        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        // Размер отдаётся с учётом /Rotate: книжная страница с поворотом 90° видна как альбомная.
        Assert.Equal(792, doc.Info.Pages[0].WidthPoints, 1);
        Assert.Equal(612, doc.Info.Pages[0].HeightPoints, 1);
    }

    [Fact]
    public async Task Open_Works_With_Cyrillic_Path()
    {
        var basePath = PdfFixture.WriteToTemp("temp.pdf", new PdfFixture.PageSpec(612, 792));
        var cyrillicPath = Path.Combine(Path.GetDirectoryName(basePath)!, "тестовый документ №1.pdf");
        File.Move(basePath, cyrillicPath);

        await using var doc = await _engine.OpenAsync(cyrillicPath, null, CancellationToken.None);
        Assert.Equal(1, doc.Info.PageCount);
    }

    [Fact]
    public async Task Open_Corrupted_File_Throws_Typed_Exception()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "broken.pdf");
        await File.WriteAllTextAsync(path, "это вовсе не PDF");

        await Assert.ThrowsAsync<PdfCorruptedException>(
            () => _engine.OpenAsync(path, null, CancellationToken.None));
    }

    [Fact]
    public async Task Render_Produces_Nonblank_Pixels()
    {
        var path = PdfFixture.WriteToTemp("render.pdf", new PdfFixture.PageSpec(612, 792));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var image = await doc.RenderPageAsync(0, 306, 396, 0, CancellationToken.None);

        Assert.Equal(306, image.PixelWidth);
        Assert.Equal(396, image.PixelHeight);
        Assert.Contains(false, EnumeratePixelWhiteness(image));
    }

    [Fact]
    public async Task Text_Layer_Is_Extracted()
    {
        var path = PdfFixture.WriteToTemp("text.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Hello NexusPDF"));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var text = await doc.GetPageTextAsync(0, CancellationToken.None);

        Assert.Contains("Hello NexusPDF", text);
    }

    [Fact]
    public async Task Text_Rects_Cover_Found_Fragment()
    {
        var path = PdfFixture.WriteToTemp("rects.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Hello NexusPDF"));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var text = await doc.GetPageTextAsync(0, CancellationToken.None);
        var index = text.IndexOf("Hello", StringComparison.Ordinal);
        Assert.True(index >= 0);

        var rects = await doc.GetTextRectsAsync(0, index, 5, CancellationToken.None);

        var rect = Assert.Single(rects);
        Assert.True(rect.Right > rect.Left);
        Assert.True(rect.Top > rect.Bottom);
    }

    [Fact]
    public async Task Compose_Reorders_Rotates_And_Subsets_Pages()
    {
        var path = PdfFixture.WriteToTemp("compose.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Page A"),
            new PdfFixture.PageSpec(500, 500, Text: "Page B"),
            new PdfFixture.PageSpec(300, 600, Text: "Page C"));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var target = Path.Combine(Path.GetDirectoryName(path)!, "composed.pdf");
        await _engine.ComposeAsync(new[]
        {
            new ComposedPage(doc, 2, 1),   // C, повёрнутая на 90°
            new ComposedPage(doc, 0, 0),   // A как есть
        }, target, CancellationToken.None);

        await using var result = await _engine.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(2, result.Info.PageCount);
        Assert.Equal(600, result.Info.Pages[0].WidthPoints, 1);   // 300x600 после поворота
        Assert.Equal(300, result.Info.Pages[0].HeightPoints, 1);
        Assert.Equal(612, result.Info.Pages[1].WidthPoints, 1);
        Assert.Contains("Page C", await result.GetPageTextAsync(0, CancellationToken.None));
        Assert.Contains("Page A", await result.GetPageTextAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task SaveService_Applies_Operations_And_Preserves_Original_On_SaveAs()
    {
        var path = PdfFixture.WriteToTemp("session.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Page A"),
            new PdfFixture.PageSpec(612, 792, Text: "Page B"),
            new PdfFixture.PageSpec(612, 792, Text: "Page C"));
        var originalBytes = await File.ReadAllBytesAsync(path);

        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new DeletePagesOperation(new[] { 1 }));          // без B
            document.Session.Apply(new MovePagesOperation(new[] { 1 }, 0));         // C, A
            document.Session.Apply(new RotatePagesOperation(new[] { 0 }, 2));       // C на 180°
            Assert.True(document.Session.IsDirty);

            var target = Path.Combine(Path.GetDirectoryName(path)!, "saved.pdf");
            var saver = new SaveService(_engine);
            await saver.SaveAsAsync(document, target, keepBackup: false, CancellationToken.None);

            Assert.False(document.Session.IsDirty);
            Assert.Equal(target, document.Session.FilePath);
            Assert.Equal(2, document.Session.Model.Pages.Count);

            await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
            Assert.Equal(2, reopened.Info.PageCount);
            Assert.Contains("Page C", await reopened.GetPageTextAsync(0, CancellationToken.None));
            Assert.Contains("Page A", await reopened.GetPageTextAsync(1, CancellationToken.None));
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SaveService_Saves_In_Place_Over_Open_File()
    {
        var path = PdfFixture.WriteToTemp("inplace.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Page A"),
            new PdfFixture.PageSpec(612, 792, Text: "Page B"));

        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new DeletePagesOperation(new[] { 0 }));

            // Ctrl+S: цель совпадает с открытым (отображённым в память) источником.
            await new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None);

            Assert.False(document.Session.IsDirty);
            Assert.Equal(1, document.Session.Model.Pages.Count);
            Assert.Contains("Page B",
                await document.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None));
        }

        await using var reopened = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(1, reopened.Info.PageCount);
    }

    [Fact]
    public async Task Copy_Operations_Refuse_Target_That_Is_Open_Source()
    {
        var path = PdfFixture.WriteToTemp("busy-target.pdf", new PdfFixture.PageSpec(612, 792));
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            await Assert.ThrowsAsync<PdfEngineException>(
                () => new SaveService(_engine).SaveCopyAsync(document, path, CancellationToken.None));
            await Assert.ThrowsAsync<PdfEngineException>(
                () => new SaveService(_engine).ExtractAsync(document, new[] { 0 }, path, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Extract_Writes_Selected_Pages_Only()
    {
        var path = PdfFixture.WriteToTemp("extract.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Page A"),
            new PdfFixture.PageSpec(612, 792, Text: "Page B"));

        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            var target = Path.Combine(Path.GetDirectoryName(path)!, "extracted.pdf");
            await new SaveService(_engine).ExtractAsync(document, new[] { 1 }, target, CancellationToken.None);

            await using var result = await _engine.OpenAsync(target, null, CancellationToken.None);
            Assert.Equal(1, result.Info.PageCount);
            Assert.Contains("Page B", await result.GetPageTextAsync(0, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Search_Finds_Matches_Across_Pages()
    {
        var path = PdfFixture.WriteToTemp("search.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "invoice number one"),
            new PdfFixture.PageSpec(612, 792, Text: "second INVOICE here"),
            new PdfFixture.PageSpec(612, 792, Text: "nothing"));

        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            var matches = await new SearchService().SearchAsync(document, "invoice", caseSensitive: false, CancellationToken.None);
            Assert.Equal(2, matches.Count);
            Assert.Equal(0, matches[0].LogicalPageIndex);
            Assert.Equal(1, matches[1].LogicalPageIndex);
        }
    }

    private static IEnumerable<bool> EnumeratePixelWhiteness(RenderedPageImage image)
    {
        for (var y = 0; y < image.PixelHeight; y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var offset = y * image.Stride + x * 4;
                yield return image.Bgra[offset] == 0xFF
                    && image.Bgra[offset + 1] == 0xFF
                    && image.Bgra[offset + 2] == 0xFF;
            }
        }
    }
}
