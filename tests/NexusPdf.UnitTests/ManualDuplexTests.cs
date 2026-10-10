using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Ручная двусторонняя печать. Ошибка порядка здесь видна только после того,
/// как испорчена пачка бумаги, поэтому порядок проверяется тестами.
/// </summary>
public sealed class ManualDuplexTests
{
    private static readonly SizePt A4 = new(595.28, 841.89);
    private static readonly PaperSizeOption A4Paper = new("A4", A4);

    private static PrinterCapabilities Caps() => new()
    {
        PrinterName = "Без дуплекса",
        PaperSizes = new[] { A4Paper },
        HardMarginsByPaper = new Dictionary<string, MarginsPt> { ["A4"] = MarginsPt.Zero },
    };

    private readonly PrintLayoutEngine _engine = new();

    private PrintJobPlan Plan(int pageCount, DuplexMode duplex = DuplexMode.Manual)
    {
        var pages = Enumerable.Range(0, pageCount)
            .Select(i => new SourcePage("doc", i, A4)).ToList();
        var settings = new LayoutSettings { Duplex = duplex };
        var sheets = _engine.BuildSheets(pages, settings, A4Paper, Caps());
        sheets = _engine.ApplyDuplexPairing(sheets, duplex, settings.Imposition);

        return new PrintJobPlan
        {
            JobName = "Задание",
            PrinterName = "Без дуплекса",
            Capabilities = Caps(),
            Sheets = sheets,
            Duplex = duplex,
        };
    }

    [Fact]
    public void Pairing_Marks_Sides_And_Links_Them()
    {
        var plan = Plan(4);

        Assert.True(plan.Sheets[0].IsFront);
        Assert.False(plan.Sheets[1].IsFront);
        Assert.Equal(1, plan.Sheets[0].PairedSheetIndex);
        Assert.Equal(0, plan.Sheets[1].PairedSheetIndex);
    }

    [Fact]
    public void Odd_Page_Count_Leaves_The_Last_Back_Unpaired()
    {
        // У пяти страниц последний лист печатается только с одной стороны.
        var plan = Plan(5);
        Assert.True(plan.Sheets[4].IsFront);
        Assert.Null(plan.Sheets[4].PairedSheetIndex);
    }

    [Fact]
    public void Paper_Count_Halves_For_Two_Sided_Printing()
    {
        Assert.Equal(4, Plan(4, DuplexMode.Simplex).SheetCount); // 4 листа бумаги
        Assert.Equal(2, Plan(4).SheetCount);                     // те же 4 страницы на 2 листах
        Assert.Equal(3, Plan(5).SheetCount);                     // пятая страница добирает третий лист
    }

    [Fact]
    public void First_Pass_Prints_Only_Fronts()
    {
        var first = ManualDuplex.FirstPass(Plan(6));

        Assert.Equal(3, first.Sheets.Count);
        Assert.Equal(DuplexMode.Simplex, first.Duplex);
        // Страницы 1, 3, 5 документа.
        Assert.Equal(new[] { 0, 2, 4 }, first.Sheets.Select(s => s.Pages[0].SourcePageIndex));
    }

    [Fact]
    public void Second_Pass_Order_Depends_On_How_Paper_Comes_Out()
    {
        var plan = Plan(6);

        // Лицом вниз: стопка сохраняет порядок, обороты печатаются как есть.
        var faceDown = ManualDuplex.SecondPass(plan, OutputFacing.FaceDown);
        Assert.Equal(new[] { 1, 3, 5 }, faceDown.Sheets.Select(s => s.Pages[0].SourcePageIndex));

        // Лицом вверх: стопка перевёрнута, второй проход обязан идти обратно.
        var faceUp = ManualDuplex.SecondPass(plan, OutputFacing.FaceUp);
        Assert.Equal(new[] { 5, 3, 1 }, faceUp.Sheets.Select(s => s.Pages[0].SourcePageIndex));
    }

    [Fact]
    public void Passes_Together_Cover_Every_Page_Exactly_Once()
    {
        var plan = Plan(7);
        var first = ManualDuplex.FirstPass(plan);
        var second = ManualDuplex.SecondPass(plan, OutputFacing.FaceUp);

        var printed = first.Sheets.Concat(second.Sheets)
            .SelectMany(s => s.Pages)
            .Select(p => p.SourcePageIndex)
            .OrderBy(i => i)
            .ToList();

        Assert.Equal(Enumerable.Range(0, 7), printed);
    }

