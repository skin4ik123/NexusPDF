using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Разбиение задания по формату бумаги. Windows задаёт формат одним
/// PrintTicket на задание, поэтому без разбиения принтер взял бы формат
/// первого листа и напечатал на нём всё остальное.
/// </summary>
public sealed class JobSplitterTests
{
    private static readonly SizePt A4 = new(595.28, 841.89);
    private static readonly SizePt A3 = new(841.89, 1190.55);

    private static SheetPlan Sheet(int index, SizePt paper, int sourcePage) => new()
    {
        SheetIndex = index,
        PaperSizePt = paper,
        PrintableAreaPt = RectPt.FromSize(paper),
        HardMarginsPt = MarginsPt.Zero,
        Pages = new[]
        {
            new PlacedPage
            {
                DocumentId = "doc",
                SourcePageIndex = sourcePage,
                Box = PageBoxKind.CropBox,
                SourceRectPt = RectPt.FromSize(paper),
                TargetRectPt = RectPt.FromSize(paper),
                ClipRectPt = RectPt.FromSize(paper),
                Scale = 1.0,
                RotationDegrees = 0,
            },
        },
    };

    private static PrintJobPlan Plan(params SizePt[] papers) => new()
    {
        JobName = "Смешанный",
        PrinterName = "Тест",
        Capabilities = new PrinterCapabilities { PrinterName = "Тест" },
        Sheets = papers.Select((p, i) => Sheet(i, p, i)).ToList(),
    };

    [Fact]
    public void Single_Paper_Size_Is_Not_Split()
    {
        var plan = Plan(A4, A4, A4);
        Assert.False(JobSplitter.NeedsSplit(plan));

        var part = Assert.Single(JobSplitter.SplitByPaperSize(plan));
        Assert.Equal(3, part.Plan.Sheets.Count);
    }

    [Fact]
    public void Mixed_Sizes_Produce_One_Part_Per_Size()
    {
        var parts = JobSplitter.SplitByPaperSize(Plan(A4, A3, A4, A3, A4));

        Assert.Equal(2, parts.Count);
        Assert.Equal(3, parts[0].Plan.Sheets.Count); // три A4
        Assert.Equal(2, parts[1].Plan.Sheets.Count); // два A3
    }

    [Fact]
    public void Parts_Follow_The_Order_Of_First_Appearance()
    {
        // Первым идёт A3, потому что он встретился раньше: так итоговая стопка
        // ближе всего к исходному порядку документа.
        var parts = JobSplitter.SplitByPaperSize(Plan(A3, A4, A3));
        Assert.Equal(A3, parts[0].Plan.Sheets[0].PaperSizePt);
        Assert.Equal(A4, parts[1].Plan.Sheets[0].PaperSizePt);
    }

    [Fact]
    public void No_Page_Is_Lost_Or_Duplicated()
    {
        var plan = Plan(A4, A3, A4, A3, A4, A3);
        var printed = JobSplitter.SplitByPaperSize(plan)
            .SelectMany(p => p.Plan.Sheets)
            .SelectMany(s => s.Pages)
            .Select(p => p.SourcePageIndex)
            .OrderBy(i => i)
            .ToList();

        Assert.Equal(Enumerable.Range(0, 6), printed);
    }

    [Fact]
    public void Sheets_Are_Renumbered_Inside_Each_Part()
    {
        var parts = JobSplitter.SplitByPaperSize(Plan(A4, A3, A4));

        foreach (var part in parts)
            Assert.Equal(Enumerable.Range(0, part.Plan.Sheets.Count),
                part.Plan.Sheets.Select(s => s.SheetIndex));
    }

    [Fact]
    public void Pair_Links_Are_Dropped_Because_They_Would_Point_Elsewhere()
    {
        // Ссылка на парный лист другой части указывала бы в никуда.
        var parts = JobSplitter.SplitByPaperSize(Plan(A4, A3, A4, A3));
        Assert.All(parts.SelectMany(p => p.Plan.Sheets), s => Assert.Null(s.PairedSheetIndex));
    }

    [Fact]
    public void Part_Names_Mention_The_Paper_Size()
    {
        var parts = JobSplitter.SplitByPaperSize(Plan(A4, A3));
        Assert.Contains("210×297", parts[0].Plan.JobName);
        Assert.Contains("297×420", parts[1].Plan.JobName);
    }

    [Fact]
    public void Split_Is_Reported_By_Preflight_As_A_Warning()
    {
        var issue = JobSplitter.DescribeSplit(Plan(A4, A3));
        Assert.NotNull(issue);
        Assert.Equal(PreflightLevel.Warning, issue!.Level);
        Assert.Contains("2", issue.Message);

        Assert.Null(JobSplitter.DescribeSplit(Plan(A4, A4)));
    }

    [Fact]
    public void Collation_Warning_Appears_When_There_Are_Copies()
    {
        // Сортировка разбитого задания работает по частям, а не по документу —
        // сказать об этом надо до печати, а не после.
        var plan = Plan(A4, A3) with { Copies = 3 };
        var issue = JobSplitter.DescribeSplit(plan);
        Assert.Contains("каждой части", issue!.Suggestion);
    }
}
