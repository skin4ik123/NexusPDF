using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Выбранная гарнитура обязана доехать до файла, а не потеряться по дороге.
/// Раньше поля шрифта у надписи не было вообще: весь вставляемый текст писался
/// одним системным шрифтом.
/// </summary>
public sealed class TextOverlayFontTests : IAsyncLifetime
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

    private async Task<PdfTextObject?> WriteAndReadBackAsync(
        string dir, string family, bool bold, bool italic)
    {
        var sourcePath = Path.Combine(dir, "blank.pdf");
        File.WriteAllBytes(sourcePath, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));

        var outPath = Path.Combine(dir, $"text-{family}-{bold}-{italic}.pdf");
        await using (var source = await _pdfium.OpenAsync(sourcePath, null, CancellationToken.None))
        {
            var overlay = new TextOverlay(
                "Проверка шрифта", 72, 100, 24, 0xFF000000, 0, family, bold, italic);
            await _pdfium.ComposeAsync(
                new[] { new ComposedPage(source, 0, 0, new PageOverlay[] { overlay }) },
                outPath, CancellationToken.None);
        }

        await using var doc = await _pdfium.OpenAsync(outPath, null, CancellationToken.None);
        return await doc.GetTextObjectAtAsync(0, 0, 100, 112, CancellationToken.None);
    }

    [Theory]
    [InlineData("Times New Roman", "Times")]
    [InlineData("Courier New", "Courier")]
    [InlineData("Georgia", "Georgia")]
    public async Task Chosen_Family_Reaches_The_File(string family, string expectedInName)
    {
        // Гарнитуры, которых в этой системе нет, проверять нечем.
        if (PdfFontCatalog.ResolvePath(family, false, false) == null)
            return;

        var found = await WriteAndReadBackAsync(NewDir(), family, bold: false, italic: false);

        Assert.NotNull(found);
        Assert.Equal("Проверка шрифта", found!.Text);
        Assert.Contains(expectedInName, found.FontName, StringComparison.OrdinalIgnoreCase);
        Assert.True(found.IsEmbeddedFont, "шрифт обязан встроиться в файл, иначе текст поедет у получателя");
    }

    [Fact]
    public async Task Bold_And_Italic_Are_Separate_Faces_Not_A_Slanted_Regular()
    {
        if (PdfFontCatalog.ResolvePath("Times New Roman", true, true) == null)
            return;

        var dir = NewDir();
        var regular = await WriteAndReadBackAsync(dir, "Times New Roman", false, false);
        var boldItalic = await WriteAndReadBackAsync(dir, "Times New Roman", true, true);

        Assert.NotNull(regular);
        Assert.NotNull(boldItalic);
        // Настоящее начертание — отдельный файл шрифта, поэтому и имя другое.
        Assert.NotEqual(regular!.FontName, boldItalic!.FontName);
    }

    [Fact]
    public async Task Unknown_Family_Falls_Back_Instead_Of_Losing_The_Text()
    {
        // Текст важнее гарнитуры: если выбранного шрифта нет, надпись всё равно
        // обязана появиться в документе, а не исчезнуть молча.
        var found = await WriteAndReadBackAsync(NewDir(), "Такого Шрифта Нет", false, false);

        Assert.NotNull(found);
        Assert.Equal("Проверка шрифта", found!.Text);
    }
}
