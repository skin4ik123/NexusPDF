using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <summary>Что известно о странице до экспорта.</summary>
/// <param name="IsScan">Текста нет, но есть крупная картинка — это скан.</param>
/// <param name="IsBlank">Нет ни текста, ни картинок — страница действительно пустая.</param>
public readonly record struct PageContentKind(bool IsScan, bool IsBlank)
{
    public bool HasText => !IsScan && !IsBlank;
}

/// <summary>
/// Отличает скан от пустой страницы и от вёрстки.
///
/// Без этого экспорт скана молча выдаёт пустой лист: текста на странице нет,
/// значит и переносить нечего. Формально верно, а по сути — потеря данных,
/// которую человек заметит уже после отправки файла.
/// </summary>
public static class ScannedPageDetector
{
    /// <summary>Меньше этого числа букв — считаем, что текста на странице нет.</summary>
    private const int MinMeaningfulChars = 12;

    /// <summary>Какую долю страницы должна занимать картинка, чтобы быть сканом.</summary>
    private const double MinScanCoverage = 0.4;

    public static PageContentKind Classify(
        IReadOnlyList<PdfTextWord> words,
        IReadOnlyList<PdfTextRect> imageBounds,
        double pageWidthPt,
        double pageHeightPt)
    {
        var letters = words.Sum(w => w.Text.Count(char.IsLetterOrDigit));
        if (letters >= MinMeaningfulChars) return new PageContentKind(false, false);

        var pageArea = Math.Max(1.0, pageWidthPt * pageHeightPt);
        var covered = imageBounds.Sum(r =>
            Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Top - r.Bottom));

        if (covered / pageArea >= MinScanCoverage) return new PageContentKind(true, false);
        return new PageContentKind(false, imageBounds.Count == 0 && words.Count == 0);
    }

    /// <summary>
    /// Распознанные слова → слова страницы.
    ///
    /// Распознаватель отдаёт рамки в пунктах от ЛЕВОГО ВЕРХНЕГО угла, а
    /// страница PDF считается от левого нижнего: перепутать эти начала
    /// координат — значит перевернуть всю страницу вверх ногами.
    /// </summary>
    public static IReadOnlyList<PdfTextWord> FromRecognized(
        IEnumerable<OcrWordBox> recognized,
        double pageHeightPt)
    {
        var words = new List<PdfTextWord>();

        foreach (var box in recognized)
        {
            var (text, x, y, width, height) = (box.Text, box.XPt, box.YPt, box.WidthPt, box.HeightPt);
            if (string.IsNullOrWhiteSpace(text) || width <= 0 || height <= 0) continue;
            var left = x;
            var right = x + width;
            var top = pageHeightPt - y;
            var bottom = pageHeightPt - (y + height);

            words.Add(new PdfTextWord(
                text.Trim(),
                new PdfTextRect(left, top, right, bottom),
                // Кегль ниже высоты рамки: распознаватель обводит букву с
                // выносными элементами, а кегль — это размер шрифта.
                Math.Max(1.0, (top - bottom) * 0.78),
                400,
                0xFF000000));
        }

        return words;
    }
}
