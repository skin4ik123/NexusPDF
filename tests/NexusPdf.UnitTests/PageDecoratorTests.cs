using NexusPdf.Application;

namespace NexusPdf.UnitTests;

public sealed class PageDecoratorTests
{
    [Fact]
    public void Template_Expands_All_Placeholders()
    {
        var result = PageDecorator.ExpandTemplate(
            "{file} — стр. {n} из {N} ({date})", 3, 10, "отчёт.pdf", new DateTime(2026, 8, 14));
        Assert.Contains("отчёт.pdf", result);
        Assert.Contains("стр. 3 из 10", result);
        Assert.DoesNotContain("{", result);
    }

    [Fact]
    public void Template_Without_Placeholders_Is_Untouched()
    {
        Assert.Equal("Конфиденциально",
            PageDecorator.ExpandTemplate("Конфиденциально", 1, 2, null, DateTime.Now));
    }

    [Fact]
    public void Approximate_Width_Grows_With_Text_And_Size()
    {
        var narrow = PageDecorator.ApproximateWidthPt("ab", 10);
        var wide = PageDecorator.ApproximateWidthPt("abcdef", 10);
        var big = PageDecorator.ApproximateWidthPt("ab", 20);
        Assert.True(wide > narrow);
        Assert.Equal(narrow * 2, big, 3);
    }
}
