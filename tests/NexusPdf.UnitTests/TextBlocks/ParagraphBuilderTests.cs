using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Абзацы чужого текста: строки — по общей базовой линии, абзац — по
/// одинаковому шагу строк и общему краю; блок из абзаца стоит ровно там, где
/// был текст. Шрифт Mono500 — буква в половину кегля, строка в один кегль.
/// </summary>
public sealed class ParagraphBuilderTests : IDisposable
{
    private const double Pad = TextBlockLayout.PaddingPt;
    private const char Break = TextBlockText.LineBreak;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "paragraphs-" + Guid.NewGuid().ToString("N"));
    private readonly FontCatalog _fonts;

    public ParagraphBuilderTests()
    {
        Directory.CreateDirectory(_dir);
        var mono = Enumerable.Range(0x20, 0x5F).Concat(Enumerable.Range(0x400, 0x60)).ToDictionary(c => c, _ => (ushort)500);
        File.WriteAllBytes(Path.Combine(_dir, "mono500.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Mono500", Advances: mono)));
        File.WriteAllBytes(Path.Combine(_dir, "mono500b.ttf"), TinyFontBuilder.Build(
            new TinyFontSpec("Mono500", "Bold Italic", WeightClass: 700, Italic: true, Advances: mono)));
        _fonts = FontCatalog.Scan([_dir]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static PageTextObject Obj(string text, double x, double baseline, double size = 10, string font = "Mono500",
        bool bold = false, bool italic = false, double angle = 0, bool inForm = false, bool type3 = false,
        bool invisible = false, uint color = 0xFF000000)
    {
        var width = text.Length * size / 2;
        var bounds = angle == 90
            ? new OverlayBox(x - 0.2 * size, baseline, size, width)
            : new OverlayBox(x, baseline - 0.8 * size, width, size);
        return new PageTextObject([0], text, bounds, x, baseline, angle, size, font, bold, italic, color,
            invisible, type3, inForm, Enumerable.Repeat(size / 2, text.Length).ToList(), "");
    }

    private IReadOnlyList<DetectedParagraph> Build(IEnumerable<PageTextObject> objects,
        IEnumerable<PageLineObject>? lines = null, IEnumerable<PageRuling>? rulings = null)
    {
        var layout = new PageTextLayout(objects.ToList(), (lines ?? []).ToList(), (rulings ?? []).ToList());
        return ParagraphBuilder.Build(layout, FormTargetIndex.Build(layout, 10), _fonts);
    }

    private DetectedParagraph Single(params PageTextObject[] objects) => Assert.Single(Build(objects));

    private static TextParagraph Paragraph(DetectedParagraph detected) => Assert.Single(detected.Block!.Paragraphs);

    private static List<PageTextObject> Lines(double x, double firstBaseline, double pitch, params string[] texts) =>
        texts.Select((t, i) => Obj(t, x, firstBaseline + i * pitch)).ToList();

    [Fact]
    public void Two_Columns_Are_Two_Paragraphs()
    {
        var left = Lines(50, 100, 12, "aaa bbb", "ccc ddd", "eee");
        var right = Lines(300, 100, 12, "fff ggg", "hhh");

        var found = Build(left.Concat(right)).OrderBy(p => p.BoundsPt.XPt).ToList();

        Assert.Equal(2, found.Count);
        Assert.Equal("aaa bbb ccc ddd eee", Paragraph(found[0]).Text);
        Assert.Equal("fff ggg hhh", Paragraph(found[1]).Text);
        Assert.Equal(3, found[0].Objects.Count);
    }

    [Fact]
    public void Lines_Far_Apart_Are_Separate_Paragraphs()
    {
        var objects = Lines(50, 100, 12, "aaa bbb", "ccc").Concat(Lines(50, 150, 12, "ddd eee", "fff"));
        Assert.Equal(2, Build(objects).Count);
    }

    [Fact]
    public void Block_Stands_Where_The_Text_Was()
    {
        var detected = Single(Lines(50, 100, 12, "aaa bbb", "ccc ddd", "eee").ToArray());
        var block = detected.Block!;

        var layout = TextBlockLayout.Compute(block, _fonts);

        Assert.Equal(TextBlockWidthMode.Fixed, block.WidthMode);
        Assert.True(block.AutoHeight);
        Assert.Equal(3, layout.Lines.Count);
        Assert.Equal(50, block.XPt + layout.Lines[0].Pieces[0].XPt, 2);
        Assert.Equal(100, block.YPt + layout.Lines[0].BaselinePt, 2);
        Assert.Equal(112, block.YPt + layout.Lines[1].BaselinePt, 2);
        Assert.Equal(124, block.YPt + layout.Lines[2].BaselinePt, 2);
        Assert.Equal(1.2, Paragraph(detected).LineSpacing, 3);
        Assert.Null(detected.RefusedKey);
    }

    [Fact]
    public void Centred_Lines_Are_Center()
    {
        var detected = Single(Obj("aaaaaaa bb", 175, 100), Obj("ccc", 192.5, 112), Obj("dddd eee", 180, 124));
        Assert.Equal(TextAlign.Center, Paragraph(detected).Align);
    }

    [Fact]
    public void Right_Edge_Lines_Are_Right()
    {
        var detected = Single(Obj("aaaaaaa bb", 150, 100), Obj("ccc ddd", 165, 112), Obj("eee", 185, 124));
        Assert.Equal(TextAlign.Right, Paragraph(detected).Align);
    }

    [Fact]
    public void Justified_Lines_Are_Justify_And_Word_Objects_Get_Spaces()
    {
        // Пробелы растянуты до 10 и 5 пт, правый край строк — общий, последняя короче.
        var detected = Single(
            Obj("aaaa", 50, 100), Obj("bbbbbb", 80, 100),
            Obj("ccc", 50, 112), Obj("dddddddd", 70, 112),
            Obj("ee", 50, 124), Obj("f", 65, 124));

        var paragraph = Paragraph(detected);
        Assert.Equal(TextAlign.Justify, paragraph.Align);
        Assert.Equal("aaaa bbbbbb ccc dddddddd ee f", paragraph.Text);
    }

    [Fact]
    public void Touching_Objects_Join_Without_A_Space()
    {
        Assert.Equal("aabb", Paragraph(Single(Obj("aa", 50, 100), Obj("bb", 60, 100))).Text);
    }

    [Fact]
    public void Hyphen_At_The_End_Of_A_Line_Joins_Without_A_Space()
    {
        var detected = Single(Lines(50, 100, 12, "aaaa inter-", "national bb").ToArray());
        Assert.Equal("aaaa inter-national bb", Paragraph(detected).Text);
    }

    [Fact]
    public void Break_Before_A_Word_That_Fitted_Is_Kept()
    {
        var detected = Single(Lines(50, 100, 12, "aaa", "bb cccccccc").ToArray());
        Assert.Equal("aaa" + Break + "bb cccccccc", Paragraph(detected).Text);
    }

    [Fact]
    public void Break_After_A_Colon_Is_Kept()
    {
        var detected = Single(Lines(50, 100, 12, "Delivery address:", "Rotterdam Europoort").ToArray());
        Assert.Equal("Delivery address:" + Break + "Rotterdam Europoort", Paragraph(detected).Text);
    }

    [Fact]
    public void First_Line_Indent_Is_Kept_As_An_Indent()
    {
        var detected = Single(Obj("aaa bbb", 60, 100), Obj("ccc ddd ee", 50, 112), Obj("fff", 50, 124));

        var paragraph = Paragraph(detected);
        Assert.Equal(10, paragraph.FirstLineIndentPt, 2);
        Assert.Equal(0, paragraph.LeftIndentPt, 2);
        Assert.Equal(50 - Pad, detected.Block!.XPt, 2);
        var layout = TextBlockLayout.Compute(detected.Block, _fonts);
        Assert.Equal(60, detected.Block.XPt + layout.Lines[0].Pieces[0].XPt, 2);
    }

    [Fact]
    public void Hanging_Indent_Is_Kept_As_An_Indent()
    {
        var detected = Single(Obj("1. aaa bbb", 50, 100), Obj("ccc ddd", 65, 112), Obj("eee", 65, 124));

        var paragraph = Paragraph(detected);
        Assert.Equal(-15, paragraph.FirstLineIndentPt, 2);
        Assert.Equal(15, paragraph.LeftIndentPt, 2);
        var layout = TextBlockLayout.Compute(detected.Block!, _fonts);
        Assert.Equal(65, detected.Block!.XPt + layout.Lines[1].Pieces[0].XPt, 2);
    }

    [Fact]
    public void Bold_And_Italic_Come_From_The_Font_Name()
    {
        var detected = Single(Obj("aaa", 50, 100, font: "Mono500-BoldItalic"));
        var style = Paragraph(detected).Runs[0].Style;
        Assert.True(style.Bold);
        Assert.True(style.Italic);
        Assert.Equal("Mono500", style.FontFamily);
        Assert.Empty(detected.SubstitutedFonts);
    }

    [Fact]
    public void Missing_Font_Is_Substituted_And_Reported()
    {
        var detected = Single(Obj("aaa", 50, 100, font: "ABCDEF+Calibri"));

        var (document, written) = Assert.Single(detected.SubstitutedFonts);
        Assert.Equal("Calibri", document);
        Assert.Equal(written, Paragraph(detected).Runs[0].Style.FontFamily);
        Assert.False(string.IsNullOrEmpty(written));
    }

    [Fact]
    public void Runs_Keep_Size_And_Colour()
    {
        var detected = Single(Obj("aaa ", 50, 100), Obj("bbb", 70, 100, size: 12, color: 0xFFD3282F));
        var runs = Paragraph(detected).Runs;
        Assert.Equal(2, runs.Count);
        Assert.Equal((10.0, 0xFF000000u), (runs[0].Style.FontSizePt, runs[0].Style.ColorArgb));
        Assert.Equal((12.0, 0xFFD3282Fu), (runs[1].Style.FontSizePt, runs[1].Style.ColorArgb));
    }

    [Fact]
    public void Underline_Line_Becomes_Underline_Of_The_Text_Above()
    {
        var underline = new PageLineObject([1], new OverlayBox(70, 101.5, 15, 0.6), 0xFF000000);

        var detected = Assert.Single(Build([Obj("aaa bbb", 50, 100)], [underline]));

        var runs = Paragraph(detected).Runs;
        Assert.Equal(["aaa ", "bbb"], runs.Select(r => r.Text));
        Assert.False(runs[0].Style.Underline);
        Assert.True(runs[1].Style.Underline);
        Assert.Same(underline, Assert.Single(detected.Decorations));
    }

    [Fact]
    public void Underline_Inside_A_Form_Is_Not_Taken()
    {
        // Объекты внутри форм запись не снимает (форма общая): такая линия
        // остаётся на странице и стилем блока не становится.
        var nested = new PageLineObject([1, 0], new OverlayBox(70, 101.5, 15, 0.6), 0xFF000000);
        var detected = Assert.Single(Build([Obj("aaa bbb", 50, 100)], [nested]));
        Assert.Empty(detected.Decorations);
        Assert.All(Paragraph(detected).Runs, r => Assert.False(r.Style.Underline));
    }

    [Fact]
    public void Strike_Line_Becomes_Strike()
    {
        var strike = new PageLineObject([1], new OverlayBox(50, 97, 35, 0.6), 0xFF000000);
        var detected = Assert.Single(Build([Obj("aaa bbb", 50, 100)], [strike]));
        Assert.True(Paragraph(detected).Runs[0].Style.Strike);
    }

    [Fact]
    public void Table_Border_Is_Not_Taken()
    {
        var border = new PageLineObject([1], new OverlayBox(50, 111.7, 40, 0.6), 0xFF000000);
        var rulings = new List<PageRuling>
        {
            new(true, 100, 50, 90), new(true, 112, 50, 90), new(false, 50, 100, 112), new(false, 90, 100, 112),
        };

        var detected = Assert.Single(Build([Obj("aaa", 52, 110)], [border], rulings));

        Assert.Empty(detected.Decorations);
        Assert.False(Paragraph(detected).Runs[0].Style.Underline);
    }

    [Fact]
    public void Single_Line_Grows_To_The_Right()
    {
        var block = Single(Obj("Rotterdam", 50, 100)).Block!;
        Assert.Equal(TextBlockWidthMode.AutoWidth, block.WidthMode);
        Assert.True(block.AutoHeight);
        var layout = TextBlockLayout.Compute(block, _fonts);
        Assert.Equal(50, block.XPt + layout.Lines[0].Pieces[0].XPt, 2);
        Assert.Equal(100, block.YPt + layout.Lines[0].BaselinePt, 2);
    }

    [Fact]
    public void Text_In_A_Cell_Takes_The_Cell_Width_And_Shrinks()
    {
        var rulings = new List<PageRuling>
        {
            new(true, 100, 50, 150), new(true, 120, 50, 150), new(false, 50, 100, 120), new(false, 150, 100, 120),
        };

        var block = Assert.Single(Build([Obj("aaa", 52, 113)], rulings: rulings)).Block!;

        Assert.Equal((51.0, 101.0, 98.0, 18.0), (block.XPt, block.YPt, block.WidthPt, block.HeightPt));
        Assert.Equal(TextBlockWidthMode.Fixed, block.WidthMode);
        Assert.False(block.AutoHeight);
        Assert.True(block.ShrinkToFit);
        Assert.Equal(TextVerticalAlign.Center, block.VerticalAlign);
    }

    [Fact]
    public void Rotated_Paragraph_Keeps_Its_Angle_And_Place()
    {
        // Повёрнут на 90° по часовой: строки идут вниз, следующая — левее.
        var detected = Single(Obj("aaa bbb", 100, 50, angle: 90), Obj("ccc", 88, 50, angle: 90));
        var block = detected.Block!;

        Assert.Equal(90, block.RotationDegrees, 3);
        Assert.Equal("aaa bbb ccc", Paragraph(detected).Text);
        var layout = TextBlockLayout.Compute(block, _fonts);
        var (x, y) = ToDisplay(block, layout.Lines[0].Pieces[0].XPt, layout.Lines[0].BaselinePt);
        Assert.Equal(100, x, 2);
        Assert.Equal(50, y, 2);
        var (x2, _) = ToDisplay(block, layout.Lines[1].Pieces[0].XPt, layout.Lines[1].BaselinePt);
        Assert.Equal(88, x2, 2);
    }

    [Theory]
    [InlineData("form", "TextParagraphInForm")]
    [InlineData("type3", "TextParagraphType3")]
    [InlineData("invisible", "TextParagraphInvisible")]
    [InlineData("arabic", "TextBlockUnsupportedScript")]
    public void Paragraph_That_Cannot_Be_Taken_Says_Why(string kind, string key)
    {
        var obj = kind switch
        {
            "form" => Obj("aaa", 50, 100, inForm: true),
            "type3" => Obj("aaa", 50, 100, type3: true),
            "invisible" => Obj("aaa", 50, 100, invisible: true),
            _ => Obj("مرحبا", 50, 100),
        };

        var detected = Single(obj);

        Assert.Equal(key, detected.RefusedKey);
        Assert.Null(detected.Block);
    }

    [Fact]
    public void Overprinted_Text_Is_Refused()
    {
        var detected = Single(Obj("aaa", 50, 100), Obj("aaa", 50.4, 100));
        Assert.Equal("TextParagraphOverlap", detected.RefusedKey);
    }

    /// <summary>Точка в осях блока → отображаемые пункты (поворот вокруг центра рамки по часовой).</summary>
    private static (double X, double Y) ToDisplay(TextBlockOverlay block, double lx, double ly)
    {
        var a = block.RotationDegrees * Math.PI / 180;
        var (dx, dy) = (lx - block.WidthPt / 2, ly - block.HeightPt / 2);
        return (block.XPt + block.WidthPt / 2 + dx * Math.Cos(a) - dy * Math.Sin(a),
                block.YPt + block.HeightPt / 2 + dx * Math.Sin(a) + dy * Math.Cos(a));
    }
}
