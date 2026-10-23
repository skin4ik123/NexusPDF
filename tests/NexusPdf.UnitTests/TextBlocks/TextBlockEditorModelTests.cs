using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Логика редактора блока без экрана: курсор, выделение, стили, отмена,
/// буфер. Шрифт Mono500 — каждая буква 5 пт при кегле 10, поэтому
/// координаты курсора проверяются точно.
/// </summary>
public sealed class TextBlockEditorModelTests : IDisposable
{
    private const double Pad = TextBlockLayout.PaddingPt;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "editor-" + Guid.NewGuid().ToString("N"));
    private readonly FontCatalog _fonts;
    private static readonly TextStyle Mono = new("Mono500", 10);

    public TextBlockEditorModelTests()
    {
        Directory.CreateDirectory(_dir);
        var mono = Enumerable.Range(0x20, 0x5F).Concat(Enumerable.Range(0x400, 0x60)).ToDictionary(c => c, _ => (ushort)500);
        File.WriteAllBytes(Path.Combine(_dir, "mono500.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Mono500", Advances: mono)));
        _fonts = FontCatalog.Scan([_dir]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private TextBlockEditorModel Editor(string text = "", double width = 100,
        TextBlockWidthMode mode = TextBlockWidthMode.Fixed)
    {
        var paragraphs = text.Split('\n').Select(p => new TextParagraph([new TextRun(p, Mono)])).ToList();
        var block = new TextBlockOverlay(0, 0, width, 20, 0, mode, true, false, paragraphs);
        return new TextBlockEditorModel(block, _fonts, Mono);
    }

    private static string Text(TextBlockEditorModel e) =>
        string.Join("\n", e.Block.Paragraphs.Select(p => p.Text));

    [Fact]
    public void Typing_Inserts_With_Pending_Style()
    {
        var e = Editor();
        e.ApplyStyle(s => s with { Bold = true });
        Assert.True(e.Insert("ab").Accepted);

        var run = Assert.Single(e.Block.Paragraphs[0].Runs);
        Assert.Equal("ab", run.Text);
        Assert.True(run.Style.Bold);
        Assert.Equal(new TextPosition(0, 2), e.Caret);
    }

    [Fact]
    public void Style_Applies_Only_To_Selection_And_Merges_Runs()
    {
        var e = Editor("abcdef");
        e.MoveCaret(CaretMove.BlockStart, false);
        e.MoveCaret(CaretMove.Right, false);
        e.MoveCaret(CaretMove.Right, true);
        e.MoveCaret(CaretMove.Right, true);       // выделено "bc"
        e.ApplyStyle(s => s with { ColorArgb = 0xFFD3282F });

        Assert.Equal(["a", "bc", "def"], e.Block.Paragraphs[0].Runs.Select(r => r.Text));
        Assert.Equal(0xFFD3282Fu, e.Block.Paragraphs[0].Runs[1].Style.ColorArgb);

        e.SelectAll();
        e.ApplyStyle(s => s with { ColorArgb = 0xFF000000 });
        Assert.Equal("abcdef", Assert.Single(e.Block.Paragraphs[0].Runs).Text);
    }

    [Fact]
    public void Backspace_And_Delete_Join_Paragraphs()
    {
        var e = Editor("ab\ncd");
        e.PlaceCaretAt(new TextPosition(1, 0));
        e.Backspace(word: false);
        Assert.Equal("abcd", Text(e));
        Assert.Equal(new TextPosition(0, 2), e.Caret);

        e.NewParagraph();
        e.MoveCaret(CaretMove.Left, false);
        e.Delete(word: false);
        Assert.Equal("abcd", Text(e));
    }

    [Fact]
    public void Word_Delete_Removes_Whole_Word()
    {
        var e = Editor("one two three");
        e.MoveCaret(CaretMove.BlockEnd, false);
        e.Backspace(word: true);
        Assert.Equal("one two ", Text(e));
        e.MoveCaret(CaretMove.BlockStart, false);
        e.Delete(word: true);
        Assert.Equal("two ", Text(e));
    }

    [Fact]
    public void Caret_Moves_By_Words_Lines_And_Visual_Rows()
    {
        // Ширина 30 пт = 6 букв: "aaa bbb ccc" → строки "aaa ", "bbb ", "ccc".
        var e = Editor("aaa bbb ccc", width: 2 * Pad + 30);
        e.PlaceCaretAt(new TextPosition(0, 1));        // a|aa
        e.MoveCaret(CaretMove.Down, false);
        Assert.Equal(new TextPosition(0, 5), e.Caret); // b|bb — та же колонка
        e.MoveCaret(CaretMove.Down, false);
        Assert.Equal(new TextPosition(0, 9), e.Caret);
        e.MoveCaret(CaretMove.LineStart, false);
        Assert.Equal(new TextPosition(0, 8), e.Caret);
        e.MoveCaret(CaretMove.WordLeft, false);
        Assert.Equal(new TextPosition(0, 4), e.Caret);
        e.MoveCaret(CaretMove.WordRight, false);
        Assert.Equal(new TextPosition(0, 8), e.Caret);
        e.MoveCaret(CaretMove.LineEnd, false);
        Assert.Equal(new TextPosition(0, 11), e.Caret);
    }

    [Fact]
    public void PlaceCaret_Hits_Nearest_Character_Boundary()
    {
        var e = Editor("abcd");
        e.PlaceCaret(Pad + 7.4, 5, extend: false);   // граница после «a» (5 пт) ближе, чем после «b» (10 пт)
        Assert.Equal(new TextPosition(0, 1), e.Caret);
        e.PlaceCaret(Pad + 7.6, 5, extend: false);
        Assert.Equal(new TextPosition(0, 2), e.Caret);
        e.PlaceCaret(Pad + 100, 5, extend: true);
        Assert.Equal(new TextPosition(0, 4), e.Caret);
        Assert.Equal(new TextPosition(0, 2), e.Anchor);
        Assert.True(e.HasSelection);

        var caret = e.CaretRect();
        Assert.Equal(Pad + 20, caret.X, 6);
        var selection = Assert.Single(e.SelectionRects());
        Assert.Equal(Pad + 10, selection.XPt, 6);
        Assert.Equal(10, selection.WidthPt, 6);
    }

    [Fact]
    public void Unsupported_Script_Is_Refused_Whole()
    {
        var e = Editor("x");
        e.MoveCaret(CaretMove.BlockEnd, false);
        var result = e.Insert("ok שלום");

        Assert.False(result.Accepted);
        Assert.Equal("TextBlockUnsupportedScript", result.RejectedReasonKey);
        Assert.Equal("x", Text(e));
    }

    [Fact]
    public void Character_No_Font_Can_Draw_Is_Refused()
    {
        // В файл не уходят «квадраты»: букву, которой нет ни в одном шрифте,
        // программа не принимает и объясняет почему.
        var e = Editor("x");
        e.MoveCaret(CaretMove.BlockEnd, false);
        var result = e.Insert("a\u4E2D");

        Assert.False(result.Accepted);
        Assert.Equal("TextBlockNoFontForChar", result.RejectedReasonKey);
        Assert.Equal("x", Text(e));
        Assert.True(e.Insert("ab").Accepted);
    }

    [Fact]
    public void Undo_Merges_Typing_And_Redo_Restores()
    {
        var e = Editor();
        foreach (var c in "hello")
            e.Insert(c.ToString());
        e.Insert(" ");
        e.ApplyStyle(s => s with { Bold = true });
        e.Insert("x");

        Assert.True(e.Undo());   // "x" вместе со сменой стиля — отдельный шаг
        Assert.Equal("hello ", Text(e));
        Assert.True(e.Undo());   // весь набор подряд — один шаг
        Assert.Equal("", Text(e));
        Assert.False(e.Undo());
        Assert.True(e.Redo());
        Assert.Equal("hello ", Text(e));
    }

    [Fact]
    public void Rich_Copy_Paste_Keeps_Styles_Plain_Paste_Takes_Caret_Style()
    {
        var source = Editor("ab");
        source.SelectAll();
        source.ApplyStyle(s => s with { Italic = true });
        var rich = source.CopyRich();
        Assert.Equal("ab", source.CopyPlainText());

        var target = Editor("xy");
        target.MoveCaret(CaretMove.BlockEnd, false);
        Assert.True(target.Paste(rich, "ab").Accepted);
        Assert.Equal("xyab", Text(target));
        Assert.True(target.Block.Paragraphs[0].Runs[^1].Style.Italic);

        Assert.True(target.Paste(null, "zz").Accepted);
        Assert.Equal("xyabzz", Text(target));
        Assert.True(target.Block.Paragraphs[0].Runs[^1].Style.Italic);   // стиль места вставки
    }

    [Fact]
    public void Rich_Paste_Clamps_Styles_From_The_Clipboard()
    {
        // Буфер обмена заполняет кто угодно: размер 10^16 повесил бы вёрстку,
        // а нулевой — испортил бы блок при следующем открытии.
        var source = Editor("ab");
        source.SelectAll();
        var rich = source.CopyRich();
        Assert.Contains("\"FontSizePt\":10", rich);
        rich = rich.Replace("\"FontSizePt\":10", "\"FontSizePt\":1E16").Replace("\"LetterSpacingPercent\":0", "\"LetterSpacingPercent\":9999");

        var target = Editor();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(target.Paste(rich, "ab").Accepted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        var style = target.Block.Paragraphs[0].Runs[^1].Style;
        Assert.InRange(style.FontSizePt, 1, 1638);
        Assert.InRange(style.LetterSpacingPercent, -50, 500);
    }

    [Fact]
    public void Paste_From_Spreadsheet()
    {
        var e = Editor();
        Assert.True(e.Paste(null, "a\tb\r\nc\r\n").Accepted);
        Assert.Equal("a b\nc\n", Text(e));

        var big = Editor();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(big.Paste(null, new string('x', 100_000)).Accepted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"вставка заняла {watch.Elapsed}");
        Assert.Equal(100_000, big.Block.Paragraphs[0].Text.Length);
    }

    [Fact]
    public void Composition_Is_Not_Part_Of_The_Block_Until_Committed()
    {
        var e = Editor("a");
        e.MoveCaret(CaretMove.BlockEnd, false);
        e.SetComposition("d");
        Assert.Equal("d", e.Composition);
        Assert.Equal("a", Text(e));

        e.CommitComposition("д");
        Assert.Equal("", e.Composition);
        Assert.Equal("aд", Text(e));
    }

    [Fact]
    public void New_Paragraph_And_Line_Break()
    {
        var e = Editor("ab");
        e.PlaceCaretAt(new TextPosition(0, 1));
        e.LineBreak();
        e.NewParagraph();
        Assert.Equal(2, e.Block.Paragraphs.Count);
        Assert.Equal("a" + TextBlockText.LineBreak, e.Block.Paragraphs[0].Text);
        Assert.Equal("b", e.Block.Paragraphs[1].Text);
        Assert.Equal(new TextPosition(1, 0), e.Caret);
    }

    [Fact]
    public void Alignment_And_Spacing_Apply_To_Paragraphs_Of_The_Selection()
    {
        var e = Editor("a\nb\nc");
        e.PlaceCaretAt(new TextPosition(0, 0));
        e.MoveCaret(CaretMove.Down, true);
        e.SetAlign(TextAlign.Right);
        e.SetLineSpacing(2);

        Assert.Equal([TextAlign.Right, TextAlign.Right, TextAlign.Left], e.Block.Paragraphs.Select(p => p.Align));
        Assert.Equal(2, e.Block.Paragraphs[1].LineSpacing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(30)]
    public void Growing_Rotated_Block_Keeps_Its_Top_Left_Corner(double rotation)
    {
        // Поворот — вокруг центра рамки; растущая при наборе рамка сдвигала бы
        // центр, и повёрнутый текст уезжал бы из-под курсора.
        var block = new TextBlockOverlay(100, 100, 5, 5, rotation, TextBlockWidthMode.AutoWidth, true, false,
            [new TextParagraph([new TextRun("", Mono)])]);
        var e = new TextBlockEditorModel(block, _fonts, Mono);
        var before = Corner(e.Block);

        e.Insert("abcdefgh");
        e.NewParagraph();
        e.Insert("ij");

        var after = Corner(e.Block);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    private static (double X, double Y) Corner(TextBlockOverlay b)
    {
        var a = b.RotationDegrees * Math.PI / 180;
        var (dx, dy) = (-b.WidthPt / 2, -b.HeightPt / 2);
        return (b.XPt + b.WidthPt / 2 + dx * Math.Cos(a) - dy * Math.Sin(a),
                b.YPt + b.HeightPt / 2 + dx * Math.Sin(a) + dy * Math.Cos(a));
    }

    [Fact]
    public void Auto_Width_Block_Reports_Layout_Size()
    {
        var e = Editor("", width: 5, mode: TextBlockWidthMode.AutoWidth);
        e.Insert("abcd");
        Assert.Equal(2 * Pad + 20, e.Block.WidthPt, 6);
        Assert.False(e.IsEmpty);
    }

    [Fact]
    public void SetIndents_Applies_To_Selected_Paragraphs()
    {
        var e = Editor("aa\nbb\ncc");
        e.PlaceCaretAt(new TextPosition(0, 1));
        e.PlaceCaretAt(new TextPosition(1, 1), extend: true);

        e.SetIndents(15, 5);

        Assert.Equal((15.0, 5.0), (e.Block.Paragraphs[0].FirstLineIndentPt, e.Block.Paragraphs[0].LeftIndentPt));
        Assert.Equal((15.0, 5.0), (e.Block.Paragraphs[1].FirstLineIndentPt, e.Block.Paragraphs[1].LeftIndentPt));
        Assert.Equal((0.0, 0.0), (e.Block.Paragraphs[2].FirstLineIndentPt, e.Block.Paragraphs[2].LeftIndentPt));
        Assert.True(e.Undo());
        Assert.Equal(0.0, e.Block.Paragraphs[0].FirstLineIndentPt);
    }

    [Fact]
    public void New_Paragraph_Keeps_The_Indents()
    {
        var e = Editor("ab");
        e.SetIndents(10, 4);
        e.MoveCaret(CaretMove.BlockEnd, false);
        e.NewParagraph();
        Assert.Equal((10.0, 4.0), (e.Block.Paragraphs[1].FirstLineIndentPt, e.Block.Paragraphs[1].LeftIndentPt));
    }

    [Fact]
    public void Caret_On_The_First_Line_Includes_The_Indent()
    {
        var e = Editor("ab");
        e.SetIndents(10, 0);
        e.MoveCaret(CaretMove.BlockStart, false);
        Assert.Equal(Pad + 10, e.CaretRect().X, 6);

        var empty = Editor("");
        empty.SetIndents(10, 0);
        Assert.Equal(Pad + 10, empty.CaretRect().X, 6);
    }

    [Fact]
    public void SetVerticalAlign_Moves_The_Lines_Of_A_Fixed_Frame()
    {
        var e = Editor("a");
        e.SetFrame(0, 0, 100, 2 * Pad + 40, TextBlockWidthMode.Fixed, autoHeight: false, shrinkToFit: false, 0);
        var top = e.CaretRect().Top;

        e.SetVerticalAlign(TextVerticalAlign.Bottom);

        Assert.Equal(TextVerticalAlign.Bottom, e.Block.VerticalAlign);
        Assert.True(e.CaretRect().Top > top + 20);
    }
}
