using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Разбиение задания по бумаге. Windows задаёт формат одним PrintTicket на
/// задание, поэтому без разбиения принтер взял бы формат первого листа и
/// напечатал на нём всё остальное. Ориентация причиной разбиения не служит.
/// </summary>
public sealed class JobSplitterTests
{
    private static readonly SizePt A4 = new(595.28, 841.89);
    private static readonly SizePt A4Landscape = new(841.89, 595.28);
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
    public void Landscape_Sheets_Of_The_Same_Paper_Stay_In_One_Job()
    {
        // Отчёт с альбомными таблицами раньше выходил «сначала все книжные,
        // потом все альбомные», и обороты ложились не на свои листы.
        var plan = Plan(A4, A4Landscape, A4, A4Landscape);
        Assert.False(JobSplitter.NeedsSplit(plan));
        var part = Assert.Single(JobSplitter.SplitByPaperSize(plan));
        Assert.Equal(new[] { 0, 1, 2, 3 }, part.Plan.Sheets.Select(s => s.Pages[0].SourcePageIndex));
    }

    [Fact]
    public void Mixed_Paper_Splits_Into_Consecutive_Runs_In_Document_Order()
    {
        var parts = JobSplitter.SplitByPaperSize(Plan(A4, A4, A3, A4, A3));

        Assert.Equal(4, parts.Count);
        Assert.Equal(new[] { 2, 1, 1, 1 }, parts.Select(p => p.Plan.Sheets.Count));
        var order = parts.SelectMany(p => p.Plan.Sheets).Select(s => s.Pages[0].SourcePageIndex);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
    }

    [Fact]
    public void No_Page_Is_Lost_Or_Duplicated()
    {
        var plan = Plan(A4, A3, A4, A3, A4, A3);
        var printed = JobSplitter.SplitByPaperSize(plan)
            .SelectMany(p => p.Plan.Sheets)
            .SelectMany(s => s.Pages)
            .Select(p => p.SourcePageIndex)
            .ToList();

        Assert.Equal(Enumerable.Range(0, 6), printed);
    }

    [Fact]
    public void Sheets_Are_Renumbered_Inside_Each_Part()
    {
        var parts = JobSplitter.SplitByPaperSize(Plan(A4, A3, A3, A4));

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
        Assert.Contains("210", parts[0].Plan.JobName);
        Assert.Contains("297", parts[0].Plan.JobName);
        Assert.Contains("420", parts[1].Plan.JobName);
        Assert.Equal(JobSplitter.Physical(A3), parts[1].PhysicalPaper);
    }

    [Fact]
    public void Split_Is_Reported_By_Preflight_As_A_Warning()
    {
        var issue = JobSplitter.DescribeSplit(Plan(A4, A3));
        Assert.NotNull(issue);
        Assert.Equal(PreflightLevel.Warning, issue!.Level);
        Assert.Contains("2", issue.Message);

        Assert.Null(JobSplitter.DescribeSplit(Plan(A4, A4Landscape)));
    }

    [Fact]
    public void Collation_Warning_Appears_When_There_Are_Copies()
    {
        // Сортировка разбитого задания работает по частям, а не по документу —
        // сказать об этом надо до печати, а не после.
        var plan = Plan(A4, A3) with { Copies = 3 };
        var issue = JobSplitter.DescribeSplit(plan);
        Assert.Equal(PrintText.Get("PfMixedPaperCopies"), issue!.Suggestion);
    }
}
