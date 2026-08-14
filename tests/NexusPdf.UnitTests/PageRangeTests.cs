using NexusPdf.Domain;

namespace NexusPdf.UnitTests;

public sealed class PageRangeTests
{
    [Fact]
    public void Parses_Single_Pages_And_Ranges_In_Listed_Order()
    {
        var result = PageRange.Parse("3,1-2,5", 10);
        Assert.Equal(new[] { 2, 0, 1, 4 }, result);
    }

    [Fact]
    public void Parses_Descending_Range()
    {
        var result = PageRange.Parse("4-2", 10);
        Assert.Equal(new[] { 3, 2, 1 }, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("2-99")]
    public void Rejects_Invalid_Input(string text)
    {
        Assert.False(PageRange.TryParse(text, 10, out _, out var error) && error == null,
            $"Ввод «{text}» не должен приниматься.");
    }

    [Fact]
    public void Error_Message_Mentions_Document_Bounds()
    {
        Assert.False(PageRange.TryParse("15", 10, out _, out var error));
        Assert.Contains("1–10", error);
    }
}
