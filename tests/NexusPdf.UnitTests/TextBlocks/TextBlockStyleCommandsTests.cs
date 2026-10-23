using System.Globalization;
using NexusPdf.Ux;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>Размер кнопками −/+, ввод размера и цвета — одинаково на панели и в правой колонке.</summary>
public sealed class TextBlockStyleCommandsTests
{
    [Theory]
    [InlineData(11, 1, 12)]
    [InlineData(72, 1, 84)]
    [InlineData(84, 1, 96)]
    [InlineData(10.5, -1, 10)]
    [InlineData(10.7, -1, 10.5)]
    [InlineData(10.7, 1, 11)]
    [InlineData(6, -1, 6)]
    [InlineData(96, -1, 84)]
    [InlineData(84, -1, 72)]
    public void Next_Size_Follows_The_Ladder(double current, int direction, double expected)
    {
        Assert.Equal(expected, TextBlockStyleCommands.NextSize(current, direction));
    }

    [Fact]
    public void Size_Is_Parsed_In_The_Current_Culture_And_Range()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.True(TextBlockStyleCommands.TryParseSize("11,5", out var ru));
            Assert.Equal(11.5, ru);
            Assert.True(TextBlockStyleCommands.TryParseSize(" 12 pt ", out var withUnit));
            Assert.Equal(12, withUnit);
            Assert.True(TextBlockStyleCommands.TryParseSize("9.5", out var dot));
            Assert.Equal(9.5, dot);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
        Assert.False(TextBlockStyleCommands.TryParseSize("0", out _));
        Assert.False(TextBlockStyleCommands.TryParseSize("1000", out _));
        Assert.False(TextBlockStyleCommands.TryParseSize("abc", out _));
        Assert.False(TextBlockStyleCommands.TryParseSize("NaN", out _));
    }

    [Theory]
    [InlineData("#1B1C20", 0xFF1B1C20u)]
    [InlineData("1b1c20", 0xFF1B1C20u)]
    [InlineData("#abc", 0xFFAABBCCu)]
    [InlineData("#80FF0000", 0x80FF0000u)]
    public void Colors_Are_Parsed_From_Hex(string text, uint expected)
    {
        Assert.True(TextBlockStyleCommands.TryParseColor(text, out var argb));
        Assert.Equal(expected, argb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void Bad_Colors_Are_Refused(string text)
    {
        Assert.False(TextBlockStyleCommands.TryParseColor(text, out _));
    }
}
