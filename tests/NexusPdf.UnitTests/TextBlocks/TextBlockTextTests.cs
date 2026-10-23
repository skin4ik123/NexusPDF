using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Какой текст блок принимает. Письменности со сложной вёрсткой (справа
/// налево, со связками) без движка shaping вышли бы в файле неверно — их
/// честнее не принять, чем исказить.
/// </summary>
public sealed class TextBlockTextTests
{
    [Theory]
    [InlineData('Ж', true)]
    [InlineData('é', true)]
    [InlineData('Ω', true)]
    [InlineData('1', true)]
    [InlineData(0x1F600, true)]
    [InlineData(0x05D0, false)] // иврит
    [InlineData(0x0627, false)] // арабский
    [InlineData(0x0915, false)] // деванагари
    [InlineData(0x0E01, false)] // тайский
    [InlineData(0xFB50, false)] // арабские формы представления
    public void Supported_Scripts(int codePoint, bool supported)
    {
        Assert.Equal(supported, TextBlockText.IsSupported(codePoint));
    }

    [Fact]
    public void Normalize_Turns_Tabs_And_Line_Ends_Into_Spaces_And_Paragraphs()
    {
        Assert.Equal("a b\u2029c\u2029d\u2029e", TextBlockText.Normalize("a\tb\r\nc\rd\ne"));
        Assert.Equal("ab", TextBlockText.Normalize("a\u0007b"));
        Assert.Equal("a\u2028b", TextBlockText.Normalize("a\u2028b"));
    }

    [Fact]
    public void Normalize_Cleans_What_Word_And_Other_Programs_Put_In_The_Clipboard()
    {
        Assert.Equal("ab", TextBlockText.Normalize("\uFEFFab"));                                  // метка порядка байтов
        Assert.Equal("a" + TextBlockText.LineBreak + "b", TextBlockText.Normalize("a\vb"));        // разрыв строки Word
        Assert.Equal("ab", TextBlockText.Normalize("a\uD800b"));                                  // одинокая половина пары
        Assert.Equal("ab", TextBlockText.Normalize("a\uDC00b"));
        Assert.Equal("x\uD83D\uDE00", TextBlockText.Normalize("x\uD83D\uDE00"));                // целая пара остаётся
    }
}
