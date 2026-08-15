using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Правка СУЩЕСТВУЮЩЕГО текста страницы: клик находит строку, замена
/// сохраняет шрифт, кегль и место, а невозможность нарисовать новые буквы
/// обнаруживается ДО сохранения, а не после.
/// </summary>
public sealed class TextObjectEditTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string WriteSource(string dir, string text = "ORIGINALTEXT")
    {
        var path = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: text)));
        return path;
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Click_Finds_The_Text_Object_With_Its_Font_And_Size()
    {
        var dir = NewDir();
        await using var doc = await _pdfium.OpenAsync(WriteSource(dir), null, CancellationToken.None);

        // Фикстура ставит текст 24 пт в точке (72, 72) снизу — сверху это ~700.
        var found = await doc.GetTextObjectAtAsync(0, 0, 110, 703, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("ORIGINALTEXT", found!.Text);
        Assert.Equal(24, found.FontSizePt, 1);
        Assert.Contains("Helvetica", found.FontName);
        Assert.False(found.IsEmbeddedFont); // стандартный шрифт, не встроенный
        Assert.True(found.WidthPt > 50);

        // Мимо текста ничего не находится.
        Assert.Null(await doc.GetTextObjectAtAsync(0, 0, 500, 200, CancellationToken.None));
    }

    [Fact]
    public async Task Replacing_Text_Keeps_Font_Size_And_Position()
    {
        var dir = NewDir();
        var source = WriteSource(dir);
        var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None);
        PdfTextObject before;
        await using (document)
        {
            before = (await document.PrimaryHandle.GetTextObjectAtAsync(
                0, 0, 110, 703, CancellationToken.None))!;

            document.Session.Apply(new AddOverlayOperation(0,
                new TextObjectReplacement(before.ObjectIndex, "REPLACEDWORD")));

            var saved = Path.Combine(dir, "edited.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            await using var result = await _pdfium.OpenAsync(saved, null, CancellationToken.None);

            var text = await result.GetPageTextAsync(0, CancellationToken.None);
            Assert.Contains("REPLACEDWORD", text);
            Assert.DoesNotContain("ORIGINALTEXT", text);

            var after = await result.GetTextObjectAtAsync(0, 0, 110, 703, CancellationToken.None);
            Assert.NotNull(after);
            Assert.Equal("REPLACEDWORD", after!.Text);
            Assert.Equal(before.FontSizePt, after.FontSizePt, 1);
            Assert.Equal(before.FontName, after.FontName);
            // Строка осталась на прежнем месте. Рамка сравнивается с допуском
            // 2 пт: она описывает ЧЕРНИЛА, а у разных букв разные выносы, так
            // что при той же точке привязки края отличаются на доли кегля.
            Assert.True(Math.Abs(before.YPt - after.YPt) <= 2,
                $"строка уехала по вертикали: было {before.YPt}, стало {after.YPt}");
            Assert.True(Math.Abs(before.XPt - after.XPt) <= 2,
                $"строка уехала по горизонтали: было {before.XPt}, стало {after.XPt}");
        }
    }

    [Fact]
    public async Task Font_Coverage_Is_Checked_By_Actually_Drawing_The_Text()
    {
        var dir = NewDir();
        await using var doc = await _pdfium.OpenAsync(WriteSource(dir), null, CancellationToken.None);
        var found = await doc.GetTextObjectAtAsync(0, 0, 110, 703, CancellationToken.None);

        // Латиница у Helvetica есть.
        Assert.True(await doc.CanFontRenderTextAsync(
            0, found!.ObjectIndex, "NEW TEXT", CancellationToken.None));

        // Пробелы рисует любой шрифт.
        Assert.True(await doc.CanFontRenderTextAsync(
            0, found.ObjectIndex, "   ", CancellationToken.None));
    }

    [Fact]
    public async Task Replacement_Of_A_Vanished_Object_Fails_Loudly()
    {
        var dir = NewDir();
        var document = await OpenedDocument.OpenAsync(
            _pdfium, WriteSource(dir), null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new TextObjectReplacement(9999, "НЕВАЖНО")));

            var saved = Path.Combine(dir, "broken.pdf");
            var error = await Assert.ThrowsAsync<PdfEngineException>(() =>
                new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None));
            Assert.Contains("не найден", error.Message);
            // Битый результат не остаётся на диске.
            Assert.False(File.Exists(saved));
        }
    }
}
