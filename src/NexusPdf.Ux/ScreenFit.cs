namespace NexusPdf.Ux;

/// <summary>Прямоугольник окна или рабочей области в единицах WPF.</summary>
public readonly record struct ScreenBox(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Вписывание окна в рабочую область монитора.
///
/// Размеры окон в разметке подбирались на больших экранах; на ноутбуке
/// 1366×768 с масштабом 125 % рабочая область — всего 1093×582 единицы, и
/// окно печати в 820 высотой уходило кнопками под панель задач. Здесь окно
/// получает размер не больше области, минимумы — не больше окна, а положение —
/// внутри экрана.
/// </summary>
public static class ScreenFit
{
    /// <summary>Зазор до краёв области, чтобы окно не прилипало к панели задач.</summary>
    public const double Gap = 8;

    /// <summary>Меньше этого окно не ужимается даже на самом тесном экране.</summary>
    public const double FloorWidth = 320;
    public const double FloorHeight = 240;

    public readonly record struct Result(ScreenBox Box, double MinWidth, double MinHeight);

    public static Result Fit(ScreenBox window, ScreenBox work, double minWidth, double minHeight)
    {
        var maxWidth = Math.Max(FloorWidth, work.Width - Gap);
        var maxHeight = Math.Max(FloorHeight, work.Height - Gap);

        var width = Math.Min(window.Width, maxWidth);
        var height = Math.Min(window.Height, maxHeight);

        // Минимум больше экрана — это окно, которое нельзя ни уменьшить, ни
        // увидеть целиком; он опускается до того, что экран вмещает.
        var fittedMinWidth = Math.Min(Sane(minWidth), maxWidth);
        var fittedMinHeight = Math.Min(Sane(minHeight), maxHeight);

        var left = Math.Max(work.Left, Math.Min(window.Left, work.Right - width));
        var top = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - height));

        return new Result(new ScreenBox(left, top, width, height), fittedMinWidth, fittedMinHeight);
    }

    private static double Sane(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
