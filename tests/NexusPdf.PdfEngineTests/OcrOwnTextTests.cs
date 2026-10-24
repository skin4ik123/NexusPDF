using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Ocr;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.TestSupport;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Распознавание пропускает страницы, где уже есть текст (иначе он задвоился бы
/// невидимым слоем). Но наш собственный текст на скане — блок, написанный
/// инструментом «Текст», черновой или уже сохранённый, — текстом скана не
/// считается: такой скан распознаётся, а сам блок в распознаваемую картинку не
/// попадает. Движок распознавания здесь — заглушка: проверяется, что ей дали.
/// </summary>
public sealed class OcrOwnTextTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "ocr-own-" + Guid.NewGuid().ToString("N"));
    private FontCatalog _fonts = null!;
    private PdfiumRenderEngine _pdfium = null!;

    /// <summary>«Скан»: штрихи вместо букв, настоящего текста нет.</summary>
    private const string Scan = "0.3 g 72 690 150 12 re f 72 660 120 12 re f";

    private sealed class Recognizer : ITextRecognizer
    {
        public RenderedPageImage? Image;
        public int Dpi;
        public string Id => "fake";
        public string DisplayName => "Fake";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool ReturnsWholeLines => false;
        public Task<OcrPageResult> RecognizeAsync(RenderedPageImage image, int renderDpi, CancellationToken ct)
        {
            (Image, Dpi) = (image, renderDpi);
            return Task.FromResult(new OcrPageResult([new OcrWord("Scanned", 10, 10, 80, 20, 95)], 95));
        }
        public void Dispose() { }
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _fonts = FontCatalog.Scan([TestFontPaths.Directory]);
        _pdfium = new PdfiumRenderEngine(null, _fonts);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pdfium.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Source(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, RawContent: content)));
        return path;
    }

    /// <summary>Блок, как его кладёт редактор: рамка уже по вёрстке.</summary>
    private TextBlockOverlay Block(string text)
    {
        var style = new TextStyle("Liberation Sans", 14);
        return new TextBlockEditorModel(new TextBlockOverlay(300, 300, 10, 10, 0, TextBlockWidthMode.AutoWidth, true, false,
            [new TextParagraph([new TextRun(text, style)])], Guid.NewGuid()), _fonts, style).Block;
    }

    private static int InkIn(RenderedPageImage image, int dpi, double xPt, double yPt, double wPt, double hPt)
    {
        var scale = dpi / 72.0;
        var inked = 0;
        for (var y = (int)(yPt * scale); y < (int)((yPt + hPt) * scale) && y < image.PixelHeight; y++)
        for (var x = (int)(xPt * scale); x < (int)((xPt + wPt) * scale) && x < image.PixelWidth; x++)
        {
            var at = (long)y * image.Stride + x * 4;
            if (Math.Min(image.Bgra[at], Math.Min(image.Bgra[at + 1], image.Bgra[at + 2])) < 160) inked++;
        }
        return inked;
    }

    [Fact]
    public async Task Draft_Block_On_A_Scan_Does_Not_Stop_Recognition()
    {
        await using var document = await OpenedDocument.OpenAsync(_pdfium, Source(Scan), null, CancellationToken.None);
        document.Session.Apply(new AddOverlayOperation(0, Block("Typed on the scan")));
        var recognizer = new Recognizer();

        var result = await new OcrService(recognizer).RecognizeAsync(document, [0], null, CancellationToken.None);

        Assert.Equal((1, 0), (result.PagesRecognized, result.PagesSkippedWithText));
        Assert.Contains(document.Session.Model.Pages[0].OverlayList, o => o is OcrTextLayerOverlay);
    }

    [Fact]
    public async Task Saved_Block_On_A_Scan_Is_Not_Page_Text_And_Not_Recognized()
    {
        var block = Block("Typed and saved");
        var saved = Path.Combine(_dir, "saved.pdf");
        await using (var source = await _pdfium.OpenAsync(Source(Scan), null, CancellationToken.None))
            await _pdfium.ComposeAsync([new ComposedPage(source, 0, 0, [block])], saved, CancellationToken.None);
        await using var document = await OpenedDocument.OpenAsync(_pdfium, saved, null, CancellationToken.None);
        Assert.Contains("Typed and saved", await document.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None));
        var recognizer = new Recognizer();

        var result = await new OcrService(recognizer).RecognizeAsync(document, [0], null, CancellationToken.None);

        Assert.Equal((1, 0), (result.PagesRecognized, result.PagesSkippedWithText));
        // Скан в картинке есть, нашего блока — нет: его текст не задвоится.
        Assert.True(InkIn(recognizer.Image!, recognizer.Dpi, 72, 90, 150, 12) > 0);
        Assert.Equal(0, InkIn(recognizer.Image!, recognizer.Dpi, block.XPt, block.YPt, block.WidthPt, block.HeightPt));
    }

    [Fact]
    public async Task Page_With_Real_Text_Is_Still_Skipped()
    {
        await using var document = await OpenedDocument.OpenAsync(_pdfium,
            Source(Scan + " BT /F1 12 Tf 72 600 Td (Real text) Tj ET"), null, CancellationToken.None);

        var result = await new OcrService(new Recognizer()).RecognizeAsync(document, [0], null, CancellationToken.None);

        Assert.Equal((0, 1), (result.PagesRecognized, result.PagesSkippedWithText));
    }
}
