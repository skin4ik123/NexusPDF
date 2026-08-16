using System.Text;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Состав печатного растра: аннотации и поля форм.
///
/// Проверяется на PDF, собранном вручную, потому что важен именно флаг Print у
/// отдельной аннотации: спецификация требует не печатать то, что автор пометил
/// как экранное, и раньше программа это требование не соблюдала — рендер знал
/// только «всё» и «ничего».
/// </summary>
public sealed class PrintContentTests : IAsyncLifetime
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

    /// <summary>
    /// Белая страница с тремя чёрными квадратами-аннотациями:
    /// печатной (флаг Print), экранной (без него) и полем формы.
    /// Квадраты не пересекаются, поэтому каждый проверяется своей точкой.
    /// </summary>
    private static string BuildPage(string path)
    {
        var objects = new List<string>
        {
            // 1 — каталог, 2 — дерево страниц, 3 — страница
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
            "/Annots [4 0 R 5 0 R 6 0 R] /Contents 7 0 R >>",

            // 4 — печатная аннотация: /F 4 = Print
            "<< /Type /Annot /Subtype /Square /Rect [20 220 80 280] /F 4 " +
            "/AP << /N 8 0 R >> >>",
            // 5 — экранная: флагов нет, печатать её нельзя
            "<< /Type /Annot /Subtype /Square /Rect [120 220 180 280] /F 0 " +
            "/AP << /N 8 0 R >> >>",
            // 6 — поле формы, тоже с флагом Print
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /Rect [220 220 280 280] " +
            "/F 4 /AP << /N 8 0 R >> >>",
        };

        const string content = "1 1 1 rg 0 0 300 300 re f";
        objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

        // 8 — общий вид аннотации: сплошной чёрный квадрат 60×60.
        const string appearance = "0 0 0 rg 0 0 60 60 re f";
        objects.Add("<< /Type /XObject /Subtype /Form /BBox [0 0 60 60] " +
                    $"/Length {appearance.Length} >>\nstream\n{appearance}\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
        return path;
    }

    /// <summary>Тёмная ли точка растра. Растр 300×300 при 1 пикселе на пункт.</summary>
    private static bool IsInk(RenderedPageImage image, int x, int y)
    {
        var offset = (long)y * image.Stride + x * 4;
        return image.Bgra[offset] < 128;
    }

    // Центры квадратов в координатах растра (ось Y вниз: 300 − 250 = 50).
    private const int PrintableX = 50, ScreenOnlyX = 150, FieldX = 250, RowY = 50;

    private async Task<RenderedPageImage> RenderAsync(string path, PrintContentOptions options)
    {
        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        return await handle.RenderPageForPrintAsync(0, 300, 300, 0, options, CancellationToken.None);
    }

    [Fact]
    public async Task Screen_Only_Annotations_Do_Not_Reach_The_Paper()
    {
        var path = BuildPage(Path.Combine(NewDir(), "annots.pdf"));

        var image = await RenderAsync(path, PrintContentOptions.Default);

        Assert.True(IsInk(image, PrintableX, RowY), "Печатная аннотация пропала с листа.");
        Assert.False(IsInk(image, ScreenOnlyX, RowY),
            "Аннотация без флага Print попала на бумагу — так спецификация запрещает.");
    }

    [Fact]
    public async Task All_Visible_Annotations_Is_A_Deliberate_Choice_And_Prints_Them()
    {
        var path = BuildPage(Path.Combine(NewDir(), "annots-all.pdf"));

        var image = await RenderAsync(path, new PrintContentOptions(
            IncludeAnnotations: true, OnlyPrintableAnnotations: false, IncludeFormFields: true));

        Assert.True(IsInk(image, PrintableX, RowY));
        Assert.True(IsInk(image, ScreenOnlyX, RowY),
            "«Все видимые» обязано печатать и экранные аннотации — за этим режим и выбирают.");
    }

    /// <summary>
    /// Пустой бланк: значения полей уходят, а комментарии остаются. Раньше обе
    /// политики сводились к одному content-only рендеру, и снятие полей молча
    /// уносило с листа всю разметку.
    /// </summary>
    [Fact]
    public async Task A_Blank_Form_Keeps_The_Comments()
    {
        var path = BuildPage(Path.Combine(NewDir(), "blank-form.pdf"));

        var image = await RenderAsync(path, new PrintContentOptions(
            IncludeAnnotations: true, OnlyPrintableAnnotations: true, IncludeFormFields: false));

        Assert.False(IsInk(image, FieldX, RowY), "Поле формы напечаталось на пустом бланке.");
        Assert.True(IsInk(image, PrintableX, RowY),
            "Вместе с полями с листа исчезли и комментарии.");
    }

    [Fact]
    public async Task Document_Only_Prints_Neither_Annotations_Nor_Fields()
    {
        var path = BuildPage(Path.Combine(NewDir(), "doc-only.pdf"));

        var image = await RenderAsync(path, PrintContentOptions.DocumentOnly);

        Assert.False(IsInk(image, PrintableX, RowY));
        Assert.False(IsInk(image, ScreenOnlyX, RowY));
        Assert.False(IsInk(image, FieldX, RowY));
    }

    /// <summary>
    /// Фильтр ставит флаг Hidden на время отрисовки — документ обязан вернуться
    /// в прежний вид, иначе второй рендер подряд дал бы другую картинку.
    /// </summary>
    [Fact]
    public async Task Filtering_Does_Not_Leave_The_Document_Changed()
    {
        var path = BuildPage(Path.Combine(NewDir(), "restore.pdf"));
        var bytesBefore = File.ReadAllBytes(path);

        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var filtered = await handle.RenderPageForPrintAsync(
            0, 300, 300, 0, PrintContentOptions.Default, CancellationToken.None);
        var afterFilter = await handle.RenderPageForPrintAsync(
            0, 300, 300, 0, new PrintContentOptions(true, false, true), CancellationToken.None);

        // Второй рендер видит экранную аннотацию: флаг Hidden снят.
        Assert.False(IsInk(filtered, ScreenOnlyX, RowY));
        Assert.True(IsInk(afterFilter, ScreenOnlyX, RowY),
            "После фильтрации аннотация осталась скрытой — флаг не вернули.");
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
    }
}
