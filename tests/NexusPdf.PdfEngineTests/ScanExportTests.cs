using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NexusPdf.Application;
using NexusPdf.Export;
using NexusPdf.Ocr;
using NexusPdf.Ocr.Paddle;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Экспорт страниц без текстового слоя и повёрнутых листов.
///
/// Два случая, на которых экспорт молча терял данные: скан выгружался пустым
/// листом (текста на странице нет — значит и переносить нечего), а лист с
/// /Rotate выгружался нужного размера, но с текстом поперёк, потому что
/// координаты объектов в PDF живут в НЕповёрнутой системе.
/// </summary>
public sealed class ScanExportTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _pdfium = new();
    private ITextRecognizer? _recognizer;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _recognizer?.Dispose();
        await _pdfium.DisposeAsync();
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private OcrService MakeOcr()
    {
        _recognizer ??= new PaddleOcrEngine(AppContext.BaseDirectory, "cyrillic");
        return new OcrService(_recognizer);
    }

    /// <summary>
    /// Скан делается из настоящей страницы: текст рисуется, страница
    /// растеризуется и собирается заново уже картинкой. Так получается ровно
    /// то, что приносит сканер, — без текстового слоя.
    /// </summary>
    private async Task<string> MakeScanAsync(string dir, string text)
    {
        var source = PdfFixture.WriteToTemp("исходник.pdf",
            new PdfFixture.PageSpec(595, 842, 0, text));
        var target = Path.Combine(dir, "скан.pdf");

        await using var document = await OpenedDocument.OpenAsync(
            _pdfium, source, null, CancellationToken.None);
        var image = await document.RenderLogicalPageAsync(0, 1240, 1754, CancellationToken.None);

        await new ConvertService(_pdfium).CreateFromImagesAsync(
            new[] { new NexusPdf.Pdf.Abstractions.ImagePageSpec(
                image.Bgra, image.PixelWidth, image.PixelHeight, 595, 842) },
            target, CancellationToken.None);
        return target;
    }

    [Fact]
    public async Task A_Scan_Is_Recognised_Instead_Of_Exporting_An_Empty_Sheet()
    {
        var dir = NewDir();
        var scan = await MakeScanAsync(dir, "Hello NexusPDF");

        await using var document = await OpenedDocument.OpenAsync(_pdfium, scan, null, CancellationToken.None);
        var ocr = MakeOcr();
        var convert = new ConvertService(_pdfium, ocr);

        // Без распознавания страница честно считается сканом и остаётся пустой.
        var silent = await convert.ExportToExcelAsync(
            document, Path.Combine(dir, "без-распознавания.xlsx"), null,
            new ExcelExportOptions(), new PageAnalysisOptions(RecognizeScans: false),
            null, CancellationToken.None);
        Assert.Equal(1, silent.ScannedPages);
        Assert.Equal(0, silent.RecognizedPages);
        Assert.Equal(0, silent.Cells);

        if (!ocr.IsAvailable) return;   // без моделей проверять распознавание нечем

        var recognised = await convert.ExportToExcelAsync(
            document, Path.Combine(dir, "с-распознаванием.xlsx"), null,
            new ExcelExportOptions(), new PageAnalysisOptions(RecognizeScans: true),
            null, CancellationToken.None);

        Assert.Equal(1, recognised.ScannedPages);
        Assert.Equal(1, recognised.RecognizedPages);
        Assert.True(recognised.Cells > 0, "Распознанный скан обязан дать содержимое, а не пустой лист.");
    }

    [Fact]
    public async Task A_Recognised_Scan_Reaches_The_Word_Document()
    {
        var dir = NewDir();
        var scan = await MakeScanAsync(dir, "Hello NexusPDF");

        await using var document = await OpenedDocument.OpenAsync(_pdfium, scan, null, CancellationToken.None);
        var ocr = MakeOcr();
        if (!ocr.IsAvailable) return;

        var target = Path.Combine(dir, "скан.docx");
        var summary = await new ConvertService(_pdfium, ocr).ExportToWordAsync(
            document, target, null,
            new WordExportOptions(KeepImages: false),
            new PageAnalysisOptions(RecognizeScans: true), null, CancellationToken.None);

        Assert.Equal(1, summary.RecognizedPages);

        using var word = WordprocessingDocument.Open(target, false);
        var text = word.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains("Nexus", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Повёрнутый лист выгружается в СВОЁЙ ориентации: 400×600 с /Rotate 90 —
    /// это альбомные 600×400, а не книжные с текстом поперёк.
    /// </summary>
    [Fact]
    public async Task A_Rotated_Page_Keeps_The_Orientation_The_Reader_Sees()
    {
        var dir = NewDir();
        var source = PdfFixture.WriteToTemp("боком.pdf",
            new PdfFixture.PageSpec(400, 600, 90, "Rotated page"));
        var target = Path.Combine(dir, "боком.docx");

        await using var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None);
        await new ConvertService(_pdfium).ExportToWordAsync(
            document, target, null, new WordExportOptions(),
            new PageAnalysisOptions(RecognizeScans: false), null, CancellationToken.None);

        using var word = WordprocessingDocument.Open(target, false);
        var size = word.MainDocumentPart!.Document!.Body!.Descendants<PageSize>().Last();

        Assert.Equal(12000u, size.Width!.Value);     // 600 пунктов
        Assert.Equal(8000u, size.Height!.Value);     // 400 пунктов
        Assert.Equal(PageOrientationValues.Landscape, size.Orient!.Value);
    }

    /// <summary>Поворот, сделанный пользователем в организаторе, тоже учитывается.</summary>
    [Fact]
    public async Task A_Page_Turned_By_The_User_Exports_Turned()
    {
        var dir = NewDir();
        var source = PdfFixture.WriteToTemp("книжная.pdf",
            new PdfFixture.PageSpec(400, 600, 0, "Turned by user"));
        var target = Path.Combine(dir, "повёрнутая.docx");

        await using var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None);
        document.Session.Apply(new NexusPdf.Domain.RotatePagesOperation(new[] { 0 }, 1));

        await new ConvertService(_pdfium).ExportToWordAsync(
            document, target, null, new WordExportOptions(),
            new PageAnalysisOptions(RecognizeScans: false), null, CancellationToken.None);

        using var word = WordprocessingDocument.Open(target, false);
        var size = word.MainDocumentPart!.Document!.Body!.Descendants<PageSize>().Last();

        Assert.Equal(12000u, size.Width!.Value);
        Assert.Equal(8000u, size.Height!.Value);
    }
}
