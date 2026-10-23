using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Чтение шрифта: от него зависят переносы (ширины букв), пометки в списке
/// шрифтов (кириллица, право встраивания) и запись в PDF. Ошибка в ширине —
/// это «на экране одно, в файле другое».
/// </summary>
public sealed class TrueTypeFontTests
{
    private static TrueTypeFont Liberation() => TrueTypeFont.Parse(File.ReadAllBytes(TestFontPaths.Sans()));

    [Fact]
    public void Reads_Liberation_Sans_Metrics()
    {
        var font = Liberation();

        Assert.Equal("Liberation Sans", font.FamilyName);
        Assert.Equal("Regular", font.SubfamilyName);
        Assert.Equal(2048, font.UnitsPerEm);
        Assert.Equal(1366, font.AdvanceWidth('A'));
        Assert.Equal(569, font.AdvanceWidth(' '));
        Assert.Equal(1891, font.AdvanceWidth('Ж'));
        Assert.Equal(1854, font.Ascender);
        Assert.Equal(-434, font.Descender);
        Assert.Equal(67, font.LineGap);
        Assert.Equal(-67, font.UnderlinePosition);
        Assert.Equal(150, font.UnderlineThickness);
        Assert.Equal(530, font.StrikeoutPosition);
        Assert.Equal(102, font.StrikeoutSize);
        Assert.True(font.IsEmbeddable);
        Assert.True(font.HasTrueTypeOutlines);
        Assert.False(font.IsBold);
        Assert.False(font.IsItalic);
    }

    [Fact]
    public void Reads_Bold_And_Italic_Flags()
    {
        var font = TrueTypeFont.Parse(File.ReadAllBytes(TestFontPaths.Sans(bold: true, italic: true)));
        Assert.True(font.IsBold);
        Assert.True(font.IsItalic);
        Assert.Equal("Bold Italic", font.SubfamilyName);
    }

    [Fact]
    public void Detects_Missing_Cyrillic()
    {
        var font = TrueTypeFont.Parse(TinyFontBuilder.Build(new TinyFontSpec("Latin")));

        Assert.True(font.HasGlyph('a'));
        Assert.False(font.HasGlyph(0x416));
        Assert.Equal(500, font.AdvanceWidth(0x416)); // ширина .notdef
    }

    [Fact]
    public void Reads_Supplementary_Code_Points_From_Format_12()
    {
        var font = TrueTypeFont.Parse(TinyFontBuilder.Build(new TinyFontSpec("Emoji",
            Advances: new Dictionary<int, ushort> { ['a'] = 400, [0x1F600] = 900 })));

        Assert.True(font.HasGlyph(0x1F600));
        Assert.Equal(900, font.AdvanceWidth(0x1F600));
        Assert.Equal(400, font.AdvanceWidth('a'));
    }

    [Theory]
    [InlineData((ushort)0x0002, false)]
    [InlineData((ushort)0x0200, false)]
    [InlineData((ushort)0x0204, false)]
    [InlineData((ushort)0x0004, true)]
    [InlineData((ushort)0x0008, true)]
    [InlineData((ushort)0x0000, true)]
    public void Restricted_And_Bitmap_Fonts_Are_Not_Embeddable(ushort fsType, bool embeddable)
    {
        var font = TrueTypeFont.Parse(TinyFontBuilder.Build(new TinyFontSpec("Rights", FsType: fsType)));
        Assert.Equal(embeddable, font.IsEmbeddable);
    }

    [Fact]
    public void Reads_Faces_Of_A_Collection()
    {
        var collection = TinyFontBuilder.BuildCollection(
            TinyFontBuilder.Build(new TinyFontSpec("A")),
            TinyFontBuilder.Build(new TinyFontSpec("B")));

        Assert.Equal(2, TrueTypeFont.FaceCount(collection));
        var second = TrueTypeFont.Parse(collection, 1);
        Assert.Equal("B", second.FamilyName);
        var extracted = TrueTypeFont.Parse(second.ExtractFace());
        Assert.Equal("B", extracted.FamilyName);
        Assert.Equal(1, TrueTypeFont.FaceCount(extracted.ExtractFace()));
    }

    public static TheoryData<byte[]> MalformedFonts()
    {
        var liberation = File.ReadAllBytes(TestFontPaths.Sans());
        var pastEnd = TinyFontBuilder.Build(new TinyFontSpec("Broken"));
        // Смещение первой таблицы — далеко за концом файла.
        pastEnd[12 + 8] = 0x7F;
        return new TheoryData<byte[]>
        {
            Array.Empty<byte>(),
            new byte[] { 0, 1, 0, 0, 9, 8, 7, 6, 5, 4, 3, 2 },
            liberation[..5000],
            pastEnd,
        };
    }

    [Theory]
    [MemberData(nameof(MalformedFonts))]
    public void Malformed_Data_Throws_FormatException(byte[] data)
    {
        Assert.Throws<FormatException>(() => TrueTypeFont.Parse(data));
    }

    [Fact]
    public void Face_Count_Of_Non_Font_Is_Zero()
    {
        Assert.Equal(0, TrueTypeFont.FaceCount("hello world"u8));
        Assert.Throws<FormatException>(() => TrueTypeFont.Parse(
            TinyFontBuilder.BuildCollection(TinyFontBuilder.Build(new TinyFontSpec("A"))), 3));
    }
}
