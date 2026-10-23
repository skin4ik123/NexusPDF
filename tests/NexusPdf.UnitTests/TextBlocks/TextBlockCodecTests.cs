using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Скрытое описание блока в файле. В нём нет самого текста — иначе при
/// вымарывании стёртое оставалось бы в метке; текст собирается из
/// записанных кусков, и любое несовпадение — «блок чужой, не восстанавливать».
/// </summary>
public sealed class TextBlockCodecTests
{
    private static readonly TextStyle Plain = new("Liberation Sans", 11);
    private static readonly TextStyle Bold = Plain with { Bold = true, ColorArgb = 0xFFD3282F };

    private static TextBlockOverlay Sample() => new(
        10, 20, 200, 40, 15, TextBlockWidthMode.Fixed, AutoHeight: true, ShrinkToFit: false,
        [
            new TextParagraph([new TextRun("SECRET ", Plain), new TextRun("42", Bold)], TextAlign.Center, 1.5),
            new TextParagraph([new TextRun("second", Plain with { Baseline = BaselineShift.Superscript })]),
        ]);

    [Fact]
    public void Structure_Has_No_Text()
    {
        var json = TextBlockCodec.EncodeStructure(Sample());
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("second", json);
        Assert.Contains("\"V\":1", json);
    }

    [Fact]
    public void Decode_Restores_Block_From_Pieces()
    {
        var original = Sample();
        var id = Guid.NewGuid();
        // Куски режутся иначе, чем фрагменты стиля: так их режет перенос строк.
        var pieces = new[]
        {
            new TextPiece(0, 0, "SEC"), new TextPiece(0, 3, "RET 4"), new TextPiece(0, 8, "2"),
            new TextPiece(1, 0, "second"),
        };

        var decoded = TextBlockCodec.Decode(TextBlockCodec.EncodeStructure(original), pieces, id);

        Assert.NotNull(decoded);
        Assert.Equal(id, decoded!.BlockId);
        Assert.Equal((10.0, 20.0, 200.0, 40.0, 15.0), (decoded.XPt, decoded.YPt, decoded.WidthPt, decoded.HeightPt, decoded.RotationDegrees));
        Assert.Equal(TextBlockWidthMode.Fixed, decoded.WidthMode);
        Assert.True(decoded.AutoHeight);
        Assert.Equal(2, decoded.Paragraphs.Count);
        Assert.Equal("SECRET 42", decoded.Paragraphs[0].Text);
        Assert.Equal(TextAlign.Center, decoded.Paragraphs[0].Align);
        Assert.Equal(1.5, decoded.Paragraphs[0].LineSpacing);
        Assert.Equal(["SECRET ", "42"], decoded.Paragraphs[0].Runs.Select(r => r.Text));
        Assert.Equal(Bold, decoded.Paragraphs[0].Runs[1].Style);
        Assert.Equal(BaselineShift.Superscript, decoded.Paragraphs[1].Runs[0].Style.Baseline);
    }

    [Fact]
    public void Empty_Paragraph_Needs_No_Pieces()
    {
        var block = Sample() with
        {
            Paragraphs = [new TextParagraph([new TextRun("", Plain)]), new TextParagraph([new TextRun("x", Plain)])],
        };
        var decoded = TextBlockCodec.Decode(TextBlockCodec.EncodeStructure(block), [new TextPiece(1, 0, "x")], Guid.NewGuid());
        Assert.NotNull(decoded);
        Assert.Equal("", decoded!.Paragraphs[0].Text);
    }

    [Fact]
    public void Line_Breaks_Live_In_The_Structure_Not_In_The_Pieces()
    {
        // Перевод строки внутри абзаца в PDF не пишется (у шрифта нет такого
        // глифа) — его место хранит описание блока.
        var block = Sample() with
        {
            Paragraphs = [new TextParagraph([new TextRun("ab" + TextBlockText.LineBreak + "cd", Plain)])],
        };
        var json = TextBlockCodec.EncodeStructure(block);
        var decoded = TextBlockCodec.Decode(json, [new TextPiece(0, 0, "ab"), new TextPiece(0, 3, "cd")], Guid.NewGuid());

        Assert.NotNull(decoded);
        Assert.Equal("ab" + TextBlockText.LineBreak + "cd", decoded!.Paragraphs[0].Text);
        Assert.Null(TextBlockCodec.Decode(json, [new TextPiece(0, 0, "abcd")], Guid.NewGuid()));
    }

    public static TheoryData<TextPiece[]> BadPieces() => new()
    {
        new[] { new TextPiece(0, 0, "SECRET"), new TextPiece(1, 0, "second") },                    // дыра: нет " 42"
        new[] { new TextPiece(0, 0, "SECRET 42"), new TextPiece(0, 5, "T 42"), new TextPiece(1, 0, "second") }, // перекрытие
        new[] { new TextPiece(0, 0, "SECRET 42!"), new TextPiece(1, 0, "second") },                // длиннее
        new[] { new TextPiece(0, 0, "SECRET 42"), new TextPiece(1, 0, "second"), new TextPiece(2, 0, "x") }, // лишний абзац
        new[] { new TextPiece(0, 0, "SECRET 42"), new TextPiece(1, -1, "second") },
    };

