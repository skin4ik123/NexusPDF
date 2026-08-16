namespace NexusPdf.Printing;

/// <summary>
/// Что известно о самом документе к моменту печати. Собирается один раз при
/// открытии окна печати: лезть в файл на каждое движение ползунка незачем.
/// </summary>
/// <param name="Pages">Всего страниц.</param>
/// <param name="SampledPages">По скольким страницам делались замеры.</param>
/// <param name="Images">Сколько изображений найдено на этих страницах.</param>
/// <param name="TextLength">Сколько символов текста на них же.</param>
/// <param name="AverageImageDpi">Средний эффективный DPI изображений; 0 — неизвестно.</param>
/// <param name="HasLayers">Есть ли в документе слои OCG.</param>
public readonly record struct PrintDocumentFacts(
    int Pages,
    int SampledPages,
    int Images,
    int TextLength,
    double AverageImageDpi,
    bool HasLayers)
{
    public static PrintDocumentFacts Unknown { get; } = new(0, 0, 0, 0, 0, false);

    /// <summary>Страница-картинка без текста — почти наверняка скан.</summary>
    public bool LooksScanned => SampledPages > 0 && Images >= SampledPages && TextLength < 200 * SampledPages;
}

/// <summary>
/// Предварительная проверка по САМОМУ документу и по выбранному устройству.
///
/// Отдельно от <see cref="Preflight"/> намеренно: тот разбирает рассчитанный
/// план и ничего не знает ни о содержимом файла, ни о том, что умеет принтер.
/// Здесь ровно наоборот — находки, которые видно только по документу, и их
/// нельзя вывести из геометрии листов.
/// </summary>
public static class DocumentPreflight
{
    public const string CodeScanLowDpi = "doc-scan-low-dpi";
    public const string CodeNoContent = "doc-no-content";
    public const string CodeGrayOnColorPrinter = "doc-gray-on-color-printer";
    public const string CodeLayers = "doc-has-layers";

    /// <summary>
    /// Ниже этого эффективного разрешения скан на бумаге заметно мягче, чем на
    /// экране: монитор показывает около 96 точек на дюйм, принтер — сотни.
    /// </summary>
    private const double SoftScanDpi = 200;

    /// <param name="describe">
    /// Перевод кода находки в текст. Печать — часть интерфейса, и сообщения
    /// обязаны быть на языке пользователя; сама проверка о языках не знает.
    /// </param>
    public static IReadOnlyList<PreflightIssue> Analyze(
        PrintDocumentFacts facts, ColorMode color, PrinterCapabilities? printer,
        Func<string, object[], string> describe)
    {
        var issues = new List<PreflightIssue>();
        if (facts.Pages <= 0) return issues;

        if (facts.SampledPages > 0 && facts.Images == 0 && facts.TextLength == 0)
        {
            issues.Add(new PreflightIssue(PreflightLevel.Warning, CodeNoContent,
                describe(CodeNoContent, Array.Empty<object>())));
        }
        else if (facts.LooksScanned && facts.AverageImageDpi > 0 && facts.AverageImageDpi < SoftScanDpi)
        {
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeScanLowDpi,
                describe(CodeScanLowDpi, new object[] { facts.AverageImageDpi.ToString("F0") })));
        }

        // Цветной принтер и серый режим — не ошибка, но об этом лучше сказать
        // до того, как человек получит серую стопку и станет искать причину.
        if (color is ColorMode.Grayscale or ColorMode.Monochrome && printer?.SupportsColor == true)
        {
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeGrayOnColorPrinter,
                describe(CodeGrayOnColorPrinter, Array.Empty<object>())));
        }

        if (facts.HasLayers)
        {
            issues.Add(new PreflightIssue(PreflightLevel.Info, CodeLayers,
                describe(CodeLayers, Array.Empty<object>())));
        }

        return issues;
    }
}
