using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Вёрстка блока — одна на экран и файл. Ширины и переносы здесь
/// проверяются точно: шрифт Mono500 даёт каждой букве ровно половину кегля.
/// </summary>
public sealed class TextBlockLayoutTests : IDisposable
{
    private const double Pad = TextBlockLayout.PaddingPt;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "layout-" + Guid.NewGuid().ToString("N"));
    private readonly FontCatalog _fonts;

    public TextBlockLayoutTests()
    {
        Directory.CreateDirectory(_dir);
        var mono = Enumerable.Range(0x20, 0x5F).Concat(Enumerable.Range(0x400, 0x60)).ToDictionary(c => c, _ => (ushort)500);
        File.WriteAllBytes(Path.Combine(_dir, "mono500.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Mono500", Advances: mono)));
        File.WriteAllBytes(Path.Combine(_dir, "latin.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Latin500")));
        File.WriteAllBytes(Path.Combine(_dir, "locked.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Locked", FsType: 0x0002)));
        _fonts = FontCatalog.Scan([_dir, TestFontPaths.Directory]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static TextStyle Mono(double size = 10) => new("Mono500", size);

    private static TextBlockOverlay Block(string text, TextStyle style, double width = 100,
        TextBlockWidthMode mode = TextBlockWidthMode.Fixed, TextAlign align = TextAlign.Left,
        double spacing = 1.0, bool autoHeight = true, double height = 50, bool shrink = false) =>
        new(0, 0, width, height, 0, mode, autoHeight, shrink,
            text.Split('\n').Select(p => new TextParagraph([new TextRun(p, style)], align, spacing)).ToList());

    private LaidOutBlock Lay(TextBlockOverlay block) => TextBlockLayout.Compute(block, _fonts);

    private static string[] Lines(LaidOutBlock layout) =>
        layout.Lines.Select(l => string.Concat(l.Pieces.Select(p => p.Text))).ToArray();

    [Fact]
    public void Auto_Width_Keeps_One_Line()
    {
        var layout = Lay(Block("AAA", new TextStyle("Liberation Sans", 10), width: 5, mode: TextBlockWidthMode.AutoWidth));

        var piece = Assert.Single(Assert.Single(layout.Lines).Pieces);
        var expected = 3 * 1366 / 2048.0 * 10;
        Assert.Equal(expected, piece.WidthPt, 3);
        Assert.Equal(expected + 2 * Pad, layout.WidthPt, 3);
    }

    [Fact]
    public void Fixed_Width_Wraps_At_Spaces()
    {
        var layout = Lay(Block("aaa bbb ccc", Mono(), width: 2 * Pad + 30));
        Assert.Equal(["aaa ", "bbb ", "ccc"], Lines(layout));
    }

    [Fact]
    public void Wraps_After_A_Hyphen()
    {
        var layout = Lay(Block("aaa-bbb", Mono(), width: 2 * Pad + 25));
        Assert.Equal(["aaa-", "bbb"], Lines(layout));
    }

    [Fact]
    public void Long_Word_Is_Split_By_Characters()
    {
        var layout = Lay(Block("abcdefghij", Mono(), width: 2 * Pad + 20));
        Assert.Equal(["abcd", "efgh", "ij"], Lines(layout));
    }

    [Fact]
    public void Forced_Line_Break_Starts_A_New_Line_And_Is_Not_Drawn()
    {
        var layout = Lay(Block("ab" + TextBlockText.LineBreak + "cd", Mono(), mode: TextBlockWidthMode.AutoWidth));

        Assert.Equal(["ab", "cd"], Lines(layout));
        Assert.Equal(3, layout.Lines[0].Length);   // строка включает сам перевод
        Assert.Equal(3, layout.Lines[1].Start);
    }

    [Fact]
    public void Line_Height_Uses_Font_Metrics_And_Spacing()
    {
        var layout = Lay(Block("a\nb", new TextStyle("Liberation Sans", 10), spacing: 1.15));

        var expected = (1854 + 434 + 67) / 2048.0 * 10 * 1.15;
        Assert.Equal(expected, layout.Lines[0].HeightPt, 3);
        Assert.Equal(layout.Lines[0].TopPt + expected, layout.Lines[1].TopPt, 3);
        Assert.Equal(Pad + 1854 / 2048.0 * 10, layout.Lines[0].BaselinePt, 3);
    }

    [Theory]
    [InlineData(TextAlign.Left, 2)]
    [InlineData(TextAlign.Center, 22)]
    [InlineData(TextAlign.Right, 42)]
    public void Alignment_Positions(TextAlign align, double x)
    {
        var layout = Lay(Block("ab", Mono(), width: 2 * Pad + 50, align: align));
        Assert.Equal(x, Assert.Single(layout.Lines[0].Pieces).XPt, 6);
    }

    [Fact]
    public void Justify_Spreads_Spaces_Except_Last_Line()
    {
        var layout = Lay(Block("aa bb cc dd", Mono(), width: 2 * Pad + 45, align: TextAlign.Justify));

        Assert.Equal(["aa bb cc ", "dd"], Lines(layout));
        var first = layout.Lines[0].Pieces;
        var lastLetter = first.Last(p => p.Text.Trim().Length > 0);
        Assert.Equal(Pad + 45, lastLetter.XPt + lastLetter.WidthPt, 6);   // правый край ровно у рамки
        Assert.Equal(Pad, layout.Lines[1].Pieces[0].XPt, 6);               // последняя строка — влево
    }

    [Fact]
    public void Superscript_Is_Smaller_And_Raised()
    {
        var block = new TextBlockOverlay(0, 0, 100, 50, 0, TextBlockWidthMode.Fixed, true, false,
            [new TextParagraph([new TextRun("a", Mono()), new TextRun("b", Mono() with { Baseline = BaselineShift.Superscript })])]);

        var pieces = Lay(block).Lines[0].Pieces;
        Assert.Equal(10 * 0.65, pieces[1].FontSizePt, 6);
        Assert.Equal(pieces[0].BaselinePt - 0.33 * 10, pieces[1].BaselinePt, 6);
    }

    [Fact]
    public void Letter_Spacing_Adds_After_Each_Character()
    {
        var layout = Lay(Block("abc", Mono() with { LetterSpacingPercent = 10 }, mode: TextBlockWidthMode.AutoWidth));
        var piece = Assert.Single(layout.Lines[0].Pieces);
        Assert.All(piece.AdvancesPt, a => Assert.Equal(6, a, 6));
    }

    [Fact]
    public void Missing_Glyph_Uses_Fallback_Face()
    {
        var layout = Lay(Block("aЖb", new TextStyle("Latin500", 10), mode: TextBlockWidthMode.AutoWidth));

        var pieces = layout.Lines[0].Pieces;
        Assert.Equal(["a", "Ж", "b"], pieces.Select(p => p.Text));
        Assert.True(pieces[1].IsFallback);
        Assert.False(pieces[0].IsFallback);
        Assert.Contains(pieces[1].Family, _fonts.FallbackFamilies);
        Assert.True(_fonts.Load(pieces[1].Face).HasGlyph('Ж'));
    }

    [Fact]
    public void Missing_Family_Is_Drawn_With_A_Substitute_And_Marked()
    {
        var layout = Lay(Block("ab", new TextStyle("Not Installed", 10), mode: TextBlockWidthMode.AutoWidth));

        var piece = Assert.Single(layout.Lines[0].Pieces);
        Assert.True(piece.IsFallback);
        Assert.Contains(piece.Family, _fonts.FallbackFamilies);
        Assert.False(Lay(Block("ab", Mono(), mode: TextBlockWidthMode.AutoWidth)).Lines[0].Pieces[0].IsFallback);
    }

    [Fact]
    public void Family_That_Forbids_Embedding_Is_Substituted()
    {
        // Такой шрифт установлен, но в PDF его вписывать нельзя (fsType):
        // блок, пришедший из файла с этой гарнитурой, пишется заменой.
        var piece = Assert.Single(Lay(Block("ab", new TextStyle("Locked", 10), mode: TextBlockWidthMode.AutoWidth)).Lines[0].Pieces);
        Assert.True(piece.IsFallback);
        Assert.NotEqual("Locked", piece.Family);
    }

    [Fact]
    public void Shrink_To_Fit_Reduces_Size_To_Fit()
    {
        var layout = Lay(Block("aaaa bbbb cccc dddd", Mono(20), width: 2 * Pad + 60,
            autoHeight: false, height: 2 * Pad + 20, shrink: true));

        Assert.False(layout.Overflows);
        Assert.True(layout.Scale < 1);
        Assert.True(layout.Lines.Sum(l => l.HeightPt) <= 20 + 1e-6);
        Assert.True(layout.Lines.SelectMany(l => l.Pieces).All(p => p.FontSizePt >= 4));
    }

    [Fact]
    public void Shrink_Never_Below_4pt_And_Reports_Overflow()
    {
        var layout = Lay(Block(string.Join(" ", Enumerable.Repeat("word", 400)), Mono(12), width: 2 * Pad + 30,
            autoHeight: false, height: 2 * Pad + 10, shrink: true));

        Assert.True(layout.Overflows);
        Assert.Equal(4, layout.Lines.SelectMany(l => l.Pieces).Min(p => p.FontSizePt), 6);
    }

    [Fact]
    public void Fixed_Height_Overflow_Is_Not_Clipped()
    {
        var text = "aaa bbb ccc ddd eee";
        var layout = Lay(Block(text, Mono(), width: 2 * Pad + 30, autoHeight: false, height: 2 * Pad + 10));

        Assert.True(layout.Overflows);
        Assert.Equal(text, string.Concat(layout.Lines.SelectMany(l => l.Pieces).Select(p => p.Text)));
        Assert.Equal(2 * Pad + 10, layout.HeightPt);
        // Редактор показывает и то, что ниже рамки: печатать вслепую нельзя.
        var last = layout.Lines[^1];
        Assert.Equal(last.TopPt + last.HeightPt + Pad, layout.ContentHeightPt, 6);
        Assert.True(layout.ContentHeightPt > layout.HeightPt);
    }

    [Fact]
    public void Auto_Height_Grows_With_Text()
    {
        var layout = Lay(Block("aaa bbb ccc", Mono(), width: 2 * Pad + 30, height: 5));
        Assert.Equal(2 * Pad + layout.Lines.Sum(l => l.HeightPt), layout.HeightPt, 6);
        Assert.Equal(layout.HeightPt, layout.ContentHeightPt, 6);
        Assert.False(layout.Overflows);
    }

    [Fact]
    public void Empty_Paragraph_Still_Has_A_Line()
    {
        var layout = Lay(Block("a\n\nb", Mono()));
        Assert.Equal(3, layout.Lines.Count);
        Assert.Empty(layout.Lines[1].Pieces);
        Assert.True(layout.Lines[1].HeightPt > 0);
    }

    [Fact]
    public void Underline_And_Strike_Are_Decorations_In_The_Run_Colour()
    {
        var style = Mono() with { Underline = true, Strike = true, ColorArgb = 0xFFD3282F };
        var layout = Lay(Block("ab", style, mode: TextBlockWidthMode.AutoWidth));

        Assert.Equal(2, layout.Decorations.Count);
        Assert.All(layout.Decorations, d => Assert.Equal(0xFFD3282Fu, d.ColorArgb));
        Assert.All(layout.Decorations, d => Assert.Equal(10, d.WidthPt, 6));
        var baseline = layout.Lines[0].BaselinePt;
        Assert.Contains(layout.Decorations, d => d.YPt > baseline);   // подчёркивание ниже базовой линии
        Assert.Contains(layout.Decorations, d => d.YPt < baseline);   // зачёркивание выше
    }
}
