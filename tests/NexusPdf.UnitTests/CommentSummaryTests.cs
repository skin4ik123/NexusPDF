using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Раскладка сводки комментариев. Проверяется то, что видно на бумаге:
/// текст не уезжает за поле и не теряется при разбиении на страницы.
/// </summary>
public sealed class CommentSummaryTests
{
    private static readonly SizePt A4 = new(595.28, 841.89);

    private static SummaryComment Comment(int n, string text, string author = "Автор") =>
        new(n, n, "Заметка", author, "15.08.2026", text);

    [Fact]
    public void Empty_Document_Gets_An_Honest_Page()
    {
        var pages = CommentSummaryLayout.Build(
            Array.Empty<SummaryComment>(), A4, new CommentSummarySettings(), "документ.pdf");

        var page = Assert.Single(pages);
        Assert.Contains(page.Lines, l => l.Text.Contains("нет комментариев"));
    }

    [Fact]
    public void Title_Appears_Once_On_The_First_Page()
    {
        var comments = Enumerable.Range(1, 60).Select(i => Comment(i, $"Комментарий {i}")).ToList();
        var pages = CommentSummaryLayout.Build(comments, A4, new CommentSummarySettings(), "файл.pdf");

        Assert.True(pages.Count > 1, "шестьдесят комментариев не помещаются на одну страницу");
        Assert.Contains(pages[0].Lines, l => l.Text.Contains("файл.pdf"));
        // Заголовок не повторяется: место в сводке дороже.
        Assert.All(pages.Skip(1), p => Assert.DoesNotContain(p.Lines, l => l.Text.Contains("Комментарии:")));
    }

    [Fact]
    public void Nothing_Falls_Below_The_Bottom_Margin()
    {
        var comments = Enumerable.Range(1, 40)
            .Select(i => Comment(i, string.Join(" ", Enumerable.Repeat("слово", 20))))
            .ToList();
        var settings = new CommentSummarySettings();
        var pages = CommentSummaryLayout.Build(comments, A4, settings, "файл.pdf");

        var bottom = A4.HeightPt - settings.MarginsPt.BottomPt;
        foreach (var page in pages)
        foreach (var line in page.Lines)
        {
            Assert.True(line.YPt + line.FontSizePt <= bottom + 1,
                $"строка «{line.Text}» на {line.YPt:F1} вылезла за нижнее поле {bottom:F1}");
        }
    }

    [Fact]
    public void Every_Comment_Number_Appears_Somewhere()
    {
        var comments = Enumerable.Range(1, 25).Select(i => Comment(i, $"Текст {i}")).ToList();
        var pages = CommentSummaryLayout.Build(comments, A4, new CommentSummarySettings(), "файл.pdf");
        var all = string.Join("\n", pages.SelectMany(p => p.Lines).Select(l => l.Text));

        foreach (var comment in comments)
            Assert.Contains($"{comment.Number}. Стр. {comment.PageNumber}", all);
    }

    [Fact]
    public void Long_Text_Is_Wrapped_Not_Cut()
    {
        var text = string.Join(" ", Enumerable.Repeat("длинное", 50));
        var lines = CommentSummaryLayout.Wrap(text, 400, 10);

        Assert.True(lines.Count > 1, "длинный текст обязан переноситься");
        // Ни одного потерянного слова.
        Assert.Equal(50, string.Join(" ", lines).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void A_Word_Longer_Than_The_Line_Is_Broken()
    {
        // Иначе оно ушло бы за поле целиком.
        var lines = CommentSummaryLayout.Wrap(new string('щ', 300), 200, 10);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 40, $"строка длиной {l.Length} слишком длинная"));
    }

    [Fact]
    public void Line_Breaks_Inside_A_Comment_Are_Kept()
    {
        var lines = CommentSummaryLayout.Wrap("первая\nвторая\nтретья", 400, 10);
        Assert.Equal(3, lines.Count);
        Assert.Equal("первая", lines[0]);
        Assert.Equal("третья", lines[2]);
    }

    [Fact]
    public void Empty_Comment_Text_Is_Marked_Explicitly()
    {
        var lines = CommentSummaryLayout.Wrap("   ", 400, 10);
        Assert.Equal("(без текста)", Assert.Single(lines));
    }

    [Theory]
    [InlineData(1, "Заметка")]
    [InlineData(9, "Выделение")]
    [InlineData(15, "Рисунок")]
    [InlineData(20, "Виджет формы")]
    [InlineData(999, "Аннотация")]
    public void Subtypes_Get_Human_Names(int subtype, string expected)
    {
        Assert.Equal(expected, CommentSummaryLayout.DescribeSubtype(subtype));
    }
}
