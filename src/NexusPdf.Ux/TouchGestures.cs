namespace NexusPdf.Ux;

/// <summary>Что жест означает для документа.</summary>
public enum GestureKind
{
    None,

    /// <summary>Прокрутка одним пальцем.</summary>
    Pan,

    /// <summary>Масштабирование щипком.</summary>
    Zoom,

    /// <summary>Долгое удержание — заменяет правую кнопку мыши.</summary>
    LongPress,
}

/// <summary>
/// Разбор жестов: что считать прокруткой, что щипком, а что удержанием.
///
/// Пороги здесь, а не в коде окна, ровно потому, что их пришлось подбирать и
/// они обязаны быть одинаковыми во всех местах: разные пороги в разных
/// панелях — это интерфейс, который «иногда срабатывает».
/// </summary>
public static class TouchGestures
{
    /// <summary>
    /// Сколько нужно сдвинуть палец, чтобы это перестало быть нажатием.
    /// Палец дрожит: 8 точек примерно 2 мм — меньше ловит дрожь, больше
    /// начинает пропускать короткие протяжки.
    /// </summary>
    public const double MoveToleranceDip = 8;

    /// <summary>Удержание для контекстного меню. 450 мс — как в Windows.</summary>
    public const int LongPressMs = 450;

    /// <summary>
    /// Порог изменения масштаба, ниже которого щипок игнорируется: пальцы
    /// расходятся на доли процента при обычной прокрутке, и без порога
    /// документ «дышал» бы масштабом на каждое движение.
    /// </summary>
    public const double ZoomDeadZone = 0.02;

    /// <summary>Границы масштаба — те же, что у колеса мыши и кнопок.</summary>
    public const double MinZoom = 0.25;
    public const double MaxZoom = 4.0;

    /// <summary>Жест по числу пальцев и накопленному изменению.</summary>
    public static GestureKind Classify(int fingerCount, double scaleDelta, double movedDip, int heldMs)
    {
        if (fingerCount <= 0)
            return GestureKind.None;

        if (fingerCount >= 2)
            return Math.Abs(scaleDelta - 1.0) > ZoomDeadZone ? GestureKind.Zoom : GestureKind.Pan;

        if (movedDip <= MoveToleranceDip && heldMs >= LongPressMs)
            return GestureKind.LongPress;

        return movedDip > MoveToleranceDip ? GestureKind.Pan : GestureKind.None;
    }

    /// <summary>Новый масштаб по коэффициенту щипка, с теми же границами.</summary>
    public static double ApplyZoom(double currentZoom, double scaleFactor) =>
        Math.Clamp(currentZoom * scaleFactor, MinZoom, MaxZoom);

    /// <summary>
    /// Толщина штриха по нажиму пера. Нулевой нажим бывает у мыши и у пера без
    /// поддержки нажима — тогда берётся базовая толщина, а не ноль.
    /// </summary>
    public static double StrokeWidthFromPressure(double baseWidthPt, double pressure)
    {
        if (!(pressure > 0) || !double.IsFinite(pressure))
            return baseWidthPt;
        // От половины до полутора базовых: перо должно чувствоваться, но не
        // превращать линию в кляксу.
        var factor = 0.5 + Math.Clamp(pressure, 0, 1);
        return baseWidthPt * Math.Min(factor, 1.5);
    }
}
