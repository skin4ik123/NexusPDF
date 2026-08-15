using NexusPdf.Application;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Printing;
using Xunit.Abstractions;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Печать проверяется без принтера: тот же PrintJobPlan, который ушёл бы в
/// очередь, выводится в файл. Проверяется не «не упало», а геометрия
/// результата — число листов, их физический размер и то, что содержимое
/// действительно легло туда, куда обещал план.
/// </summary>
public sealed class PrintOutputTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PdfiumRenderEngine _pdfium = null!;
    private string _dir = "";

    public PrintOutputTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static readonly SizePt A4 = new(595.28, 841.89);
    private static readonly PaperSizeOption A4Paper = new("A4", A4);

    private static PrinterCapabilities Caps() => new()
    {
        PrinterName = "Вывод в файл",
        IsVirtual = true,
        SupportsColor = true,
        PaperSizes = new[] { A4Paper },
        HardMarginsByPaper = new Dictionary<string, MarginsPt> { ["A4"] = MarginsPt.Uniform(14.17) },
    };

    /// <summary>Документ с различимыми страницами: иначе перестановку не увидеть.</summary>
    private string MakeDocument(int pages)
    {
        var path = Path.Combine(_dir, $"src{pages}.pdf");
        var specs = Enumerable.Range(1, pages)
            .Select(i => new PdfFixture.PageSpec(595, 842, Text: $"PAGE {i}"))
            .ToArray();
        File.WriteAllBytes(path, PdfFixture.Build(specs));
        return path;
    }

    private async Task<PrintToFileResult> RunAsync(
        OpenedDocument document, LayoutSettings settings, string name, double dpi = 150)
    {
        var pages = Enumerable.Range(0, document.Session.Model.Pages.Count)
            .Select(i =>
            {
                var size = document.GetLogicalPageSize(i);
                return new SourcePage("doc", i, new SizePt(size.WidthPoints, size.HeightPoints));
            })
            .ToList();

        var sheets = new PrintLayoutEngine().BuildSheets(pages, settings, A4Paper, Caps());
        var plan = new PrintJobPlan
        {
            JobName = name,
            PrinterName = "Вывод в файл",
            Capabilities = Caps(),
            Sheets = sheets,
            Duplex = settings.Duplex,
        };
        plan = plan with { Issues = Preflight.Analyze(plan) };

        foreach (var issue in plan.Issues)
            _output.WriteLine($"  [{issue.Level}] {issue.Message}");

        var target = Path.Combine(_dir, name + ".pdf");
        var result = await new PrintToFileService(_pdfium)
            .SaveAsync(document, plan, target, dpi, null, CancellationToken.None);

        _output.WriteLine($"{name}: листов {result.SheetsWritten}, фактический DPI {result.EffectiveDpi:F0}");
        return result;
    }

    [Fact]
    public async Task Single_Page_Per_Sheet_Writes_One_Sheet_Per_Page()
    {
        var document = await OpenedDocument.OpenAsync(_pdfium, MakeDocument(3), null, CancellationToken.None);
        await using (document)
        {
            var result = await RunAsync(document, new LayoutSettings(), "single");
            Assert.Equal(3, result.SheetsWritten);

            await using var check = await _pdfium.OpenAsync(result.Path, null, CancellationToken.None);
            Assert.Equal(3, check.Info.PageCount);
            // Физический размер листа обязан остаться A4, а не превратиться в размер растра.
            Assert.Equal(A4.WidthPt, check.Info.Pages[0].WidthPoints, 0);
            Assert.Equal(A4.HeightPt, check.Info.Pages[0].HeightPoints, 0);
        }
    }

    [Fact]
    public async Task Four_Up_Puts_Four_Pages_On_One_Sheet()
    {
        var document = await OpenedDocument.OpenAsync(_pdfium, MakeDocument(8), null, CancellationToken.None);
        await using (document)
        {
            var result = await RunAsync(document, new LayoutSettings
            {
                Imposition = ImpositionMode.NUp,
                NUp = new NUpSettings { Rows = 2, Columns = 2 },
            }, "nup4");

            Assert.Equal(2, result.SheetsWritten); // 8 страниц по 4 на лист
        }
    }

    [Fact]
    public async Task Booklet_Of_Six_Pages_Rounds_Up_To_Two_Sheets()
    {
        var document = await OpenedDocument.OpenAsync(_pdfium, MakeDocument(6), null, CancellationToken.None);
        await using (document)
        {
            var result = await RunAsync(document, new LayoutSettings
            {
                Imposition = ImpositionMode.Booklet,
            }, "booklet");

            // 6 страниц округляются до 8 = 2 листа = 4 стороны.
            Assert.Equal(4, result.SheetsWritten);
        }
    }

    [Fact]
    public async Task Poster_Splits_One_Page_Across_Sheets()
    {
        var path = Path.Combine(_dir, "big.pdf");
        // Страница вдвое больше A4 по обеим сторонам.
        File.WriteAllBytes(path, PdfFixture.Build(
            new PdfFixture.PageSpec(1190, 1684, Text: "POSTER")));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var result = await RunAsync(document, new LayoutSettings
            {
                Imposition = ImpositionMode.Poster,
                Orientation = OrientationMode.Portrait,
                Poster = new PosterSettings { Scale = 1.0, OverlapPt = 14.17 },
            }, "poster");

            Assert.True(result.SheetsWritten >= 4,
                $"плакат 2×2 листа должен дать минимум 4 листа, получено {result.SheetsWritten}");
        }
    }

    [Fact]
    public async Task Rendered_Sheet_Has_White_Margins_And_Ink_Inside()
    {
        // Прямая проверка обещания: содержимое лежит внутри печатаемой области,
        // а непечатаемые поля остаются чистыми.
        var document = await OpenedDocument.OpenAsync(_pdfium, MakeDocument(1), null, CancellationToken.None);
        await using (document)
        {
            var pages = new[] { new SourcePage("doc", 0, A4) };
            var settings = new LayoutSettings { Size = SizeMode.Fit, Orientation = OrientationMode.Portrait };
            var sheet = new PrintLayoutEngine().BuildSheets(pages, settings, A4Paper, Caps()).Single();

            var composed = SheetComposer.Compose(sheet, 150);
            var image = await new PrintPlanRenderer(document)
                .RenderSheetAsync(composed, CancellationToken.None);

            Assert.Equal(composed.WidthPx, image.PixelWidth);
            Assert.Equal(composed.HeightPx, image.PixelHeight);

            // Угол листа — в непечатаемом поле, там обязана быть чистая бумага.
            Assert.True(IsWhite(image, 2, 2), "угол листа должен остаться белым");

            // Внутри области содержимого должны быть небелые пиксели: иначе
            // «напечатался» бы пустой лист, и тест бы этого не заметил.
            Assert.True(HasInk(image, composed.PrintableAreaPx),
                "в печатаемой области нет ни одного небелого пикселя");
        }
    }

    private static bool IsWhite(NexusPdf.Pdf.Abstractions.RenderedPageImage image, int x, int y)
    {
        var offset = y * image.Stride + x * 4;
        return image.Bgra[offset] > 250 && image.Bgra[offset + 1] > 250 && image.Bgra[offset + 2] > 250;
    }

    private static bool HasInk(NexusPdf.Pdf.Abstractions.RenderedPageImage image, RectPx area)
    {
        for (var y = area.Y; y < Math.Min(area.Bottom, image.PixelHeight); y += 2)
        for (var x = area.X; x < Math.Min(area.Right, image.PixelWidth); x += 2)
        {
            if (!IsWhite(image, x, y)) return true;
        }
        return false;
    }
}
