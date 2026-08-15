namespace NexusPdf.Printing;

/// <summary>Насколько серьёзна находка предварительной проверки.</summary>
public enum PreflightLevel
{
    Info,
    Warning,
    Error,

    /// <summary>Печать блокируется до исправления или осознанного подтверждения.</summary>
    Critical,
}

/// <summary>
/// Находка предварительной проверки. Код нужен, чтобы интерфейс мог предложить
/// конкретное действие, а не только показать текст.
/// </summary>
public sealed record PreflightIssue(
    PreflightLevel Level,
    string Code,
    string Message,
    string? Suggestion = null,
    IReadOnlyList<int>? AffectedSheets = null);

/// <summary>
/// Проверка рассчитанного плана. Работает по самому плану, а не по настройкам:
/// проверяется то, что действительно уйдёт на бумагу.
/// </summary>
public static class Preflight
{
    public const string CodeClipped = "clipped-content";
    public const string CodeOutsidePrintable = "outside-printable-area";
    public const string CodeMixedPaper = "mixed-paper-sizes";
    public const string CodeTinyScale = "tiny-scale";
    public const string CodeHugeScale = "huge-scale";
    public const string CodeEmptyJob = "empty-job";
    public const string CodeManualDuplex = "manual-duplex";
    public const string CodeDuplexUnsupported = "duplex-unsupported";
    public const string CodePrinterOffline = "printer-offline";
    public const string CodeCollateInApp = "collate-in-app";
    public const string CodeBlankInserted = "blank-pages-inserted";
    public const string CodeRasterFallback = "raster-fallback";

    /// <summary>Масштаб мельче этого почти наверняка ошибка настроек, а не намерение.</summary>
    private const double TinyScale = 0.10;
    private const double HugeScale = 5.0;

    public static IReadOnlyList<PreflightIssue> Analyze(PrintJobPlan plan)
    {
        var issues = new List<PreflightIssue>();

        if (plan.Sheets.Count == 0 || plan.PlacedPageCount == 0)
        {
            issues.Add(new PreflightIssue(PreflightLevel.Critical, CodeEmptyJob,
                "Печатать нечего: под выбранные параметры не попала ни одна страница.",
                "Проверьте диапазон страниц и фильтры."));
            return issues;
        }

        var clipped = plan.Sheets.Where(s => s.HasClippedContent).Select(s => s.SheetIndex).ToList();
        if (clipped.Count > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeClipped,
                $"Содержимое обрезано на листах: {clipped.Count}.",
                "Выберите «Вписать» или бумагу большего размера.", clipped));

        var outside = plan.Sheets
            .Where(s => s.Pages.Any(p => !p.TargetRectPt.IsInside(s.PrintableAreaPt)))
            .Select(s => s.SheetIndex).ToList();
        if (outside.Count > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeOutsidePrintable,
                $"Часть содержимого выходит за печатаемую область на листах: {outside.Count}.",
                "Принтер физически не печатает у самого края бумаги.", outside));

        var paperNames = plan.Sheets.Select(s => $"{s.PaperSizePt.WidthPt:F0}x{s.PaperSizePt.HeightPt:F0}")
            .Distinct().ToList();
        if (paperNames.Count > 1)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeMixedPaper,
                $"В задании листы разного размера: {paperNames.Count}.",
                "Убедитесь, что нужная бумага загружена, либо разделите задание."));

        var scales = plan.Sheets.SelectMany(s => s.Pages).Select(p => p.Scale).ToList();
        if (scales.Count > 0)
        {
            if (scales.Min() < TinyScale)
                issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeTinyScale,
                    $"Минимальный масштаб {scales.Min() * 100:F0}% — текст может стать нечитаемым."));
            if (scales.Max() > HugeScale)
                issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeHugeScale,
                    $"Максимальный масштаб {scales.Max() * 100:F0}% — изображения могут стать размытыми."));
        }

        var blanks = plan.Sheets.Count(s => s.IsInsertedBlank);
        if (blanks > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeBlankInserted,
                $"В задание добавлено пустых листов: {blanks}.",
                "Так требует выбранная раскладка."));

        var raster = plan.Sheets.SelectMany(s => s.Pages).Count(p => p.Raster != RasterReason.None);
        if (raster > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeRasterFallback,
                $"Страниц печатается растром: {raster}.",
                "Растр надёжнее на проблемных драйверах, но текст в нём не векторный."));

        if (plan.Duplex == DuplexMode.Manual)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeManualDuplex,
                "Двусторонняя печать выполняется вручную в два прохода.",
                "После первой стороны программа покажет, как вернуть бумагу в лоток."));

        if (plan.Duplex is DuplexMode.LongEdge or DuplexMode.ShortEdge &&
            !plan.Capabilities.SupportsAnyDuplex)
            issues.Add(new PreflightIssue(PreflightLevel.Error, CodeDuplexUnsupported,
                "Принтер не сообщает о поддержке автоматической двусторонней печати.",
                "Используйте ручную двустороннюю печать."));

        if (plan.Copies > 1 && plan.CollationBy == CollationExecutor.Application)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeCollateInApp,
                "Копии раскладывает программа: объём задания вырастет во столько же раз.",
                "Принтер не сообщил о собственной поддержке сортировки."));

        if (plan.Capabilities.State is PrinterState.Offline or PrinterState.Error)
            issues.Add(new PreflightIssue(PreflightLevel.Error, CodePrinterOffline,
                "Принтер сейчас недоступен.",
                "Задание встанет в очередь и напечатается, когда принтер вернётся."));

        return issues;
    }
}
