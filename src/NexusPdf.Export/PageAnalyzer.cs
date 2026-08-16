using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <summary>Что искать на странице при разборе.</summary>
/// <param name="DetectWhitespaceTables">
/// Искать ли таблицы без линий. Это восстановление по расположению текста: на
/// обычной вёрстке оно иногда видит таблицу там, где её нет, поэтому его можно
/// выключить.
/// </param>
/// <param name="IncludeFormValues">Переносить ли значения заполненных полей формы.</param>
/// <param name="RecognizeScans">
/// Распознавать ли страницы-сканы. Без этого страница без текстового слоя
/// выгружается пустой — формально честно, по сути потеря.
/// </param>
public sealed record PageAnalysisOptions(
    bool DetectWhitespaceTables = true,
    bool IncludeFormValues = true,
    bool RecognizeScans = true);

/// <summary>
/// Сырьё страницы → структура: таблицы и строки текста вне таблиц.
///
/// Порядок неслучаен. Сначала берутся таблицы по нарисованным линиям — это
/// факты. Их текст изымается, и только на остатке работает разбор по пробелам,
/// который может ошибаться. Так догадка никогда не переспорит факт.
/// </summary>
public static class PageAnalyzer
{
    public static PageLayout Analyze(
        int pageIndex,
        double widthPt,
        double heightPt,
        IReadOnlyList<PdfTextWord> words,
        IReadOnlyList<PdfRulingLine> rulings,
        IReadOnlyList<PdfFormFieldValue> formFields,
        PageAnalysisOptions? options = null)
    {
        options ??= new PageAnalysisOptions();

        var all = words.ToList();
        if (options.IncludeFormValues)
            all.AddRange(formFields.Select(AsWord));

        var tables = new List<ExtractedTable>(RulingTableDetector.Detect(rulings, all));

        var free = all.Where(w => !tables.Any(t => Covers(t.Bounds, w.CenterY, Center(w)))).ToList();
        var lines = TextLineBuilder.Build(free);

        if (options.DetectWhitespaceTables)
        {
            var guessed = WhitespaceTableDetector.Detect(lines);
            tables.AddRange(guessed);
            lines = lines
                .Where(l => !guessed.Any(t => Covers(t.Bounds, l.CenterY, (l.Left + l.Right) / 2.0)))
                .ToList();
        }

        return new PageLayout(
            pageIndex, widthPt, heightPt,
            tables.OrderByDescending(t => t.Bounds.Top).ToList(),
            lines,
            all);
    }

    /// <summary>
    /// Значение поля формы становится обычным словом: дальше оно участвует в
    /// разборе наравне с текстом и попадает в ту же ячейку, где стоит на бумаге.
    /// </summary>
    private static PdfTextWord AsWord(PdfFormFieldValue field)
    {
        var height = Math.Max(1.0, field.RectPt.Top - field.RectPt.Bottom);
        return new PdfTextWord(
            field.Value.Replace('\r', ' ').Replace('\n', ' ').Trim(),
            field.RectPt,
            Math.Min(height * 0.72, 24),
            400,
            0xFF000000);
    }

    private static double Center(PdfTextWord word) => (word.RectPt.Left + word.RectPt.Right) / 2.0;

    private static bool Covers(PdfTextRect bounds, double y, double x) =>
        x >= bounds.Left && x <= bounds.Right && y <= bounds.Top && y >= bounds.Bottom;
}
