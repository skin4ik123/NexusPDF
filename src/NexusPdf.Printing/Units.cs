namespace NexusPdf.Printing;

/// <summary>
/// Единицы измерения интерфейса печати. Внутри всё считается в пунктах PDF
/// (1/72 дюйма) — экранные пиксели появляются только на последнем шаге.
/// </summary>
public enum LengthUnit
{
    Millimeters,
    Centimeters,
    Inches,
    Points,
}

/// <summary>Перевод длин между пунктами PDF и единицами интерфейса.</summary>
public static class Units
{
    public const double PointsPerInch = 72.0;
    public const double MillimetersPerInch = 25.4;

    /// <summary>Device-independent units WPF: 96 на дюйм.</summary>
    public const double DiuPerInch = 96.0;

    public static double PointsToUnit(double points, LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeters => points / PointsPerInch * MillimetersPerInch,
        LengthUnit.Centimeters => points / PointsPerInch * MillimetersPerInch / 10.0,
        LengthUnit.Inches => points / PointsPerInch,
        _ => points,
    };

    public static double UnitToPoints(double value, LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeters => value / MillimetersPerInch * PointsPerInch,
        LengthUnit.Centimeters => value * 10.0 / MillimetersPerInch * PointsPerInch,
        LengthUnit.Inches => value * PointsPerInch,
        _ => value,
    };

    public static double PointsToDiu(double points) => points * DiuPerInch / PointsPerInch;

    public static double DiuToPoints(double diu) => diu * PointsPerInch / DiuPerInch;

    /// <summary>Короткое обозначение единицы для интерфейса.</summary>
    public static string Suffix(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeters => "мм",
        LengthUnit.Centimeters => "см",
        LengthUnit.Inches => "″",
        _ => "пт",
    };
}

/// <summary>Размер в пунктах PDF.</summary>
public readonly record struct SizePt(double WidthPt, double HeightPt)
{
    public bool IsLandscape => WidthPt > HeightPt;

    public SizePt Swapped => new(HeightPt, WidthPt);

    public double Area => WidthPt * HeightPt;

    // ToString переопределён вручную НЕ ради красоты: сгенерированный для record
    // печатает все свойства, включая Swapped, который возвращает SizePt — и
    // печать уходит в бесконечную рекурсию до переполнения стека.
    public override string ToString() => $"{WidthPt:F1}×{HeightPt:F1} пт";
}

/// <summary>Прямоугольник в пунктах PDF, начало координат — левый ВЕРХНИЙ угол листа.</summary>
public readonly record struct RectPt(double XPt, double YPt, double WidthPt, double HeightPt)
{
    public double RightPt => XPt + WidthPt;
    public double BottomPt => YPt + HeightPt;
    public SizePt Size => new(WidthPt, HeightPt);

    public static RectPt FromSize(SizePt size) => new(0, 0, size.WidthPt, size.HeightPt);

    public RectPt Deflate(MarginsPt margins) => new(
        XPt + margins.LeftPt,
        YPt + margins.TopPt,
        Math.Max(0, WidthPt - margins.LeftPt - margins.RightPt),
        Math.Max(0, HeightPt - margins.TopPt - margins.BottomPt));

    /// <summary>Пересечение; пустой прямоугольник, если пересечения нет.</summary>
    public RectPt Intersect(RectPt other)
    {
        var x = Math.Max(XPt, other.XPt);
        var y = Math.Max(YPt, other.YPt);
        var right = Math.Min(RightPt, other.RightPt);
        var bottom = Math.Min(BottomPt, other.BottomPt);
        return right <= x || bottom <= y ? new RectPt(x, y, 0, 0) : new RectPt(x, y, right - x, bottom - y);
    }

    public bool IsEmpty => WidthPt <= 0 || HeightPt <= 0;

    // Как и у SizePt: сгенерированный ToString печатал бы свойство Size и
    // уходил в рекурсию. Заодно вид удобнее для сообщений тестов.
    public override string ToString() =>
        $"[{XPt:F1};{YPt:F1} {WidthPt:F1}×{HeightPt:F1} пт]";

    /// <summary>Содержится ли целиком в другом прямоугольнике с допуском на округление.</summary>
    public bool IsInside(RectPt outer, double tolerancePt = 0.01) =>
        XPt >= outer.XPt - tolerancePt &&
        YPt >= outer.YPt - tolerancePt &&
        RightPt <= outer.RightPt + tolerancePt &&
        BottomPt <= outer.BottomPt + tolerancePt;
}

/// <summary>Поля в пунктах PDF.</summary>
public readonly record struct MarginsPt(double LeftPt, double TopPt, double RightPt, double BottomPt)
{
    public static readonly MarginsPt Zero = new(0, 0, 0, 0);

    public static MarginsPt Uniform(double pt) => new(pt, pt, pt, pt);

    public bool IsZero => LeftPt <= 0 && TopPt <= 0 && RightPt <= 0 && BottomPt <= 0;

    /// <summary>Наибольшее из полей — нужно, когда драйвер даёт одну общую величину.</summary>
    public double MaxPt => Math.Max(Math.Max(LeftPt, RightPt), Math.Max(TopPt, BottomPt));
}
