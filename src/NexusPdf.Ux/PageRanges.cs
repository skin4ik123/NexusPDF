using System.Globalization;

namespace NexusPdf.Ux;

/// <summary>Номера страниц для человека: «1, 3–5, 8» вместо списка индексов.</summary>
public static class PageRanges
{
    /// <summary>Индексы с нуля → диапазоны с единицы, подряд идущие склеиваются.</summary>
    public static string Format(IReadOnlyList<int> zeroBasedIndices)
    {
        var sorted = zeroBasedIndices.Distinct().OrderBy(i => i).ToList();
        if (sorted.Count == 0) return "";

        var parts = new List<string>();
        var start = sorted[0];
        var previous = start;
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] == previous + 1)
            {
                previous = sorted[i];
                continue;
            }
            parts.Add(start == previous
                ? (start + 1).ToString(CultureInfo.InvariantCulture)
                : (start + 1).ToString(CultureInfo.InvariantCulture) + "–" + (previous + 1).ToString(CultureInfo.InvariantCulture));
            if (i < sorted.Count)
            {
                start = sorted[i];
                previous = start;
            }
        }
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Размер миниатюр организатора. На экране карточка — любой ширины, растр же
/// заказывается ступенями по 50 px: иначе каждый шаг ползунка требовал бы
/// нового растра для каждой страницы и забивал кэш почти одинаковыми картинками.
/// </summary>
public static class ThumbnailSizing
{
    public const double MinWidth = 90;
    public const double MaxWidth = 420;
    public const double DefaultWidth = 150;

    /// <summary>Высота карточки под портретный лист с небольшим запасом.</summary>
    public const double HeightFactor = 1.27;

    public static double Clamp(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : DefaultWidth;

    /// <summary>Ширина растра в пикселях под карточку данной ширины при данной плотности экрана.</summary>
    public static int PixelWidth(double cardWidthDiu, double dpiScale)
    {
        var dpi = dpiScale > 0 && double.IsFinite(dpiScale) ? dpiScale : 1.0;
        var wanted = Clamp(cardWidthDiu) * dpi;
        return Math.Max(150, (int)Math.Ceiling(wanted / 50.0) * 50);
    }
}
