using System.Text;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Описание страницы для правки текста и заполнения бланков: какие текстовые
/// объекты где нарисованы, какие тонкие линии и границы таблиц есть рядом.
/// Всё в отображаемых пунктах — там, где их видит пользователь.
/// </summary>
public sealed class PageTextLayoutTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "layout-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pdfium.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<PageTextLayout> LayoutOf(byte[] pdf, int extraQuarterTurns = 0)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, pdf);
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        return await doc.GetPageTextLayoutAsync(0, extraQuarterTurns, CancellationToken.None);
    }

    private Task<PageTextLayout> LayoutOf(string content, int rotate = 0, int extraQuarterTurns = 0) =>
        LayoutOf(PdfFixture.Build(new PdfFixture.PageSpec(612, 792, rotate, RawContent: content)), extraQuarterTurns);

    /// <summary>PDF из готовых объектов: 1 — каталог, 2 — страницы, дальше как задано.</summary>
    private static byte[] Pdf(params string[] objects)
    {
        var all = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" }.Concat(objects).ToList();
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < all.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{all[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {all.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {all.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string content, string dictionary = "") =>
        $"<< {dictionary} /Length {content.Length} >>\nstream\n{content}\nendstream";

    [Fact]
    public async Task Text_Objects_Carry_Text_Origin_And_Size()
    {
        var layout = await LayoutOf("BT /F1 12 Tf 72 700 Td (Hello world) Tj ET BT /F1 12 Tf 72 686 Td (Second line) Tj ET");

        Assert.Equal(2, layout.Objects.Count);
        var (first, second) = (layout.Objects[0], layout.Objects[1]);
        Assert.Equal("Hello world", first.Text);
        Assert.Equal("Second line", second.Text);
        Assert.Equal([0], first.Path);
        Assert.Equal([1], second.Path);
        Assert.Equal(72, first.OriginXPt, 2);
        Assert.Equal(92, first.OriginYPt, 2);
        Assert.Equal(106, second.OriginYPt, 2);
        Assert.Equal(12, first.FontSizePt, 3);
        Assert.Equal(0, first.AngleDeg, 3);
        Assert.Contains("Helvetica", first.FontName);
        Assert.False(first.Bold || first.Italic || first.Invisible || first.Type3 || first.InForm);
        Assert.Equal(0xFF000000u, first.ColorArgb);

        // Буквы — по местам: у «H» в Helvetica ширина 0,722 кегля.
        Assert.Equal(first.Text.Length, first.CharAdvancesPt.Count);
        Assert.Equal(0.722 * 12, first.CharAdvancesPt[0], 1);
        // Сумма — ширина строки по таблице Helvetica (4,945 кегля), а не плотная рамка букв.
        Assert.Equal(4.945 * 12, first.CharAdvancesPt.Sum(), 1);
        Assert.InRange(first.BoundsPt.YPt, 92 - 12, 92);
        Assert.InRange(first.BoundsPt.YPt + first.BoundsPt.HeightPt, 92, 92 + 4);
    }

    [Fact]
    public async Task Matrix_Scale_Halves_The_Font_Size()
    {
        var layout = await LayoutOf("q 0.5 0 0 0.5 0 0 cm BT /F1 20 Tf 144 1400 Td (Half) Tj ET Q");

        var text = Assert.Single(layout.Objects);
        Assert.Equal(10, text.FontSizePt, 3);
        Assert.Equal(72, text.OriginXPt, 2);
        Assert.Equal(92, text.OriginYPt, 2);
    }

    [Theory]
    [InlineData(90, 0)]
    [InlineData(0, 1)]
    public async Task Rotated_Page_Gives_Displayed_Coordinates(int rotate, int extraQuarterTurns)
    {
        var layout = await LayoutOf("BT /F1 12 Tf 72 700 Td (Turn) Tj ET", rotate, extraQuarterTurns);

        var text = Assert.Single(layout.Objects);
        // Поворот по часовой: верхний левый угол содержимого уходит вправо вверх.
        Assert.Equal(700, text.OriginXPt, 2);
        Assert.Equal(72, text.OriginYPt, 2);
        Assert.Equal(90, text.AngleDeg, 3);
        Assert.True(text.BoundsPt.XPt + text.BoundsPt.WidthPt <= 792);
        Assert.True(text.BoundsPt.HeightPt > text.BoundsPt.WidthPt);
    }

    [Fact]
    public async Task Rotated_Text_Matrix_Gives_The_Angle()
    {
        var layout = await LayoutOf("BT /F1 12 Tf 0 1 -1 0 300 300 Tm (Up) Tj ET");
        Assert.Equal(-90, Assert.Single(layout.Objects).AngleDeg, 3);
    }

    [Fact]
    public async Task Underline_Rect_Is_One_Line_Object()
    {
        var layout = await LayoutOf("BT /F1 12 Tf 72 700 Td (Under) Tj ET 0 0 1 rg 72 697 40 0.8 re f");

        var line = Assert.Single(layout.Lines);
        Assert.Equal([1], line.Path);
        Assert.Equal(72, line.BoundsPt.XPt, 1);
        Assert.Equal(40, line.BoundsPt.WidthPt, 1);
        Assert.Equal(94.2, line.BoundsPt.YPt, 1);
        Assert.True(line.BoundsPt.HeightPt < 1.5);
        Assert.Equal(0xFF0000FFu, line.ColorArgb);
    }

    [Fact]
    public async Task Thick_Bar_And_Dot_Are_Not_Lines()
    {
        var layout = await LayoutOf("72 600 200 4 re f 72 500 1 1 re f");
        Assert.Empty(layout.Lines);
    }

    [Fact]
    public async Task Grid_Gives_Rulings_In_Displayed_Points()
    {
        var content = new StringBuilder("0.5 w ");
        foreach (var y in new[] { 700, 680, 660, 640 }) content.Append($"72 {y} m 272 {y} l S ");
        foreach (var x in new[] { 72, 172, 272 }) content.Append($"{x} 640 m {x} 700 l S ");

        var layout = await LayoutOf(content.ToString());

        var horizontal = layout.Rulings.Where(r => r.Horizontal).OrderBy(r => r.PositionPt).ToList();
        var vertical = layout.Rulings.Where(r => !r.Horizontal).OrderBy(r => r.PositionPt).ToList();
        Assert.Equal([92.0, 112.0, 132.0, 152.0], horizontal.Select(r => Math.Round(r.PositionPt, 1)));
        Assert.Equal([72.0, 172.0, 272.0], vertical.Select(r => Math.Round(r.PositionPt, 1)));
        Assert.All(horizontal, r => Assert.InRange(r.StartPt, 71.4, 72.1));
        Assert.All(horizontal, r => Assert.InRange(r.EndPt, 271.9, 272.6));
        Assert.All(vertical, r => Assert.InRange(r.StartPt, 91.4, 92.1));
        Assert.All(vertical, r => Assert.InRange(r.EndPt, 151.9, 152.6));
    }

    [Fact]
    public async Task Rulings_Turn_With_The_Page()
    {
        var layout = await LayoutOf("0.5 w 72 700 m 272 700 l S", rotate: 90);
        var ruling = Assert.Single(layout.Rulings);
        Assert.False(ruling.Horizontal);
        Assert.Equal(700, ruling.PositionPt, 1);
    }

    [Fact]
    public async Task Text_Inside_A_Form_Is_Marked_InForm()
    {
        var pdf = Pdf(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 1 0 0 1 100 600 cm /X1 Do Q"),
            Stream("BT /F1 12 Tf 0 0 Td (Nested) Tj ET 0 -3 50 0.6 re f",
                "/Type /XObject /Subtype /Form /BBox [0 -10 300 40] /Resources << /Font << /F1 6 0 R >> >>"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var layout = await LayoutOf(pdf);

        var text = Assert.Single(layout.Objects);
        Assert.Equal("Nested", text.Text);
        Assert.True(text.InForm);
        Assert.Equal(2, text.Path.Count);
        Assert.Equal(100, text.OriginXPt, 2);
        Assert.Equal(192, text.OriginYPt, 2);
        // Линия внутри формы — тоже на своём месте страницы.
        var line = Assert.Single(layout.Lines);
        Assert.Equal(2, line.Path.Count);
        Assert.Equal(100, line.BoundsPt.XPt, 1);
        Assert.Equal(194.4, line.BoundsPt.YPt, 1);
    }

    [Fact]
    public async Task Render_Mode_3_Is_Invisible()
    {
        var layout = await LayoutOf("BT 3 Tr /F1 12 Tf 72 700 Td (Hidden) Tj ET");
        Assert.True(Assert.Single(layout.Objects).Invisible);
    }

    [Fact]
    public async Task Bold_And_Italic_Come_From_The_Font()
    {
        var pdf = Pdf(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("BT /F1 12 Tf 72 700 Td (Strong) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-BoldOblique >>");

        var text = Assert.Single((await LayoutOf(pdf)).Objects);
        Assert.True(text.Bold);
        Assert.True(text.Italic);
    }

    [Fact]
    public async Task Type3_Font_Is_Flagged()
    {
        var pdf = Pdf(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("BT /F1 12 Tf 72 700 Td (a) Tj ET"),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
            "/CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> " +
            "/FirstChar 97 /LastChar 97 /Widths [1000] /ToUnicode 7 0 R >>",
            Stream("1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f"),
            Stream("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T3 def " +
                   "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <61> <0061> endbfchar " +
                   "endcmap CMapName currentdict /CMap defineresource pop end end"));

        var text = Assert.Single((await LayoutOf(pdf)).Objects);
        Assert.True(text.Type3);
    }

    [Fact]
    public async Task Fingerprint_Uses_Content_Coordinates()
    {
        var plain = Assert.Single((await LayoutOf("BT /F1 12 Tf 72 700 Td (Same) Tj ET")).Objects);
        var turned = Assert.Single((await LayoutOf("BT /F1 12 Tf 72 700 Td (Same) Tj ET", extraQuarterTurns: 1)).Objects);
        var moved = Assert.Single((await LayoutOf("BT /F1 12 Tf 72 701 Td (Same) Tj ET")).Objects);

        Assert.Equal(PageTextFingerprint.Of("Same", 72, 700, 12), plain.Fingerprint);
        Assert.Equal(plain.Fingerprint, turned.Fingerprint);
        Assert.NotEqual(plain.Fingerprint, moved.Fingerprint);
    }
}
