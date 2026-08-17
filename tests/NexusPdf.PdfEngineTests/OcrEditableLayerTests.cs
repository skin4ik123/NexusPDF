using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Редактируемый текст поверх скана: строка обязана вставать в свою рамку
/// в натуральных пропорциях. Раньше высота подгонялась под рамку отдельно от
/// ширины, и строка без заглавных и хвостов вниз («оно все») раздувалась на
/// всю высоту рамки, а буквы плющились по одной оси.
/// </summary>
public sealed class OcrEditableLayerTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const double BoxWidth = 50;
    private const double BoxHeight = 20;

    /// <summary>Кладёт две строки в ОДИНАКОВЫЕ рамки и возвращает готовый файл.</summary>
    private async Task<string> ComposeTwoLinesAsync(string dir, string low, string tall)
    {
        var sourcePath = Path.Combine(dir, "blank.pdf");
        // Пустая страница: чужой текст фикстуры не должен попасться под клик.
        File.WriteAllBytes(sourcePath, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));

        var outPath = Path.Combine(dir, "editable.pdf");
        await using (var source = await _pdfium.OpenAsync(sourcePath, null, CancellationToken.None))
        {
            var layer = new OcrEditableTextOverlay(new[]
            {
                new OcrTextLine(low, 72, 100, BoxWidth, BoxHeight),
                new OcrTextLine(tall, 72, 200, BoxWidth, BoxHeight),
            });
            await _pdfium.ComposeAsync(
                new[] { new ComposedPage(source, 0, 0, new PageOverlay[] { layer }) },
                outPath, CancellationToken.None);
        }
        return outPath;
    }

    [Fact]
    public async Task Line_Without_Tall_Letters_Is_Not_Stretched_To_The_Whole_Box()
    {
        var dir = NewDir();
        // «ооо» — только строчные без выносных элементов, «ЙЙЙ» — заглавные с
        // краткой сверху. Рамки одинаковые, поэтому вся разница в высоте
        // обязана прийти от самих букв, а не от подгонки.
        var path = await ComposeTwoLinesAsync(dir, "ооо", "ЙЙЙ");

        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var low = await doc.GetTextObjectAtAsync(
            0, 0, 72 + BoxWidth / 2, 100 + BoxHeight / 2, CancellationToken.None);
        var tall = await doc.GetTextObjectAtAsync(
            0, 0, 72 + BoxWidth / 2, 200 + BoxHeight / 2, CancellationToken.None);

        Assert.NotNull(low);
        Assert.NotNull(tall);
        Assert.Equal("ооо", low!.Text);
        Assert.Equal("ЙЙЙ", tall!.Text);

        // Главное: строчные буквы НЕ занимают рамку по высоте целиком.
        // При подгонке по высоте здесь было ровно BoxHeight.
        Assert.True(low.HeightPt < BoxHeight * 0.95,
            $"строка «ооо» растянута на всю рамку: {low.HeightPt:0.##} при рамке {BoxHeight}");

        // И заглавные в той же рамке обязаны быть заметно выше строчных.
        // При подгонке по высоте обе строки получались одной высоты.
        Assert.True(tall.HeightPt > low.HeightPt * 1.05,
            $"«ЙЙЙ» ({tall.HeightPt:0.##}) не выше «ооо» ({low.HeightPt:0.##}) — " +
            "высота всё ещё берётся от рамки, а не от букв");
    }

    [Fact]
    public async Task Line_Keeps_The_Width_Of_Its_Box()
    {
        var dir = NewDir();
        var path = await ComposeTwoLinesAsync(dir, "ооо", "ЙЙЙ");

        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var low = await doc.GetTextObjectAtAsync(
            0, 0, 72 + BoxWidth / 2, 100 + BoxHeight / 2, CancellationToken.None);

        Assert.NotNull(low);
        // Ширина рамки — то немногое, чему распознавание можно верить: именно
        // по ней и считается масштаб, поэтому строка обязана в неё попасть.
        Assert.InRange(low!.WidthPt, BoxWidth * 0.9, BoxWidth * 1.1);
    }

    [Fact]
    public async Task Line_Is_Covered_By_Its_Background_Patch_Not_A_Flat_Fill()
    {
        // Оригинал под строкой закрывается кусочком ФОНА. На фотографии
        // документа заливка одним цветом видна как дыра, и страница после
        // замены текста выглядит хуже, чем была. Проверяем, что заплатка
        // доехала до PDF: кладём заведомо двухцветную (красный верх, синий
        // низ) и ищем оба цвета на отрисованной странице.
        var dir = NewDir();
        var sourcePath = Path.Combine(dir, "blank.pdf");
        File.WriteAllBytes(sourcePath, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));

        const int patchW = 8, patchH = 8;
        var patch = new byte[patchW * patchH * 4];
        for (var row = 0; row < patchH; row++)
        {
            for (var col = 0; col < patchW; col++)
            {
                var o = (row * patchW + col) * 4;
                var top = row < patchH / 2;
                patch[o] = top ? (byte)0x00 : (byte)0xFF;     // B
                patch[o + 1] = 0x00;                          // G
                patch[o + 2] = top ? (byte)0xFF : (byte)0x00; // R
                patch[o + 3] = 0xFF;
            }
        }

        var outPath = Path.Combine(dir, "patched.pdf");
        await using (var source = await _pdfium.OpenAsync(sourcePath, null, CancellationToken.None))
        {
            var layer = new OcrEditableTextOverlay(new[]
            {
                new OcrTextLine("текст", 72, 100, BoxWidth, BoxHeight,
                    Patch: new OcrLinePatch(patch, patchW, patchH, 72, 100, BoxWidth, BoxHeight)),
            });
            await _pdfium.ComposeAsync(
                new[] { new ComposedPage(source, 0, 0, new PageOverlay[] { layer }) },
                outPath, CancellationToken.None);
        }

        await using var doc = await _pdfium.OpenAsync(outPath, null, CancellationToken.None);
        var image = await doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);

        var red = 0;
        var blue = 0;
        for (var y = 100; y < 100 + (int)BoxHeight; y++)
        {
            for (var x = 72; x < 72 + (int)BoxWidth; x++)
            {
                var o = y * image.Stride + x * 4;
                var b = image.Bgra[o];
                var r = image.Bgra[o + 2];
                if (r > 0xA0 && b < 0x60) red++;
                if (b > 0xA0 && r < 0x60) blue++;
            }
        }

        Assert.True(red > 20, $"верхняя половина заплатки не красная (найдено {red} точек)");
        Assert.True(blue > 20, $"нижняя половина заплатки не синяя (найдено {blue} точек)");
    }
}