    [Theory]
    [MemberData(nameof(BadPieces))]
    public void Decode_Rejects_Gaps_Overlaps_And_Length_Mismatch(TextPiece[] pieces)
    {
        Assert.Null(TextBlockCodec.Decode(TextBlockCodec.EncodeStructure(Sample()), pieces, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"V\":99}")]
    [InlineData("{\"V\":1,\"X\":0,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Fixed\",\"AH\":true,\"SF\":false,\"P\":[{\"A\":\"Left\",\"LS\":1,\"Runs\":[{\"L\":-1,\"S\":{\"F\":\"A\",\"Sz\":10}}]}]}")]
    [InlineData("{\"V\":1,\"X\":0,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Fixed\",\"AH\":true,\"SF\":false,\"P\":[{\"A\":\"Left\",\"LS\":1,\"Runs\":[{\"L\":2147483647,\"S\":{\"F\":\"A\",\"Sz\":10}}]}]}")]
    [InlineData("{\"V\":1,\"X\":1e400,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Fixed\",\"AH\":true,\"SF\":false,\"P\":[]}")]
    [InlineData("{\"V\":1,\"X\":0,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Sideways\",\"AH\":true,\"SF\":false,\"P\":[]}")]
    public void Decode_Rejects_Malformed_Json(string json)
    {
        Assert.Null(TextBlockCodec.Decode(json, [new TextPiece(0, 0, "x")], Guid.NewGuid()));
    }

    [Fact]
    public void Decode_Rejects_Too_Many_Paragraphs()
    {
        var paragraphs = string.Join(",", Enumerable.Repeat(
            "{\"A\":\"Left\",\"LS\":1,\"Runs\":[{\"L\":0,\"S\":{\"F\":\"A\",\"Sz\":10}}]}", 100_000));
        var json = "{\"V\":1,\"X\":0,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Fixed\",\"AH\":true,\"SF\":false,\"P\":[" + paragraphs + "]}";
        Assert.Null(TextBlockCodec.Decode(json, [], Guid.NewGuid()));
    }

    [Fact]
    public void Round_Trip_Keeps_Vertical_Align_And_Indents()
    {
        var original = Sample() with { VerticalAlign = TextVerticalAlign.Bottom };
        original = original with
        {
            Paragraphs = [original.Paragraphs[0] with { FirstLineIndentPt = -12.5, LeftIndentPt = 20 }, original.Paragraphs[1]],
        };
        var pieces = new[] { new TextPiece(0, 0, "SECRET 42"), new TextPiece(1, 0, "second") };

        var decoded = TextBlockCodec.Decode(TextBlockCodec.EncodeStructure(original), pieces, Guid.NewGuid());

        Assert.NotNull(decoded);
        Assert.Equal(TextVerticalAlign.Bottom, decoded!.VerticalAlign);
        Assert.Equal((-12.5, 20.0), (decoded.Paragraphs[0].FirstLineIndentPt, decoded.Paragraphs[0].LeftIndentPt));
        Assert.Equal((0.0, 0.0), (decoded.Paragraphs[1].FirstLineIndentPt, decoded.Paragraphs[1].LeftIndentPt));
    }

    [Fact]
    public void Defaults_Are_Not_Written()
    {
        var json = TextBlockCodec.EncodeStructure(Sample());
        Assert.DoesNotContain("\"VA\"", json);
        Assert.DoesNotContain("\"FI\"", json);
        Assert.DoesNotContain("\"LI\"", json);
    }

    [Fact]
    public void Old_Mark_Without_New_Keys_Decodes_As_Before()
    {
        const string json = "{\"V\":1,\"X\":0,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Fixed\",\"AH\":true,\"SF\":false," +
                            "\"P\":[{\"A\":\"Left\",\"LS\":1,\"Runs\":[{\"L\":1,\"S\":{\"F\":\"A\",\"Sz\":10}}]}]}";

        var decoded = TextBlockCodec.Decode(json, [new TextPiece(0, 0, "x")], Guid.NewGuid());

        Assert.NotNull(decoded);
        Assert.Equal(TextVerticalAlign.Top, decoded!.VerticalAlign);
        Assert.Equal((0.0, 0.0), (decoded.Paragraphs[0].FirstLineIndentPt, decoded.Paragraphs[0].LeftIndentPt));
    }

    [Theory]
    [InlineData("\"VA\":\"Middle\",")]
    [InlineData("\"VA\":\"1\",")]
    public void Decode_Rejects_Unknown_Vertical_Align(string extra)
    {
        var json = "{\"V\":1,\"X\":0,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Fixed\",\"AH\":true,\"SF\":false," + extra +
                   "\"P\":[{\"A\":\"Left\",\"LS\":1,\"Runs\":[{\"L\":1,\"S\":{\"F\":\"A\",\"Sz\":10}}]}]}";
        Assert.Null(TextBlockCodec.Decode(json, [new TextPiece(0, 0, "x")], Guid.NewGuid()));
    }

    [Fact]
    public void Decode_Rejects_Indent_Out_Of_Range()
    {
        const string json = "{\"V\":1,\"X\":0,\"Y\":0,\"W\":10,\"H\":10,\"R\":0,\"WM\":\"Fixed\",\"AH\":true,\"SF\":false," +
                            "\"P\":[{\"A\":\"Left\",\"LS\":1,\"LI\":1001,\"Runs\":[{\"L\":1,\"S\":{\"F\":\"A\",\"Sz\":10}}]}]}";
        Assert.Null(TextBlockCodec.Decode(json, [new TextPiece(0, 0, "x")], Guid.NewGuid()));
    }
}
