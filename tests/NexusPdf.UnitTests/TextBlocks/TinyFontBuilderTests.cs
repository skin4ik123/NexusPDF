using System.Buffers.Binary;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Синтетический шрифт — основа точных тестов вёрстки: ширины букв в нём
/// заданы, а не взяты из чужого файла. Если он собран неверно, все тесты
/// поверх него проверяют мусор.
/// </summary>
public sealed class TinyFontBuilderTests
{
    [Fact]
    public void Built_Font_Has_Valid_Table_Directory()
    {
        var font = TinyFontBuilder.Build(new TinyFontSpec("Tiny"));

        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(font));
        var tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        Assert.Equal(10, tables);
        for (var i = 0; i < tables; i++)
        {
            var record = font.AsSpan(12 + 16 * i);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            Assert.True(offset + length <= font.Length, $"таблица {i} выходит за файл");
        }
    }

    [Fact]
    public void Collection_Has_Header_And_Face_Offsets()
    {
        var a = TinyFontBuilder.Build(new TinyFontSpec("A"));
        var b = TinyFontBuilder.Build(new TinyFontSpec("B"));
        var collection = TinyFontBuilder.BuildCollection(a, b);

        Assert.Equal("ttcf"u8.ToArray(), collection[..4]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(8)));
        var second = BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(16));
        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan((int)second)));
    }

    [Fact]
    public void Liberation_Test_Fonts_Are_Copied_Next_To_The_Tests()
    {
        foreach (var file in new[]
                 {
                     "LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf",
                     "LiberationSans-BoldItalic.ttf", "LiberationMono-Regular.ttf", "LICENSE-Liberation.txt",
                 })
            Assert.True(File.Exists(Path.Combine(TestFontPaths.Directory, file)), file);
        Assert.EndsWith("LiberationSans-BoldItalic.ttf", TestFontPaths.Sans(bold: true, italic: true));
    }
}
