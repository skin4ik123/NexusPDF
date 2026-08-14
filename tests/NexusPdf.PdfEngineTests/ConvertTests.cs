using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Конвертация: экспорт растров, извлечение текста, объединение PDF, сборка из изображений.</summary>
public sealed class ConvertTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;
    private ConvertService _convert = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        _convert = new ConvertService(_pdfium);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ExportImages_Renders_Every_Page_At_Requested_Dpi()
    {
        var path = PdfFixture.WriteToTemp("export.pdf",
            new PdfFixture.PageSpec(612, 792),
            new PdfFixture.PageSpec(612, 792, Rotate: 90));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var images = new List<(int PageIndex, RenderedPageImage Image)>();
            var count = await _convert.ExportImagesAsync(
                document, null, 150,
                (image, pageIndex, _) => { images.Add((pageIndex, image)); return Task.CompletedTask; },
                null, CancellationToken.None);

            Assert.Equal(2, count);
            Assert.Equal(new[] { 0, 1 }, images.Select(i => i.PageIndex));
            // 612 pt на 150 DPI = 1275 px; повёрнутая страница отдаётся в отображаемой ориентации.
            Assert.Equal(1275, images[0].Image.PixelWidth);
            Assert.Equal(1650, images[0].Image.PixelHeight);
            Assert.Equal(1650, images[1].Image.PixelWidth);
            Assert.Equal(1275, images[1].Image.PixelHeight);
        }
    }

    [Fact]
    public async Task ExtractText_Returns_All_Pages_In_Order()
    {
        var path = PdfFixture.WriteToTemp("text.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Alpha page"),
            new PdfFixture.PageSpec(612, 792, Text: "Beta page"));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var text = await _convert.ExtractTextAsync(document, CancellationToken.None);
            Assert.Contains("Alpha page", text);
            Assert.Contains("Beta page", text);
            Assert.True(text.IndexOf("Alpha", StringComparison.Ordinal) <
                        text.IndexOf("Beta", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Merge_Concatenates_Files_And_Preserves_Text()
    {
        var dir = TempDir();
        var first = Path.Combine(dir, "first.pdf");
        var second = Path.Combine(dir, "second.pdf");
        File.WriteAllBytes(first, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "First doc")));
        File.WriteAllBytes(second, PdfFixture.Build(
            new PdfFixture.PageSpec(500, 500, Text: "Second doc"),
            new PdfFixture.PageSpec(500, 500, Text: "Second doc page two")));

        var target = Path.Combine(dir, "merged.pdf");
        var pages = await _convert.MergeAsync(new[] { first, second }, target, CancellationToken.None);
        Assert.Equal(3, pages);

        await using var merged = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(3, merged.Info.PageCount);
        Assert.Contains("First doc", await merged.GetPageTextAsync(0, CancellationToken.None));
        Assert.Contains("page two", await merged.GetPageTextAsync(2, CancellationToken.None));
    }

    [Fact]
    public async Task Merge_Refuses_Target_Equal_To_Source()
    {
        var dir = TempDir();
        var first = Path.Combine(dir, "a.pdf");
        var second = Path.Combine(dir, "b.pdf");
        File.WriteAllBytes(first, PdfFixture.Build(new PdfFixture.PageSpec(612, 792)));
        File.WriteAllBytes(second, PdfFixture.Build(new PdfFixture.PageSpec(612, 792)));

        await Assert.ThrowsAsync<PdfEngineException>(() =>
            _convert.MergeAsync(new[] { first, second }, first, CancellationToken.None));
        // Исходник не пострадал.
        await using var check = await _pdfium.OpenAsync(first, null, CancellationToken.None);
        Assert.Equal(1, check.Info.PageCount);
    }

    [Fact]
    public async Task Merge_Reports_Protected_Source_By_Name()
    {
        var dir = TempDir();
        var plain = Path.Combine(dir, "plain.pdf");
        File.WriteAllBytes(plain, PdfFixture.Build(new PdfFixture.PageSpec(612, 792)));
        var qpdf = new NexusPdf.Pdf.Qpdf.QpdfEngine();
        if (!qpdf.IsAvailable) return;
        var locked = Path.Combine(dir, "locked.pdf");
        await qpdf.EncryptAsync(plain, locked, "secret", null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<PdfEngineException>(() =>
            _convert.MergeAsync(new[] { plain, locked }, Path.Combine(dir, "out.pdf"), CancellationToken.None));
        Assert.Contains("locked.pdf", ex.Message);
    }

    [Fact]
    public async Task CreateFromImages_Builds_Document_With_Image_Pages()
    {
        var dir = TempDir();
        // Красная картинка 200×100 при 100 DPI → страница 144×72 pt.
        var red = new byte[200 * 100 * 4];
        for (var i = 0; i < red.Length; i += 4)
        {
            red[i + 2] = 0xFF; // R (BGRA)
            red[i + 3] = 0xFF; // A
        }
        var target = Path.Combine(dir, "images.pdf");
        await _convert.CreateFromImagesAsync(
            new[]
            {
                new ImagePageSpec(red, 200, 100, 144, 72),
                new ImagePageSpec(red, 200, 100, 288, 144),
            },
            target, CancellationToken.None);

        await using var built = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(2, built.Info.PageCount);
        Assert.Equal(144, built.Info.Pages[0].WidthPoints, 1);
        Assert.Equal(72, built.Info.Pages[0].HeightPoints, 1);
        Assert.Equal(288, built.Info.Pages[1].WidthPoints, 1);

        // Изображение реально закрывает страницу: центр — красный.
        var render = await built.RenderPageAsync(0, 144, 72, 0, CancellationToken.None);
        var center = (36 * render.Stride) + (72 * 4);
        Assert.True(render.Bgra[center + 2] > 200, "Красный канал в центре страницы должен быть насыщенным.");
        Assert.True(render.Bgra[center + 0] < 60, "Синий канал в центре страницы должен быть тёмным.");
    }
}
