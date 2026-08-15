namespace NexusPdf.Printing;

/// <summary>Часть задания, отправляемая отдельно.</summary>
public sealed record JobPart(PrintJobPlan Plan, string Reason);

/// <summary>
/// Разбиение задания на части.
///
/// Windows задаёт формат бумаги одним PrintTicket на всё задание, поэтому
/// документ со смешанными форматами физически нельзя напечатать одним
/// заданием: принтер возьмёт формат первого листа и напечатает на нём всё.
/// Молча так и сделать — значит испортить остальные листы, поэтому задание
/// разбивается, а пользователю показывается список частей.
/// </summary>
public static class JobSplitter
{
    /// <summary>Нужно ли разбивать: в задании больше одного размера листа.</summary>
    public static bool NeedsSplit(PrintJobPlan plan) => DistinctSizes(plan).Count > 1;

    public static IReadOnlyList<SizePt> DistinctSizes(PrintJobPlan plan) =>
        plan.Sheets
            .Select(s => s.PaperSizePt)
            .Distinct()
            .ToList();

    /// <summary>
    /// Части задания по размеру бумаги. Порядок листов внутри части сохраняется,
    /// а сами части идут в порядке первого появления размера — так итоговая
    /// стопка ближе всего к исходному порядку документа.
    /// </summary>
    public static IReadOnlyList<JobPart> SplitByPaperSize(PrintJobPlan plan)
    {
        if (!NeedsSplit(plan))
            return new[] { new JobPart(plan, "Одно задание") };

        var parts = new List<JobPart>();
        var seen = new List<SizePt>();

        foreach (var size in plan.Sheets.Select(s => s.PaperSizePt))
        {
            if (seen.Contains(size)) continue;
            seen.Add(size);

            var sheets = plan.Sheets
                .Where(s => s.PaperSizePt.Equals(size))
                .Select((s, index) => s with { SheetIndex = index, PairedSheetIndex = null })
                .ToList();

            var name = DescribeSize(size);
            parts.Add(new JobPart(
                plan with
                {
                    Sheets = sheets,
                    JobName = $"{plan.JobName} — {name}",
                    // Дуплекс внутри части остаётся, но пары пересчитаны выше:
                    // ссылки на листы другой части были бы неверными.
                    Duplex = plan.Duplex,
                },
                $"Формат {name}: листов {sheets.Count}"));
        }
        return parts;
    }

    private static string DescribeSize(SizePt size)
    {
        var widthMm = Units.PointsToUnit(size.WidthPt, LengthUnit.Millimeters);
        var heightMm = Units.PointsToUnit(size.HeightPt, LengthUnit.Millimeters);
        return $"{widthMm:F0}×{heightMm:F0} мм";
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
            ? "Сортировка копий и скрепление будут работать внутри каждой части отдельно."
            : "Части печатаются одна за другой.";

        return new PreflightIssue(
            PreflightLevel.Warning,
            Preflight.CodeMixedPaper,
            $"В документе листы разного размера, поэтому задание будет разбито на части: {parts.Count}.",
            suggestion);
    }
}
