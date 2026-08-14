using NexusPdf.Application;
using NexusPdf.Ocr;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// OCR: полный конвейер «скан → распознавание → невидимый текстовый слой →
/// сохранение → текст ищется, картинка не изменилась». Пропускаются, если
/// языковые модели tessdata не скачаны (tools/fetch-tessdata.ps1).
/// </summary>
public sealed class OcrTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;
    private TesseractOcrEngine _ocr = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        _ocr = new TesseractOcrEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _ocr.Dispose();
        await _pdfium.DisposeAsync();
    }

    /// <summary>
    /// Строит «скан»: страница с крупным текстом (латиница из фикстуры +
    /// кириллица оверлеем) рендерится в растр, и растр кладётся картинкой
    /// на пустую страницу нового PDF. Текстового слоя у результата нет.
    /// </summary>
    private async Task<string> BuildScanAsync(string dir)
    {
        var sourcePath = Path.Combine(dir, "text-source.pdf");
        File.WriteAllBytes(sourcePath, PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "INVOICE 12345")));

        var styledPath = Path.Combine(dir, "text-styled.pdf");
        await using (var source = await _pdfium.OpenAsync(sourcePath, null, CancellationToken.None))
        {
            var overlay = new TextOverlay("ДОГОВОР АРЕНДЫ", 72, 300, 36, 0xFF000000, 0);
            await _pdfium.ComposeAsync(
                new[] { new ComposedPage(source, 0, 0, new PageOverlay[] { overlay }) },
                styledPath, CancellationToken.None);
        }

        var scanPath = Path.Combine(dir, "scan.pdf");
        await using (var styled = await _pdfium.OpenAsync(styledPath, null, CancellationToken.None))
        {
            var image = await styled.RenderPageAsync(0, 2448, 3168, 0, CancellationToken.None);
            var blankPath = Path.Combine(dir, "blank.pdf");
            File.WriteAllBytes(blankPath, PdfFixture.Build(
                new PdfFixture.PageSpec(612, 792, Text: " ")));
            await using var blank = await _pdfium.OpenAsync(blankPath, null, CancellationToken.None);
            var pageImage = new ImageOverlay(
                image.Bgra, image.PixelWidth, image.PixelHeight, 0, 0, 612, 792);
            await _pdfium.ComposeAsync(
                new[] { new ComposedPage(blank, 0, 0, new PageOverlay[] { pageImage }) },
                scanPath, CancellationToken.None);
        }
        return scanPath;
    }

    [Fact]
    public async Task Scan_Is_Recognized_Saved_Searchable_And_Visually_Unchanged()
    {
        if (!_ocr.IsAvailable) return;
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scanPath = await BuildScanAsync(dir);

        var document = await OpenedDocument.OpenAsync(_pdfium, scanPath, null, CancellationToken.None);
        await using (document)
        {
            // У скана нет текстового слоя.
            var before = await document.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None);
            Assert.True(before.Count(c => !char.IsWhiteSpace(c)) < 8, $"Скан уже содержит текст: '{before}'");

            var service = new OcrService(_ocr);
            var result = await service.RecognizeAsync(document, null, null, CancellationToken.None);
            Assert.False(result.Cancelled);
            Assert.Equal(1, result.PagesRecognized);
            Assert.True(result.WordCount >= 3, $"Распознано слов: {result.WordCount}");

            var savedPath = Path.Combine(dir, "scan-ocr.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, savedPath, CancellationToken.None);

            await using var reopened = await _pdfium.OpenAsync(savedPath, null, CancellationToken.None);
            var text = (await reopened.GetPageTextAsync(0, CancellationToken.None)).ToUpperInvariant();
            Assert.Contains("INVOICE", text);
            Assert.Contains("ДОГОВОР", text);

            // Невидимость: рендер страницы со слоем OCR совпадает попиксельно
            // с рендером исходного скана.
            var original = await document.PrimaryHandle.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
            var withLayer = await reopened.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
            Assert.Equal(original.Bgra, withLayer.Bgra);
        }
    }

    [Fact]
    public async Task Page_With_Real_Text_Is_Skipped()
    {
        if (!_ocr.IsAvailable) return;
        var path = PdfFixture.WriteToTemp("has-text.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Regular text page"));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var service = new OcrService(_ocr);
            var result = await service.RecognizeAsync(document, null, null, CancellationToken.None);
            Assert.Equal(0, result.PagesRecognized);
            Assert.Equal(1, result.PagesSkippedWithText);
            Assert.Empty(document.Session.Model.Pages[0].OverlayList ?? Array.Empty<PageOverlay>());
        }
    }

    [Fact]
    public async Task Second_Run_Skips_Already_Recognized_Page()
    {
        if (!_ocr.IsAvailable) return;
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scanPath = await BuildScanAsync(dir);
        var document = await OpenedDocument.OpenAsync(_pdfium, scanPath, null, CancellationToken.None);
        await using (document)
        {
            var service = new OcrService(_ocr);
            var first = await service.RecognizeAsync(document, null, null, CancellationToken.None);
            Assert.Equal(1, first.PagesRecognized);

            // Повторный запуск не наслаивает второй слой на ту же страницу.
            var second = await service.RecognizeAsync(document, null, null, CancellationToken.None);
            Assert.Equal(0, second.PagesRecognized);
            Assert.Equal(1, second.PagesSkippedWithText);
        }
    }
}
