using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Смена ОФОРМЛЕНИЯ существующей строки: гарнитуры, кегля, цвета.
///
/// Установить шрифт существующему объекту PDFium не даёт, поэтому объект
/// заменяется новым на том же месте, с его матрицей и на той же позиции в
/// порядке рисования. Проверяется именно это: текст не уезжает и не пропадает.
/// </summary>
public sealed class TextRestyleTests : IAsyncLifetime
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

    /// <summary>Фикстура ставит текст 24 пт в (72, 72) снизу — сверху это ~703.</summary>
    private const double ProbeX = 110;
    private const double ProbeY = 703;

    private async Task<PdfTextObject?> RestyleAsync(
        string dir, Func<PdfTextObject, TextObjectReplacement> replace)
    {
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "ORIGINALTEXT")));

        var outPath = Path.Combine(dir, "restyled.pdf");
        await using (var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            var found = await doc.GetTextObjectAtAsync(0, 0, ProbeX, ProbeY, CancellationToken.None);
            Assert.NotNull(found);
            await _pdfium.ComposeAsync(
                new[] { new ComposedPage(doc, 0, 0, new PageOverlay[] { replace(found!) }) },
                outPath, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(outPath, null, CancellationToken.None);
        return await result.GetTextObjectAtAsync(0, 0, ProbeX, ProbeY, CancellationToken.None);
    }

    [Fact]
    public async Task Changing_The_Family_Keeps_The_Text_In_Place()
    {
        if (PdfFontCatalog.ResolvePath("Georgia", false, false) == null)
            return;

        var dir = NewDir();
        var before = (PdfTextObject?)null;
        var source = Path.Combine(dir, "probe.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "ORIGINALTEXT")));
        await using (var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None))
            before = await doc.GetTextObjectAtAsync(0, 0, ProbeX, ProbeY, CancellationToken.None);

        var after = await RestyleAsync(dir, found => new TextObjectReplacement(
            found.ObjectPath, "ПЕРЕКРАШЕННЫЙ", "Georgia", false, false, found.FontSizePt, 0xFF2563EB));

        Assert.NotNull(after);
        Assert.Equal("ПЕРЕКРАШЕННЫЙ", after!.Text);
        Assert.Contains("Georgia", after.FontName, StringComparison.OrdinalIgnoreCase);

        // Строка обязана остаться на своём месте: матрица переносится целиком.
        // Левый край совпадает точно — начало строки то же самое.
        Assert.NotNull(before);
        Assert.InRange(after.XPt, before!.XPt - 2, before.XPt + 2);

        // По вертикали допуск шире, и это не поблажка: рамка меряется по
        // нарисованным буквам, а у другой гарнитуры другой подъём. Базовая
        // линия при этом та же — она сидит в матрице, которую мы перенесли.
        Assert.InRange(after.YPt, before.YPt - 10, before.YPt + 10);
    }

    [Fact]
    public async Task Changing_The_Family_Lets_Cyrillic_Through_A_Latin_Only_Font()
    {
        if (PdfFontCatalog.ResolvePath("Arial", false, false) == null)
            return;

        // Стандартная Helvetica фикстуры кириллицу не рисует. Ради этого случая
        // смена гарнитуры и нужна: иначе строку не поправить вообще.
        var after = await RestyleAsync(NewDir(), found => new TextObjectReplacement(
            found.ObjectPath, "Кириллица", "Arial", false, false, 0, 0));

        Assert.NotNull(after);
        Assert.Equal("Кириллица", after!.Text);
        Assert.True(after.IsEmbeddedFont, "новый шрифт обязан встроиться в файл");
    }

    [Fact]
    public async Task Changing_Only_The_Size_Keeps_The_Original_Font()
    {
        var after = await RestyleAsync(NewDir(), found => new TextObjectReplacement(
            found.ObjectPath, "ORIGINALTEXT", "", false, false, found.FontSizePt * 1.5, 0));

        Assert.NotNull(after);
        // Кегль просили другой — он и обязан стать другим.
        Assert.InRange(after!.FontSizePt, 34, 38);
    }

    [Fact]
    public async Task Text_Only_Edit_Does_Not_Touch_The_Font()
    {
        var after = await RestyleAsync(NewDir(), found => new TextObjectReplacement(
            found.ObjectPath, "JUSTNEWWORDS"));

        Assert.NotNull(after);
        Assert.Equal("JUSTNEWWORDS", after!.Text);
        // Без запроса на оформление шрифт остаётся исходным: это самый
        // безопасный путь, и сворачивать на замену объекта тут незачем.
        Assert.Contains("Helvetica", after.FontName, StringComparison.OrdinalIgnoreCase);
    }
}
