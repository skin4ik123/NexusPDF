using NexusPdf.Imaging;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Выравнивание и чистка сканов на настоящем PDF. Проверяется главное: кривая
/// страница становится ровной, текстовый слой при этом не теряется (иначе
/// пропадёт поиск по документу), а чистка не трогает страницы, которые сканом
/// не являются.
/// </summary>
public sealed class ScanEnhanceTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _pdfium = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Скан: белая страница со строками «текста», накренёнными по часовой стрелке.</summary>
    private static ImagePageSpec ScanPage(double clockwiseDegrees, int width = 1000, int height = 1400)
    {
        var bgra = new byte[width * height * 4];
        Array.Fill(bgra, (byte)255);
        var radians = clockwiseDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = width / 2.0;
        var cy = height / 2.0;

        for (var line = 0; line < 26; line++)
        {
            var lineY = 90 + line * 46;
            for (var thickness = 0; thickness < 10; thickness++)
            for (var x = 120; x < width - 120; x++)
            {
                if ((x / 45) % 5 == 4) continue; // пробелы между «словами»
                var dx = x - cx;
                var dy = lineY + thickness - cy;
                var px = (int)Math.Round(cx + dx * cos - dy * sin);
                var py = (int)Math.Round(cy + dx * sin + dy * cos);
                if (px < 0 || py < 0 || px >= width || py >= height) continue;
                var o = ((long)py * width + px) * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = 25;
            }
        }
        // A4 в точках: страница ведёт себя как настоящий скан.
        return new ImagePageSpec(bgra, width, height, 595, 842);
    }

    private static void Speck(ImagePageSpec page, int x, int y)
    {
        var o = ((long)y * page.PixelWidth + x) * 4;
        page.Bgra[o] = page.Bgra[o + 1] = page.Bgra[o + 2] = 15;
    }

    [Fact]
    public async Task A_Crooked_Scan_Is_Measured_Before_Anything_Is_Changed()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "crooked.pdf");
        await _pdfium.CreateImageDocumentAsync(new[] { ScanPage(2.5) }, source, CancellationToken.None);
        var before = File.ReadAllBytes(source);

        var skews = await _pdfium.MeasureSkewAsync(source, null, null, CancellationToken.None);

        Assert.Single(skews);
        Assert.True(Math.Abs(skews[0].AngleDegrees + 2.5) < 0.4,
            $"Крен 2,5° по часовой должен дать около -2,5°, а получено {skews[0].AngleDegrees:0.00}°.");
        Assert.True(skews[0].Confidence > SkewDetector.MinConfidence);
        // Разбор обязан быть только чтением.
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Deskew_Straightens_The_Page()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "tilted.pdf");
        var target = Path.Combine(dir, "straight.pdf");
        await _pdfium.CreateImageDocumentAsync(new[] { ScanPage(3.0) }, source, CancellationToken.None);

        var stats = await _pdfium.EnhanceScansAsync(source, null, target,
            new ScanEnhanceOptions(Deskew: true, Despeckle: false), null, CancellationToken.None);

        Assert.Equal(1, stats.PagesStraightened);
        Assert.True(stats.MaxAngleDegrees > 2.5);

        var after = await _pdfium.MeasureSkewAsync(target, null, null, CancellationToken.None);
        Assert.True(Math.Abs(after[0].AngleDegrees) < 0.4,
            $"После выравнивания осталось {after[0].AngleDegrees:0.00}°.");
    }

    [Fact]
    public async Task A_Straight_Page_Is_Not_Touched()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "ok.pdf");
        var target = Path.Combine(dir, "ok-out.pdf");
        await _pdfium.CreateImageDocumentAsync(new[] { ScanPage(0) }, source, CancellationToken.None);

        var stats = await _pdfium.EnhanceScansAsync(source, null, target,
            new ScanEnhanceOptions(Deskew: true, Despeckle: false), null, CancellationToken.None);

        Assert.Equal(1, stats.PagesProcessed);
        Assert.Equal(0, stats.PagesStraightened);
    }

    [Fact]
    public async Task Deskew_Keeps_The_Searchable_Text_Layer()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "with-text.pdf");
        var target = Path.Combine(dir, "with-text-out.pdf");
        await _pdfium.CreateImageDocumentAsync(new[] { ScanPage(2.0) }, source, CancellationToken.None);

        // Поверх скана — текстовый слой, как после распознавания.
        var withText = Path.Combine(dir, "layered.pdf");
        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            var page = new ComposedPage(handle, 0, 0, new PageOverlay[]
            {
                new TextOverlay("Накладная № 42", 100, 700, 14, 0xFF000000, 0),
            });
            await _pdfium.ComposeAsync(new[] { page }, withText, CancellationToken.None);
        }

        var stats = await _pdfium.EnhanceScansAsync(withText, null, target,
            new ScanEnhanceOptions(Deskew: true, Despeckle: false), null, CancellationToken.None);
        Assert.Equal(1, stats.PagesStraightened);

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var text = await result.GetPageTextAsync(0, CancellationToken.None);
        Assert.Contains("Накладная", text);
    }

    [Fact]
    public async Task Scanner_Dirt_Is_Removed_From_The_Page_Image()
    {
        var dir = NewDir();
        var page = ScanPage(0);
        for (var i = 0; i < 60; i++)
            Speck(page, 40 + i * 15, 30);
        var source = Path.Combine(dir, "dirty.pdf");
        var target = Path.Combine(dir, "clean.pdf");
        await _pdfium.CreateImageDocumentAsync(new[] { page }, source, CancellationToken.None);

        var stats = await _pdfium.EnhanceScansAsync(source, null, target,
            new ScanEnhanceOptions(Deskew: false, Despeckle: true), null, CancellationToken.None);

        Assert.Equal(1, stats.ImagesCleaned);
        Assert.True(stats.SpecklesRemoved >= 55, $"Удалено {stats.SpecklesRemoved} пятен из 60.");

        // Верхняя полоса страницы обязана стать чистой, а текст — остаться.
        await using var doc = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var render = await doc.RenderPageAsync(0, 1000, 1400, 0, CancellationToken.None);
        var dirtyRow = 0;
        for (var x = 0; x < 1000; x++)
            if (render.Bgra[30 * render.Stride + x * 4] < 128) dirtyRow++;
        Assert.True(dirtyRow < 5, $"Мусор остался: тёмных пикселей в строке {dirtyRow}.");

        var textRow = 0;
        for (var x = 0; x < 1000; x++)
            if (render.Bgra[95 * render.Stride + x * 4] < 128) textRow++;
        Assert.True(textRow > 100, $"Текст пропал: тёмных пикселей в строке {textRow}.");
    }

    [Fact]
    public async Task A_Small_Logo_On_A_Text_Page_Is_Left_Alone()
    {
        // Страница-вёрстка с маленькой картинкой: чистка сканов не должна её
        // трогать — там нет шума сканера, зато есть аккуратная графика.
        var dir = NewDir();
        var logo = new byte[80 * 80 * 4];
        Array.Fill(logo, (byte)255);
        for (var i = 0; i < 40; i++)
        {
            var o = ((long)(i * 80 + i)) * 4;
            logo[o] = logo[o + 1] = logo[o + 2] = 10; // тонкая диагональ из точек
        }
        var source = Path.Combine(dir, "logo.pdf");
        var target = Path.Combine(dir, "logo-out.pdf");
        await _pdfium.CreateImageDocumentAsync(
            new[] { new ImagePageSpec(logo, 80, 80, 595, 842) }, source, CancellationToken.None);

        var stats = await _pdfium.EnhanceScansAsync(source, null, target,
            new ScanEnhanceOptions(Deskew: false, Despeckle: true), null, CancellationToken.None);

        // Картинка занимает всю страницу по размещению, но она крошечная в
        // пикселях — чистить нечего, и портить её мы не имеем права.
        Assert.Equal(0, stats.SpecklesRemoved);
    }
}
