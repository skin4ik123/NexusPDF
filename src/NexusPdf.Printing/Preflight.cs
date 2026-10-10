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
    public const string CodePrintForbidden = "print-forbidden";
    public const string CodeLowQualityOnly = "low-quality-only";

    /// <summary>Масштаб мельче этого почти наверняка ошибка настроек, а не намерение.</summary>
    private const double TinyScale = 0.10;
    private const double HugeScale = 5.0;

    /// <param name="permissions">
    /// Разрешения документа. Запрет печати блокирует задание целиком: обойти
    /// его растеризацией или экспортом раскладки нельзя — это было бы обходом
    /// ограничения, а не функцией.
    /// </param>
    public static IReadOnlyList<PreflightIssue> Analyze(
        PrintJobPlan plan, PrintPermissions? permissions = null)
    {
        var issues = new List<PreflightIssue>();

        var rights = permissions ?? PrintPermissions.Unrestricted;
        if (!rights.AllowPrint)
        {
            issues.Add(new PreflightIssue(PreflightLevel.Critical, CodePrintForbidden,
                PrintText.Get("PfPrintForbidden"),
                PrintText.Get("PfPrintForbiddenHint")));
            return issues;
        }
        if (!rights.AllowHighQuality)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeLowQualityOnly,
                PrintText.Get("PfLowQualityOnly", PrintPermissions.LowQualityDpi.ToString("F0")),
                PrintText.Get("PfLowQualityOnlyHint")));

        if (plan.Sheets.Count == 0 || plan.PlacedPageCount == 0)
        {
            issues.Add(new PreflightIssue(PreflightLevel.Critical, CodeEmptyJob,
                PrintText.Get("PfEmptyJob"),
                PrintText.Get("PfEmptyJobHint")));
            return issues;
        }

        var clipped = plan.Sheets.Where(s => s.HasClippedContent).Select(s => s.SheetIndex).ToList();
        if (clipped.Count > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeClipped,
                PrintText.Get("PfClipped", clipped.Count),
                PrintText.Get("PfClippedHint"), clipped));

        var outside = plan.Sheets
            .Where(s => s.Pages.Any(p => !p.TargetRectPt.IsInside(s.PrintableAreaPt)))
            .Select(s => s.SheetIndex).ToList();
        if (outside.Count > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeOutsidePrintable,
                PrintText.Get("PfOutsidePrintable", outside.Count),
                PrintText.Get("PfOutsidePrintableHint"), outside));

        // Разбиение по формату — не пожелание, а необходимость: Windows задаёт
        // бумагу одним PrintTicket на всё задание.
        if (JobSplitter.DescribeSplit(plan) is { } split)
            issues.Add(split);

        var scales = plan.Sheets.SelectMany(s => s.Pages).Select(p => p.Scale).ToList();
        if (scales.Count > 0)
        {
            if (scales.Min() < TinyScale)
                issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeTinyScale,
                    PrintText.Get("PfTinyScale", (scales.Min() * 100).ToString("F0"))));
            if (scales.Max() > HugeScale)
                issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeHugeScale,
                    PrintText.Get("PfHugeScale", (scales.Max() * 100).ToString("F0"))));
        }

        var blanks = plan.Sheets.Count(s => s.IsInsertedBlank);
        if (blanks > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeBlankInserted,
                PrintText.Get("PfBlankInserted", blanks),
                PrintText.Get("PfBlankInsertedHint")));

        var raster = plan.Sheets.SelectMany(s => s.Pages).Count(p => p.Raster != RasterReason.None);
        if (raster > 0)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeRasterFallback,
                PrintText.Get("PfRaster", raster),
                PrintText.Get("PfRasterHint")));

        if (plan.Duplex == DuplexMode.Manual)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeManualDuplex,
                PrintText.Get("PfManualDuplex"),
                PrintText.Get("PfManualDuplexHint")));

        if (plan.Duplex is DuplexMode.LongEdge or DuplexMode.ShortEdge &&
            !plan.Capabilities.SupportsAnyDuplex)
            issues.Add(new PreflightIssue(PreflightLevel.Error, CodeDuplexUnsupported,
                PrintText.Get("PfDuplexUnsupported"),
                PrintText.Get("PfDuplexUnsupportedHint")));

        if (plan.Copies > 1 && plan.CollationBy == CollationExecutor.Application)
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeCollateInApp,
                PrintText.Get("PfCollateInApp"),
                PrintText.Get("PfCollateInAppHint")));

        if (plan.Capabilities.State is PrinterState.Offline or PrinterState.Error)
            issues.Add(new PreflightIssue(PreflightLevel.Error, CodePrinterOffline,
                PrintText.Get("PfPrinterOffline"),
                PrintText.Get("PfPrinterOfflineHint")));

        return issues;
    }
}
