namespace NexusPdf.Printing;

/// <summary>Часть задания, отправляемая отдельно.</summary>
public sealed record JobPart(PrintJobPlan Plan, SizePt PhysicalPaper);

/// <summary>
/// Разбиение задания на части.
///
/// Windows задаёт формат бумаги одним PrintTicket на всё задание, поэтому
/// документ на РАЗНОЙ бумаге физически нельзя напечатать одним заданием:
/// принтер возьмёт формат первого листа и напечатает на нём всё.
///
/// Ориентация причиной разбиения НЕ служит. Раньше альбомный лист A4 считался
/// «другим форматом», и отчёт с альбомными таблицами выходил так: сначала все
/// книжные листы, потом все альбомные, а обороты при двусторонней печати
/// ложились не на свои листы. Листы и так уходят на принтер изображениями,
/// поэтому альбомный лист просто поворачивается на книжной странице задания.
/// </summary>
public static class JobSplitter
{
    /// <summary>Допуск сравнения форматов: драйверы округляют размеры по-разному.</summary>
    private const double TolerancePt = 1.5;

    /// <summary>Нужно ли разбивать: в задании больше одного физического формата.</summary>
    public static bool NeedsSplit(PrintJobPlan plan) => DistinctPapers(plan).Count > 1;

    /// <summary>Физические форматы задания: книжная и альбомная A4 — одна бумага.</summary>
    public static IReadOnlyList<SizePt> DistinctPapers(PrintJobPlan plan)
    {
        var result = new List<SizePt>();
        foreach (var sheet in plan.Sheets)
        {
            var paper = Physical(sheet.PaperSizePt);
            if (!result.Any(p => SamePaper(p, paper)))
                result.Add(paper);
        }
        return result;
    }

    /// <summary>Формат бумаги без учёта ориентации: короткая сторона — ширина.</summary>
    public static SizePt Physical(SizePt size) =>
        size.WidthPt <= size.HeightPt ? size : new SizePt(size.HeightPt, size.WidthPt);

    public static bool SamePaper(SizePt a, SizePt b)
    {
        var pa = Physical(a);
        var pb = Physical(b);
        return Math.Abs(pa.WidthPt - pb.WidthPt) <= TolerancePt &&
               Math.Abs(pa.HeightPt - pb.HeightPt) <= TolerancePt;
    }

    /// <summary>
    /// Части задания: подряд идущие листы на одной бумаге. Порядок документа
    /// сохраняется полностью — части идут ровно так, как листы стоят в плане,
    /// и стопка на выходе совпадает с документом.
    /// </summary>
    public static IReadOnlyList<JobPart> SplitByPaperSize(PrintJobPlan plan)
    {
        if (plan.Sheets.Count == 0 || !NeedsSplit(plan))
        {
            var paper = plan.Sheets.Count > 0 ? Physical(plan.Sheets[0].PaperSizePt) : default;
            return new[] { new JobPart(plan, paper) };
        }

        var parts = new List<JobPart>();
        var run = new List<SheetPlan>();
        SizePt? current = null;

        void Flush()
        {
            if (run.Count == 0 || current == null) return;
            var sheets = run
                .Select((s, index) => s with
                {
                    SheetIndex = index,
                    // Ссылка на парный лист другой части указывала бы в никуда.
                    PairedSheetIndex = null,
                })
                .ToList();
            parts.Add(new JobPart(
                plan with
                {
                    Sheets = sheets,
                    JobName = plan.JobName + " — " + DescribeSize(current.Value),
                },
                current.Value));
            run = new List<SheetPlan>();
        }

        foreach (var sheet in plan.Sheets)
        {
            var paper = Physical(sheet.PaperSizePt);
            if (current is { } open && !SamePaper(open, paper))
                Flush();
            if (run.Count == 0)
                current = paper;
            run.Add(sheet);
        }
        Flush();
        return parts;
    }

    public static string DescribeSize(SizePt size)
    {
        var widthMm = Units.PointsToUnit(size.WidthPt, LengthUnit.Millimeters);
        var heightMm = Units.PointsToUnit(size.HeightPt, LengthUnit.Millimeters);
        return PrintText.Get("PrintSizeMm", widthMm.ToString("F0"), heightMm.ToString("F0"));
    }

    /// <summary>
    /// Предупреждение о разбиении для предварительной проверки. Сортировка
    /// копий и скрепление разбитого задания работают по частям, а не по всему
    /// документу — об этом надо сказать до печати, а не после.
    /// </summary>
    public static PreflightIssue? DescribeSplit(PrintJobPlan plan)
    {
        if (!NeedsSplit(plan)) return null;

        var parts = SplitByPaperSize(plan);
        var suggestion = plan.Copies > 1 || plan.Capabilities.SupportsStapling
            ? PrintText.Get("PfMixedPaperCopies")
            : PrintText.Get("PfMixedPaperSequential");

        return new PreflightIssue(
            PreflightLevel.Warning,
            Preflight.CodeMixedPaper,
            PrintText.Get("PfMixedPaper", parts.Count),
            suggestion);
    }
}
