using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Текст внутри Form XObject — вложенного потока рисования.
///
/// Так устроена добрая половина документов: содержимое страницы завёрнуто в
/// форму. Обход только верхнего уровня страницы не находил в них НИ ОДНОГО
/// текстового объекта, и правка текста молча не работала на всём таком классе
/// файлов, а тесты этого не видели — фикстура кладёт текст прямо на страницу.
/// </summary>
public sealed class NestedTextObjectTests : IAsyncLifetime
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

    /// <summary>
    /// Документ, где текст лежит НЕ на странице, а внутри формы, а сама форма
    /// сдвинута на странице. Сдвиг важен: он проверяет, что рамка вложенного
    /// объекта пересчитывается матрицей формы, а не берётся как есть.
    /// </summary>
    private static string WriteNestedPdf(string dir, double formShiftX, double formShiftY)
    {
        var path = Path.Combine(dir, "nested.pdf");

        var form = "BT /F1 24 Tf 0 0 Td (NESTEDTEXT) Tj ET\n";
        var page = $"q 1 0 0 1 {formShiftX} {formShiftY} cm /X1 Do Q\n";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {page.Length} >>\nstream\n{page}endstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 300 40] " +
            "/Resources << /Font << /F1 6 0 R >> >> " +
            $"/Length {form.Length} >>\nstream\n{form}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    [Fact]
    public async Task Text_Inside_A_Form_Is_Found_By_Click()
    {
        var dir = NewDir();
        // Форма сдвинута в (100, 600) от низа страницы; текст рисуется в её
        // начале координат, значит на странице он окажется примерно там же.
        var path = WriteNestedPdf(dir, 100, 600);

        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        // Клик в отображаемых координатах: сверху вниз, 792 − 600 − высота строки.
        var found = await doc.GetTextObjectAtAsync(0, 0, 130, 180, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("NESTEDTEXT", found!.Text);
        // Путь обязан быть вложенным: объект страницы, внутри него — текст.
        Assert.True(found.ObjectPath.Count >= 2,
            $"ожидался путь во вложенный объект, получен [{string.Join(", ", found.ObjectPath)}]");
    }

    [Fact]
    public async Task Bounds_Follow_The_Form_Matrix()
    {
        var dir = NewDir();
        var near = WriteNestedPdf(NewDir(), 100, 600);
        var far = WriteNestedPdf(dir, 300, 600);

        await using var docNear = await _pdfium.OpenAsync(near, null, CancellationToken.None);
        await using var docFar = await _pdfium.OpenAsync(far, null, CancellationToken.None);

        var a = await docNear.GetTextObjectAtAsync(0, 0, 130, 180, CancellationToken.None);
        var b = await docFar.GetTextObjectAtAsync(0, 0, 330, 180, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);
        // Сдвиг формы на 200 пт обязан сдвинуть и рамку текста: если бы матрица
        // не учитывалась, обе рамки оказались бы в одном месте.
        Assert.InRange(b!.XPt - a!.XPt, 180, 220);
    }

    [Fact]
    public async Task Nested_Text_Is_Reported_As_Not_Editable()
    {
        var dir = NewDir();
        var path = WriteNestedPdf(dir, 100, 600);

        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var found = await doc.GetTextObjectAtAsync(0, 0, 130, 180, CancellationToken.None);

        Assert.NotNull(found);
        // Найти получается, а сохранить правку — нет: PDFium перегенерирует
        // поток страницы и не трогает поток формы. Программа обязана знать это
        // ЗАРАНЕЕ и не предлагать правку, которая пропадёт при сохранении.
        Assert.False(found!.CanEdit);
    }

    [Fact]
    public async Task Saving_A_Nested_Edit_Fails_Loudly_Instead_Of_Losing_It()
    {
        var dir = NewDir();
        var path = WriteNestedPdf(dir, 100, 600);

        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var found = await doc.GetTextObjectAtAsync(0, 0, 130, 180, CancellationToken.None);
        Assert.NotNull(found);

        var outPath = Path.Combine(dir, "edited.pdf");
        // Молчаливая потеря правки хуже отказа: пользователь ушёл бы с
        // уверенностью, что текст изменён, а в файле осталось бы старое.
        await Assert.ThrowsAsync<PdfEngineException>(() => _pdfium.ComposeAsync(
            new[]
            {
                new ComposedPage(doc, 0, 0,
                    new PageOverlay[] { new TextObjectReplacement(found!.ObjectPath, "CHANGEDWORD") }),
            },
            outPath, CancellationToken.None));
    }

    [Fact]
    public async Task Plain_Page_Text_Stays_Editable()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "plain.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "PLAINTEXT")));

        await using var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        var found = await doc.GetTextObjectAtAsync(0, 0, 110, 703, CancellationToken.None);

        Assert.NotNull(found);
        // Текст прямо на странице как правился, так и правится: спуск в формы
        // не должен был ничего сломать в обычном случае.
        Assert.True(found!.CanEdit);
        Assert.Single(found.ObjectPath);
    }

    [Fact]
    public async Task Font_Check_Reaches_The_Nested_Object()
    {
        var dir = NewDir();
        var path = WriteNestedPdf(dir, 100, 600);

        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var found = await doc.GetTextObjectAtAsync(0, 0, 130, 180, CancellationToken.None);
        Assert.NotNull(found);

        // Helvetica рисует латиницу — проверка обязана дойти до вложенного
        // объекта, а не потеряться на верхнем уровне и вернуть false.
        Assert.True(await doc.CanFontRenderTextAsync(
            0, found!.ObjectPath, "PLAINLATIN", CancellationToken.None));
    }
}
