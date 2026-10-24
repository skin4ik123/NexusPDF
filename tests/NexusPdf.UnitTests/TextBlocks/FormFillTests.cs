using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Блок для места в бланке: в клетке — ровно по клетке, текст по центру по
/// высоте и не крупнее, чем клетка вмещает; на строке — стоит на линии до её
/// конца; отметка ✓ — по центру клетки размером в 70 % её высоты.
/// </summary>
public sealed class FormFillTests
{
    private const double Pad = TextBlockLayout.PaddingPt;
    private static readonly TextStyle Style = new("Liberation Sans", 11);

    private static FormTarget Cell(double x, double y, double w, double h) =>
        new(FormTargetKind.Cell, new OverlayBox(x, y, w, h), new OverlayBox(x, y, w, h), 0, 0, 0);

    [Fact]
    public void Cell_Block_Fills_The_Cell_And_Centres_The_Text()
    {
        var block = FormFill.BlockFor(Cell(100, 200, 80, 30), Style, TextAlign.Left, 1.15);

        Assert.Equal((101.0, 201.0, 78.0, 28.0), (block.XPt, block.YPt, block.WidthPt, block.HeightPt));
        Assert.Equal(TextBlockWidthMode.Fixed, block.WidthMode);
        Assert.False(block.AutoHeight);
        Assert.True(block.ShrinkToFit);
        Assert.Equal(TextVerticalAlign.Center, block.VerticalAlign);
        Assert.NotEqual(Guid.Empty, block.BlockId);
        var paragraph = Assert.Single(block.Paragraphs);
        Assert.Equal(TextAlign.Left, paragraph.Align);
        Assert.Equal(11, Assert.Single(paragraph.Runs).Style.FontSizePt);
        Assert.Equal("", paragraph.Text);
    }

    [Fact]
    public void Font_Size_Is_Capped_By_A_Low_Cell()
    {
        var block = FormFill.BlockFor(Cell(100, 200, 80, 12), Style, TextAlign.Left, 1.15);
        // Рамка 10 пт: (10 − 2·2) / 1,2 = 5 пт.
        Assert.Equal((10 - 2 * Pad) / 1.2, block.Paragraphs[0].Runs[0].Style.FontSizePt, 6);
    }

    [Fact]
    public void Font_Size_Never_Drops_Below_The_Minimum()
    {
        var block = FormFill.BlockFor(Cell(100, 200, 80, 6), Style, TextAlign.Left, 1.15);
        Assert.Equal(TextBlockLayout.MinimumFontSizePt, block.Paragraphs[0].Runs[0].Style.FontSizePt, 6);
    }

    [Fact]
    public void Caption_Cell_Uses_Its_Free_Part()
    {
        var target = new FormTarget(FormTargetKind.CaptionCell, new OverlayBox(160, 200, 140, 30),
            new OverlayBox(100, 200, 200, 30), 0, 0, 0);
        var block = FormFill.BlockFor(target, Style, TextAlign.Left, 1.15);
        Assert.Equal((161.0, 201.0, 138.0, 28.0), (block.XPt, block.YPt, block.WidthPt, block.HeightPt));
        Assert.Equal(TextVerticalAlign.Center, block.VerticalAlign);
    }

    [Fact]
    public void Line_Block_Stands_On_The_Line_Up_To_Its_End()
    {
        // Полоса над линией y = 300: низ — на 1 пт выше линии.
        var target = new FormTarget(FormTargetKind.Line, new OverlayBox(150, 284.5, 150, 14.5),
            new OverlayBox(100, 284.5, 200, 14.5), -1, 0, 0);

        var block = FormFill.BlockFor(target, Style, TextAlign.Left, 1.15);

        Assert.Equal(150, block.XPt, 6);
        Assert.Equal(300, block.XPt + block.WidthPt, 6);
        Assert.Equal(299, block.YPt + block.HeightPt, 6);
        Assert.True(block.HeightPt >= 11 * 1.2 + 2 * Pad - 1e-9);
        Assert.Equal(TextVerticalAlign.Bottom, block.VerticalAlign);
        Assert.Equal(TextBlockWidthMode.Fixed, block.WidthMode);
        Assert.False(block.AutoHeight);
        Assert.True(block.ShrinkToFit);
    }

    [Fact]
    public void Text_Blank_Is_Filled_Like_A_Line()
    {
        var target = new FormTarget(FormTargetKind.TextBlank, new OverlayBox(120, 390, 40, 12.5),
            new OverlayBox(120, 390, 40, 12.5), -1, 0, 0);
        var block = FormFill.BlockFor(target, Style, TextAlign.Left, 1.15);
        Assert.Equal(120, block.XPt, 6);
        Assert.Equal(160, block.XPt + block.WidthPt, 6);
        Assert.Equal(402.5, block.YPt + block.HeightPt, 6);
        Assert.Equal(TextVerticalAlign.Bottom, block.VerticalAlign);
    }

    [Fact]
    public void Mark_In_A_Cell_Is_Centred_At_Seventy_Percent_Of_Its_Height()
    {
        var block = FormFill.MarkFor(Cell(100, 200, 40, 20), 0, 0, '✓', Style);

        var paragraph = Assert.Single(block.Paragraphs);
        Assert.Equal("✓", paragraph.Text);
        Assert.Equal(TextAlign.Center, paragraph.Align);
        Assert.Equal(14, paragraph.Runs[0].Style.FontSizePt, 6);
        Assert.Equal(TextVerticalAlign.Center, block.VerticalAlign);
        Assert.Equal(120, block.XPt + block.WidthPt / 2, 6);
        Assert.Equal(210, block.YPt + block.HeightPt / 2, 6);
        Assert.True(block.ShrinkToFit);
    }

    [Fact]
    public void Mark_Without_A_Target_Sits_At_The_Point()
    {
        var block = FormFill.MarkFor(null, 300, 400, '●', Style);

        Assert.Equal("●", block.Paragraphs[0].Text);
        Assert.Equal(11, block.Paragraphs[0].Runs[0].Style.FontSizePt, 6);
        Assert.Equal(TextBlockWidthMode.AutoWidth, block.WidthMode);
        Assert.True(block.AutoHeight);
        Assert.InRange(300 - block.XPt, 0, 2 * Pad);
        Assert.InRange(400, block.YPt, block.YPt + block.HeightPt);
    }

    [Fact]
    public void Mark_Style_Takes_A_Font_That_Has_The_Mark()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "marks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var latin = Enumerable.Range(0x20, 0x5F).ToDictionary(c => c, _ => (ushort)500);
            File.WriteAllBytes(Path.Combine(dir, "plain.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Plain", Advances: latin)));
            File.WriteAllBytes(Path.Combine(dir, "marks.ttf"), TinyFontBuilder.Build(
                new TinyFontSpec("Marks", Advances: new Dictionary<int, ushort> { [0x20] = 300, [0x2713] = 800 })));
            var fonts = FontCatalog.Scan([dir]);
            var plain = new TextStyle("Plain", 11) with { Bold = false };

            Assert.Equal("Marks", FormFill.MarkStyle(fonts, plain, '\u2713')!.FontFamily);
            Assert.Equal(11, FormFill.MarkStyle(fonts, plain, '\u2713')!.FontSizePt);
            Assert.Equal("Plain", FormFill.MarkStyle(fonts, plain, 'x')!.FontFamily);
            Assert.Null(FormFill.MarkStyle(fonts, plain, '\u25CF'));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
