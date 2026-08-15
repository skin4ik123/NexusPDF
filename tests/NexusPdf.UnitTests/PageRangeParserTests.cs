using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Разбор диапазона — то место, где ошибка на единицу печатает не тот
/// документ. Наружу отдаются нуль-базные индексы, пользователь пишет
/// один-базные номера, и каждый тест проверяет именно этот стык.
/// </summary>
public sealed class PageRangeParserTests
{
    [Fact]
    public void Simple_Range_Is_Inclusive_On_Both_Ends()
    {
        var r = PageRangeParser.Parse("1-5", 10);
        Assert.True(r.IsValid);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, r.Indices);
        Assert.Equal("1-5", r.Normalized);
    }

    [Fact]
    public void Comma_List_Keeps_Order()
    {
        var r = PageRangeParser.Parse("1,3,7", 10);
        Assert.Equal(new[] { 0, 2, 6 }, r.Indices);
        Assert.Equal("1, 3, 7", r.Normalized);
    }

    [Fact]
    public void Mixed_List_And_Ranges()
    {
        var r = PageRangeParser.Parse("1-5,8,10-14", 20);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 7, 9, 10, 11, 12, 13 }, r.Indices);
        Assert.Equal("1-5, 8, 10-14", r.Normalized);
    }

    [Fact]
    public void Open_End_Runs_To_Last_Page()
    {
        var r = PageRangeParser.Parse("8-", 10);
        Assert.Equal(new[] { 7, 8, 9 }, r.Indices);
    }

    [Fact]
    public void Open_Start_Runs_From_First_Page()
    {
        var r = PageRangeParser.Parse("-3", 10);
        Assert.Equal(new[] { 0, 1, 2 }, r.Indices);
    }

    [Fact]
    public void Reverse_Range_Prints_Backwards()
    {
        // 10-1 — не ошибка ввода, а просьба печатать в обратном порядке.
        var r = PageRangeParser.Parse("5-1", 10);
        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, r.Indices);
    }

    [Fact]
    public void Repeated_Pages_Are_Kept()
    {
        var r = PageRangeParser.Parse("1,1,2,2", 5);
        Assert.Equal(new[] { 0, 0, 1, 1 }, r.Indices);
    }

    [Fact]
    public void Page_Labels_Win_Over_Physical_Numbers()
    {
        // Документ с римской нумерацией вступления: «iv» — четвёртый лист файла,
        // а «4» — метка страницы, которая физически идёт пятой.
        var labels = new[] { "i", "ii", "iii", "iv", "1", "2", "3", "4" };
        var r = PageRangeParser.Parse("iv", 8, labels);
        Assert.Equal(new[] { 3 }, r.Indices);

        var byLabel = PageRangeParser.Parse("4", 8, labels);
        Assert.Equal(new[] { 7 }, byLabel.Indices);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("-")]
    [InlineData("1-abc")]
    public void Invalid_Input_Fails_With_A_Message(string text)
    {
        var r = PageRangeParser.Parse(text, 10);
        Assert.False(r.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Out_Of_Document_Number_Is_Rejected_Not_Clamped()
    {
        // Молчаливое приведение 99 к последней странице напечатало бы не то,
        // что просил пользователь.
        var r = PageRangeParser.Parse("1-99", 10);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Normalize_Collapses_Runs_But_Keeps_Pairs_Readable()
    {
        Assert.Equal("1-4", PageRangeParser.Normalize(new[] { 0, 1, 2, 3 }));
        Assert.Equal("1, 2", PageRangeParser.Normalize(new[] { 0, 1 }));
        Assert.Equal("3", PageRangeParser.Normalize(new[] { 2 }));
        Assert.Equal("", PageRangeParser.Normalize(Array.Empty<int>()));
    }
}

/// <summary>Порядок применения фильтров: объём → чётность → повтор → разворот.</summary>
public sealed class PageSelectionTests
{
    [Fact]
    public void All_Pages_By_Default()
    {
        var r = new PageSelection().Resolve(4);
        Assert.Equal(new[] { 0, 1, 2, 3 }, r.Indices);
    }

    [Fact]
    public void Odd_And_Even_Count_By_Human_Numbers()
    {
        var odd = new PageSelection { Parity = PageParity.OddOnly }.Resolve(6);
        Assert.Equal(new[] { 0, 2, 4 }, odd.Indices); // страницы 1, 3, 5

        var even = new PageSelection { Parity = PageParity.EvenOnly }.Resolve(6);
        Assert.Equal(new[] { 1, 3, 5 }, even.Indices); // страницы 2, 4, 6
    }

    [Fact]
    public void Repeat_Duplicates_Each_Page_Before_Reversing()
    {
        var r = new PageSelection { RepeatEachPage = 2, ReverseOrder = true }.Resolve(3);
        // Сначала 1,1,2,2,3,3 — потом разворот целиком.
        Assert.Equal(new[] { 2, 2, 1, 1, 0, 0 }, r.Indices);
    }

    [Fact]
    public void Parity_Applies_To_The_Chosen_Range_Not_The_Whole_Document()
    {
        var r = new PageSelection
        {
            Scope = PageScope.Range,
            RangeText = "3-8",
            Parity = PageParity.EvenOnly,
        }.Resolve(20);
        Assert.Equal(new[] { 3, 5, 7 }, r.Indices); // страницы 4, 6, 8
    }

    [Fact]
    public void Empty_Result_After_Filter_Is_An_Honest_Failure()
    {
        var r = new PageSelection
        {
            Scope = PageScope.Range,
            RangeText = "2",
            Parity = PageParity.OddOnly,
        }.Resolve(10);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Current_Page_Outside_Document_Fails()
    {
        var r = new PageSelection { Scope = PageScope.CurrentPage, CurrentPageIndex = 99 }.Resolve(3);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Selected_Pages_Drop_Stale_Indices()
    {
        // Панель могла отдать индексы до удаления страниц — задание не должно падать.
        var r = new PageSelection
        {
            Scope = PageScope.Selected,
            ExplicitIndices = new[] { 0, 2, 99 },
        }.Resolve(3);
        Assert.Equal(new[] { 0, 2 }, r.Indices);
    }
}