    [Fact]
    public void Sheets_Are_Renumbered_And_Unlinked_In_Each_Pass()
    {
        // В отдельном задании парного листа больше нет: ссылка на него ввела бы
        // в заблуждение и предпросмотр, и отчёт.
        var first = ManualDuplex.FirstPass(Plan(6));
        Assert.Equal(new[] { 0, 1, 2 }, first.Sheets.Select(s => s.SheetIndex));
        Assert.All(first.Sheets, s => Assert.Null(s.PairedSheetIndex));
    }

    [Fact]
    public void Single_Page_Job_Has_No_Second_Pass()
    {
        var plan = Plan(1);
        Assert.False(ManualDuplex.HasSecondPass(plan));
        Assert.Single(ManualDuplex.FirstPass(plan).Sheets);
    }

    [Fact]
    public void Odd_Count_Face_Up_Starts_The_Second_Pass_With_A_Blank_Back()
    {
        // Лицом вверх принтер подаёт стопку в обратном порядке: первым идёт
        // последний лицевой лист, у которого оборота нет. Без пустого листа
        // оборот второго листа лёг бы на третий, первого — на второй.
        var plan = Plan(5);
        var second = ManualDuplex.SecondPass(plan, OutputFacing.FaceUp);

        Assert.Equal(ManualDuplex.FirstPass(plan).Sheets.Count, second.Sheets.Count);
        Assert.Empty(second.Sheets[0].Pages);
        Assert.Equal(new[] { 3, 1 }, second.Sheets.Skip(1).Select(s => s.Pages[0].SourcePageIndex));
        Assert.True(ManualDuplex.AddsBlankBack(plan));
    }

    [Fact]
    public void Odd_Count_Face_Down_Ends_The_Second_Pass_With_A_Blank_Back()
    {
        // Лицом вниз последний лист иначе остался бы в лотке и ушёл бы в
        // следующее, чужое задание.
        var second = ManualDuplex.SecondPass(Plan(5), OutputFacing.FaceDown);

        Assert.Equal(3, second.Sheets.Count);
        Assert.Equal(new[] { 1, 3 }, second.Sheets.Take(2).Select(s => s.Pages[0].SourcePageIndex));
        Assert.Empty(second.Sheets[2].Pages);
    }

    [Fact]
    public void Even_Count_Adds_No_Blank_Back()
    {
        var plan = Plan(6);
        Assert.False(ManualDuplex.AddsBlankBack(plan));
        Assert.All(ManualDuplex.SecondPass(plan, OutputFacing.FaceUp).Sheets, s => Assert.NotEmpty(s.Pages));
    }

    [Fact]
    public void Instructions_Name_The_Edge_Explicitly()
    {
        // «Переверните» без уточнения края даёт перевёрнутые обороты ровно в
        // половине случаев, поэтому край назван прямо.
        var longEdge = ManualDuplex.Explain(OutputFacing.FaceDown, flipOnShortEdge: false);
        Assert.Contains("MdStepFlipLong", longEdge.StepKeys);
        Assert.Equal("MdEdgeLong", longEdge.EdgeHintKey);

        var shortEdge = ManualDuplex.Explain(OutputFacing.FaceDown, flipOnShortEdge: true);
        Assert.Contains("MdStepFlipShort", shortEdge.StepKeys);
        Assert.Equal("MdEdgeShort", shortEdge.EdgeHintKey);
    }

    [Fact]
    public void Instructions_Explain_The_Output_Direction_And_The_Blank_Sheet()
    {
        var faceUp = ManualDuplex.Explain(OutputFacing.FaceUp, flipOnShortEdge: false, addsBlankBack: true);
        Assert.Contains("MdStepFaceUp", faceUp.StepKeys);
        Assert.Contains("MdStepBlankBack", faceUp.StepKeys);

        var faceDown = ManualDuplex.Explain(OutputFacing.FaceDown, flipOnShortEdge: false);
        Assert.Contains("MdStepFaceDown", faceDown.StepKeys);
        Assert.DoesNotContain("MdStepBlankBack", faceDown.StepKeys);
    }

    [Fact]
    public void Landscape_Sheets_Flip_On_The_Short_Edge()
    {
        // Альбомные листы (и буклет — альбомный лист, сложенный пополам)
        // переворачивают по короткому краю, книжные — по длинному.
        var portrait = Plan(4);
        Assert.False(ManualDuplex.FlipsOnShortEdge(portrait));

        var landscape = portrait with
        {
            Sheets = portrait.Sheets.Select(s => s with { PaperSizePt = new SizePt(841.89, 595.28) }).ToList(),
        };
        Assert.True(ManualDuplex.FlipsOnShortEdge(landscape));
    }
}
