namespace NexusPdf.Printing;

/// <summary>Как бумага выходит из принтера.</summary>
public enum OutputFacing
{
    /// <summary>Лицом вниз: стопка на выходе повторяет порядок печати.</summary>
    FaceDown,

    /// <summary>Лицом вверх: стопка на выходе перевёрнута.</summary>
    FaceUp,
}

/// <summary>Как пользователь возвращает стопку в лоток.</summary>
public sealed record ManualDuplexInstructions(
    string Headline,
    IReadOnlyList<string> Steps,
    string EdgeHint);

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
        var sheets = SelectSides(plan, front: true);
        return plan with
        {
            Sheets = Renumber(sheets),
            Duplex = DuplexMode.Simplex,
            JobName = plan.JobName + " — сторона 1",
        };
    }

    /// <summary>
    /// Второй проход: обороты. Порядок зависит от того, как принтер выкладывает
    /// бумагу: при выводе лицом вниз стопка сохраняет порядок, при выводе лицом
    /// вверх — переворачивается, и второй проход надо развернуть.
    /// </summary>
    public static PrintJobPlan SecondPass(PrintJobPlan plan, OutputFacing facing)
    {
        var sheets = SelectSides(plan, front: false).ToList();
        if (facing == OutputFacing.FaceUp)
            sheets.Reverse();

        return plan with
        {
            Sheets = Renumber(sheets),
            Duplex = DuplexMode.Simplex,
            JobName = plan.JobName + " — сторона 2",
        };
    }

    /// <summary>Есть ли вообще что печатать на обороте.</summary>
    public static bool HasSecondPass(PrintJobPlan plan) => SelectSides(plan, front: false).Any();

    private static IEnumerable<SheetPlan> SelectSides(PrintJobPlan plan, bool front)
    {
        // Листы плана идут парами «лицо, оборот»: чётный индекс — лицо.
        for (var i = 0; i < plan.Sheets.Count; i++)
        {
            if (i % 2 == (front ? 0 : 1))
                yield return plan.Sheets[i];
        }
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
    /// вывода принтера, и от края переплёта — «переверните» без уточнения края
    /// приводит к перевёрнутым оборотам ровно в половине случаев.
    /// </summary>
    public static ManualDuplexInstructions Explain(OutputFacing facing, DuplexMode binding)
    {
        var longEdge = binding != DuplexMode.ShortEdge;

        var steps = new List<string>
        {
            "Дождитесь, пока принтер закончит первую сторону.",
            "Заберите стопку из выходного лотка целиком, не меняя порядок листов.",
        };

        if (facing == OutputFacing.FaceDown)
            steps.Add("Листы вышли лицом вниз — порядок стопки уже правильный, перекладывать не нужно.");
        else
            steps.Add("Листы вышли лицом вверх — программа сама напечатает обороты в обратном порядке.");

        steps.Add(longEdge
            ? "Переверните стопку вокруг ДЛИННОЙ стороны, как переворачивают страницу книги."
            : "Переверните стопку вокруг КОРОТКОЙ стороны, как переворачивают лист блокнота.");

        steps.Add("Положите стопку в лоток подачи так же, как лежала чистая бумага.");
        steps.Add("Напечатайте одну проверочную страницу, если печатаете так впервые.");

        return new ManualDuplexInstructions(
            "Переверните стопку и верните её в лоток",
            steps,
            longEdge
                ? "Переплёт по длинной стороне"
                : "Переплёт по короткой стороне");
    }
}
