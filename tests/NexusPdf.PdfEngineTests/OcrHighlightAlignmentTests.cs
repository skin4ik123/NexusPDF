using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;
using Xunit.Abstractions;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Подсветка найденного и выделение мышью строятся по прямоугольникам символов,
/// а те у невидимого слоя OCR берутся из МЕТРИК шрифта, а не из чернил. Если
/// метрический бокс уезжает от рамки распознанного слова, пользователь видит
/// жёлтый прямоугольник рядом со словом, а не на нём. Здесь это измеряется.
/// </summary>
public sealed class OcrHighlightAlignmentTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PdfiumRenderEngine _pdfium = null!;

    public OcrHighlightAlignmentTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Fact]
    public async Task Highlight_Of_Recognized_Word_Covers_Its_Box()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "scan.pdf");
        const double PageHeight = 792;
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, PageHeight, Text: "")));

        // Рамка слова в ОТОБРАЖАЕМЫХ координатах: отсчёт сверху страницы.
        const double WordX = 100, WordY = 200, WordW = 90, WordH = 14;

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0, new OcrTextLayerOverlay(new[]
            {
                new OcrWordBox("215279", WordX, WordY, WordW, WordH),
            })));

            var match = Assert.Single(await new SearchService()
                .SearchAsync(document, "215279", false, CancellationToken.None));
            var (handle, pageIndex) = await document.ResolveTextPageAsync(
                match.LogicalPageIndex, CancellationToken.None);
            var rects = await handle.GetTextRectsAsync(
                pageIndex, match.CharIndex, match.Length, CancellationToken.None);

            var rect = Assert.Single(rects);
            // PDF-координаты снизу вверх → в отображаемые сверху вниз.
            var topFromPageTop = PageHeight - rect.Top;
            var bottomFromPageTop = PageHeight - rect.Bottom;
            _output.WriteLine($"рамка слова:   X {WordX}..{WordX + WordW}, Y {WordY}..{WordY + WordH}");
            _output.WriteLine($"подсветка:     X {rect.Left:F1}..{rect.Right:F1}, " +
                              $"Y {topFromPageTop:F1}..{bottomFromPageTop:F1}");

            // Перекрытие по вертикали: подсветка обязана накрывать само слово.
            var overlap = Math.Min(bottomFromPageTop, WordY + WordH) - Math.Max(topFromPageTop, WordY);
            _output.WriteLine($"перекрытие по вертикали: {overlap:F1} пт из {WordH} пт рамки");

            Assert.True(overlap >= WordH * 0.8,
                $"подсветка накрывает слово лишь на {overlap:F1} пт из {WordH} — она уехала от слова");
            Assert.True(rect.Left <= WordX + 1 && rect.Right >= WordX + WordW - 1,
                $"подсветка по горизонтали {rect.Left:F1}..{rect.Right:F1} не накрывает {WordX}..{WordX + WordW}");
        }
    }
}
