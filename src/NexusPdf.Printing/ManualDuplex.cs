namespace NexusPdf.Printing;

/// <summary>Как бумага выходит из принтера.</summary>
public enum OutputFacing
{
    /// <summary>Лицом вниз: стопка на выходе повторяет порядок печати.</summary>
    FaceDown,

    /// <summary>Лицом вверх: стопка на выходе перевёрнута.</summary>
    FaceUp,
}

/// <summary>
/// Как пользователь возвращает стопку в лоток: ключи текстов, которые окно
/// переводит на язык интерфейса (слой печати языков не знает).
/// </summary>
public sealed record ManualDuplexInstructions(
    string HeadlineKey,
    IReadOnlyList<string> StepKeys,
    string EdgeHintKey);

/// <summary>
/// Разбиение задания на два прохода для принтера без автоматического дуплекса.
///
/// Главная ловушка здесь — порядок второго прохода. Стопка выходит из принтера
/// в обратном порядке, поэтому наивная печать «сначала все лицевые, потом все
/// оборотные подряд» кладёт обороты не на свои листы. Испорченную пачку бумаги
/// видно только после печати, поэтому порядок считается здесь и проверяется
/// тестами.
/// </summary>
public static class ManualDuplex
{
    /// <summary>Первый проход: лицевые стороны (чётные индексы листов плана).</summary>
    public static PrintJobPlan FirstPass(PrintJobPlan plan)
    {
        var sheets = Fronts(plan);
        return plan with
        {
            Sheets = Renumber(sheets),
            Duplex = DuplexMode.Simplex,
            JobName = PrintText.Get("MdJobSide1", plan.JobName),
        };
    }

    /// <summary>
    /// Второй проход: обороты. На КАЖДЫЙ лицевой лист приходится ровно один
    /// оборот — у последнего листа нечётного задания он пустой. Без пустого
    /// листа принтер при выводе лицом вверх (подача в обратном порядке)
    /// напечатал бы оборот второго листа на третьем, третьего — на четвёртом и
    /// так далее; при выводе лицом вниз последний лист остался бы в лотке и
    /// попал бы в чужое следующее задание.
    /// Порядок зависит от того, как принтер выкладывает бумагу: при выводе
    /// лицом вниз стопка сохраняет порядок, при выводе лицом вверх —
    /// переворачивается, и второй проход надо развернуть.
    /// </summary>
    public static PrintJobPlan SecondPass(PrintJobPlan plan, OutputFacing facing)
    {
        var sheets = BacksPerFront(plan);
        if (facing == OutputFacing.FaceUp)
            sheets.Reverse();

        return plan with
        {
            Sheets = Renumber(sheets),
            Duplex = DuplexMode.Simplex,
            JobName = PrintText.Get("MdJobSide2", plan.JobName),
        };
    }

    /// <summary>Есть ли вообще что печатать на обороте.</summary>
    public static bool HasSecondPass(PrintJobPlan plan) => plan.Sheets.Count >= 2;

    /// <summary>Пройдёт ли во втором проходе пустой лист (нечётное число сторон).</summary>
    public static bool AddsBlankBack(PrintJobPlan plan) =>
        HasSecondPass(plan) && plan.Sheets.Count % 2 == 1;

    /// <summary>
    /// Переворачивать стопку вокруг короткой стороны: так читают альбомные
    /// листы — и буклет, где лист альбомный и складывается пополам. Для
    /// книжных листов переворот — вокруг длинной стороны, как страницу книги.
    /// </summary>
    public static bool FlipsOnShortEdge(PrintJobPlan plan) =>
        plan.Sheets.Count(s => s.PaperSizePt.IsLandscape) * 2 > plan.Sheets.Count;

    private static List<SheetPlan> Fronts(PrintJobPlan plan)
    {
        // Листы плана идут парами «лицо, оборот»: чётный индекс — лицо.
        var result = new List<SheetPlan>();
        for (var i = 0; i < plan.Sheets.Count; i += 2)
            result.Add(plan.Sheets[i]);
        return result;
    }

    private static List<SheetPlan> BacksPerFront(PrintJobPlan plan)
    {
        var result = new List<SheetPlan>();
        for (var i = 0; i < plan.Sheets.Count; i += 2)
        {
            result.Add(i + 1 < plan.Sheets.Count
                ? plan.Sheets[i + 1]
                : plan.Sheets[i] with
                {
                    // Пустой оборот последнего листа: бумага проходит через
                    // принтер, ничего на ней не печатается.
                    IsFront = false,
                    Pages = Array.Empty<PlacedPage>(),
                    Marks = Array.Empty<SheetMark>(),
                });
        }
        return result;
    }

    private static IReadOnlyList<SheetPlan> Renumber(IEnumerable<SheetPlan> sheets)
    {
        var result = new List<SheetPlan>();
        foreach (var sheet in sheets)
        {
            result.Add(sheet with
            {
                SheetIndex = result.Count,
                // В отдельном задании парного листа больше нет: ссылка на него
                // ввела бы в заблуждение и предпросмотр, и отчёт.
                PairedSheetIndex = null,
            });
        }
        return result;
    }

    /// <summary>
    /// Указания пользователю: что именно сделать со стопкой. Текст зависит и от
    /// вывода принтера, и от края переворота — «переверните» без уточнения края
    /// приводит к перевёрнутым оборотам ровно в половине случаев.
    /// </summary>
    public static ManualDuplexInstructions Explain(OutputFacing facing, bool flipOnShortEdge, bool addsBlankBack = false)
    {
        var steps = new List<string> { "MdStepWait", "MdStepTake" };
        steps.Add(facing == OutputFacing.FaceDown ? "MdStepFaceDown" : "MdStepFaceUp");
        steps.Add(flipOnShortEdge ? "MdStepFlipShort" : "MdStepFlipLong");
        steps.Add("MdStepLoad");
        if (addsBlankBack)
            steps.Add("MdStepBlankBack");
        steps.Add("MdStepTest");

        return new ManualDuplexInstructions(
            "MdFlipHeadline",
            steps,
            flipOnShortEdge ? "MdEdgeShort" : "MdEdgeLong");
    }
}
