namespace NexusPdf.Ux;

/// <summary>
/// Арифметика подгонки масштаба и пиксельной сетки. Вынесена из модели
/// документа, чтобы проверяться без WPF: в модели остаётся только «когда»,
/// здесь — «сколько».
/// </summary>
public static class ZoomFitting
{
    /// <summary>Пункт PDF (1/72 дюйма) в единицах WPF (1/96 дюйма).</summary>
    public const double PtToDiu = 96.0 / 72.0;

    public static double Clamp(double zoom) =>
        Math.Clamp(zoom, TouchGestures.MinZoom, TouchGestures.MaxZoom);

    /// <summary>По ширине: лист занимает окно целиком, не считая полей по бокам.</summary>
    public static double ByWidth(double viewportWidthDiu, double pageWidthPt, double marginDiu) =>
        Clamp((viewportWidthDiu - marginDiu * 2) / (Math.Max(1, pageWidthPt) * PtToDiu));

    /// <summary>Целиком: лист помещается и по ширине, и по высоте.</summary>
    public static double ByPage(
        double viewportWidthDiu, double viewportHeightDiu,
        double pageWidthPt, double pageHeightPt, double marginDiu)
    {
        var byWidth = (viewportWidthDiu - marginDiu * 2) / (Math.Max(1, pageWidthPt) * PtToDiu);
        var byHeight = (viewportHeightDiu - marginDiu * 2) / (Math.Max(1, pageHeightPt) * PtToDiu);
        return Clamp(Math.Min(byWidth, byHeight));
    }

    /// <summary>
    /// Как открывать документ, пока человек ничего не выбрал. Портретный
    /// лист — по ширине: так текст крупнее всего, а листать вниз привычно.
    /// Альбомный (слайд, таблица, чертёж) — целиком: по ширине он занял бы
    /// полтора экрана в высоту, и нижняя треть оказывалась бы за краем.
    /// </summary>
    public static bool PrefersWholePage(double pageWidthPt, double pageHeightPt) =>
        pageWidthPt > pageHeightPt;

    /// <summary>
    /// Размер в DIU, занимающий ЦЕЛОЕ число пикселей экрана. Подгонка по ширине
    /// почти всегда даёт дробный масштаб: лист в 1049,37 px рисовался бы в 1049
    /// и растягивался на треть пикселя — вся страница мылилась.
    /// </summary>
    public static double SnapToPixels(double diu, double dpiScale)
    {
        if (!(dpiScale > 0) || !double.IsFinite(dpiScale)) dpiScale = 1.0;
        if (!double.IsFinite(diu)) return 1.0;
        return Math.Max(1, Math.Round(diu * dpiScale)) / dpiScale;
    }
}
