using System.Globalization;

namespace NexusPdf.Pdf.Abstractions;

/// <summary>Вид измерения.</summary>
public enum MeasurementKind
{
    /// <summary>Расстояние между двумя точками.</summary>
    Distance,

    /// <summary>Длина ломаной.</summary>
    Perimeter,

    /// <summary>Площадь замкнутого многоугольника.</summary>
    Area,
}

/// <summary>Единица реального мира, в которой показывается результат.</summary>
public enum MeasureUnit
{
    Millimeter,
    Centimeter,
    Meter,
    Kilometer,
    Inch,
    Foot,
    Yard,
}

/// <summary>
/// Измерение на чертеже. Точки — в отображаемых пунктах страницы на момент
/// размещения (как у рисунка от руки). Масштаб и единица хранятся В САМОМ
/// измерении, как делает Acrobat: сменив масштаб документа, человек не должен
/// увидеть, как перерисовались все прежние размеры.
/// </summary>
/// <param name="ScaleRatio">Во сколько раз натура больше листа: 200 для чертежа 1:200.</param>
/// <param name="Label">Подпись, которая рисуется на странице («33,12 м»): считается один раз при создании.</param>
/// <param name="FontSizePt">Кегль подписи в пунктах листа.</param>
public sealed record MeasurementDraft(
    MeasurementKind Kind,
    IReadOnlyList<InkPoint> Points,
    double ScaleRatio,
    MeasureUnit Unit,
    int Decimals,
    string Label,
    uint ColorArgb,
    string Author,
    double FontSizePt = 9) : PageOverlay;

/// <summary>Отрезок векторной графики страницы в отображаемых пунктах (начало — левый верхний угол).</summary>
public readonly record struct PdfSegment(double X1, double Y1, double X2, double Y2)
{
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
    public double MidX => (X1 + X2) / 2;
    public double MidY => (Y1 + Y2) / 2;
}

/// <summary>
/// Арифметика измерений: длины, площади, перевод листа в натуру и разбор
/// масштаба. Живёт рядом с моделью, потому что подпись на экране и подпись в
/// файле обязаны считаться одним кодом.
/// </summary>
public static class MeasurementMath
{
    public const double PtToMm = 25.4 / 72.0;

    public static double MillimetersPerUnit(MeasureUnit unit) => unit switch
    {
        MeasureUnit.Millimeter => 1,
        MeasureUnit.Centimeter => 10,
        MeasureUnit.Meter => 1000,
        MeasureUnit.Kilometer => 1_000_000,
        MeasureUnit.Inch => 25.4,
        MeasureUnit.Foot => 304.8,
        MeasureUnit.Yard => 914.4,
        _ => 1,
    };

    public static double Distance(InkPoint a, InkPoint b) =>
        Math.Sqrt((b.XPt - a.XPt) * (b.XPt - a.XPt) + (b.YPt - a.YPt) * (b.YPt - a.YPt));

    /// <summary>Длина ломаной в пунктах листа; замкнутая — с отрезком от последней точки к первой.</summary>
    public static double PathLengthPt(IReadOnlyList<InkPoint> points, bool closed)
    {
        double total = 0;
        for (var i = 1; i < points.Count; i++)
            total += Distance(points[i - 1], points[i]);
        if (closed && points.Count > 2)
            total += Distance(points[^1], points[0]);
        return total;
    }

    /// <summary>Площадь многоугольника в квадратных пунктах (формула шнурков, самопересечения не поддерживаются).</summary>
    public static double PolygonAreaPt2(IReadOnlyList<InkPoint> points)
    {
        if (points.Count < 3) return 0;
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += a.XPt * b.YPt - b.XPt * a.YPt;
        }
        return Math.Abs(sum) / 2;
    }

    /// <summary>Центр тяжести многоугольника; у вырожденного — середина рамки.</summary>
    public static InkPoint Centroid(IReadOnlyList<InkPoint> points)
    {
        if (points.Count == 0) return new InkPoint(0, 0);
        double signed = 0, cx = 0, cy = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var cross = a.XPt * b.YPt - b.XPt * a.YPt;
            signed += cross;
            cx += (a.XPt + b.XPt) * cross;
            cy += (a.YPt + b.YPt) * cross;
        }
        if (Math.Abs(signed) < 1e-9)
        {
            return new InkPoint(
                (points.Min(p => p.XPt) + points.Max(p => p.XPt)) / 2,
                (points.Min(p => p.YPt) + points.Max(p => p.YPt)) / 2);
        }
        return new InkPoint(cx / (3 * signed), cy / (3 * signed));
    }

    /// <summary>Длина на листе → длина в натуре в выбранной единице.</summary>
    public static double ToRealLength(double lengthPt, double scaleRatio, MeasureUnit unit) =>
        lengthPt * PtToMm * scaleRatio / MillimetersPerUnit(unit);

    /// <summary>Площадь на листе → площадь в натуре в квадратных единицах.</summary>
    public static double ToRealArea(double areaPt2, double scaleRatio, MeasureUnit unit)
    {
        var mmPerUnit = MillimetersPerUnit(unit);
        return areaPt2 * PtToMm * PtToMm * scaleRatio * scaleRatio / (mmPerUnit * mmPerUnit);
    }

    /// <summary>
    /// Масштаб по известному отрезку: столько-то пунктов листа — это столько-то
    /// метров в натуре. Так настраивают масштаб, когда чертёж напечатан
    /// «по размеру листа» и его номинальное 1:200 уже ничего не значит.
    /// </summary>
    public static double RatioFromKnownLength(double paperLengthPt, double realLength, MeasureUnit unit)
    {
        if (!(paperLengthPt > 0) || !(realLength > 0)) return 0;
        return realLength * MillimetersPerUnit(unit) / (paperLengthPt * PtToMm);
    }

    /// <summary>
    /// Разбор масштаба в любом привычном виде: «1:200», «1 : 200», «1/200»,
    /// «200», «1:2,5» и «2:1» (увеличение). Возвращает, во сколько раз натура
    /// больше листа.
    /// </summary>
    public static bool TryParseRatio(string? text, out double ratio)
    {
        ratio = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Replace(',', '.').Split(new[] { ':', '/', '÷' }, StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2) return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left) || !(left > 0))
            return false;
        if (parts.Length == 1)
        {
            ratio = left;
            return double.IsFinite(ratio) && ratio > 0;
        }
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var right) || !(right > 0))
            return false;
        ratio = right / left;
        return double.IsFinite(ratio) && ratio > 0;
    }

    private static readonly (string Token, MeasureUnit Unit)[] UnitTokens =
    {
        ("мм", MeasureUnit.Millimeter), ("mm", MeasureUnit.Millimeter),
        ("см", MeasureUnit.Centimeter), ("cm", MeasureUnit.Centimeter),
        ("км", MeasureUnit.Kilometer), ("km", MeasureUnit.Kilometer),
        ("м", MeasureUnit.Meter), ("m", MeasureUnit.Meter),
        ("дюйм", MeasureUnit.Inch), ("дюймов", MeasureUnit.Inch), ("in", MeasureUnit.Inch), ("\"", MeasureUnit.Inch),
        ("фут", MeasureUnit.Foot), ("футов", MeasureUnit.Foot), ("ft", MeasureUnit.Foot), ("'", MeasureUnit.Foot),
        ("ярд", MeasureUnit.Yard), ("ярдов", MeasureUnit.Yard), ("yd", MeasureUnit.Yard),
    };

    /// <summary>
    /// Длина, введённая человеком при калибровке: «22080 мм», «22,08 m», «72 ft»,
    /// просто «22.08» (тогда — в <paramref name="fallback"/>). Запятая и точка
    /// равноправны: чертёжник не обязан помнить десятичный знак своей Windows.
    /// </summary>
    public static bool TryParseLength(string? text, MeasureUnit fallback, out double value, out MeasureUnit unit)
    {
        value = 0;
        unit = fallback;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        var split = 0;
        while (split < trimmed.Length &&
               (char.IsDigit(trimmed[split]) || trimmed[split] is '.' or ',' or ' ' or ' ' or '-' or '+'))
            split++;

        var number = trimmed[..split].Replace(" ", "").Replace(" ", "").Replace(',', '.');
        var suffix = trimmed[split..].Trim().ToLowerInvariant();
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !double.IsFinite(value))
            return false;

        if (suffix.Length == 0) return true;
        foreach (var (token, candidate) in UnitTokens)
        {
            if (suffix == token || suffix == token + ".")
            {
                unit = candidate;
                return true;
            }
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex ScaleInText = new(
        @"(?:SCALE|МАСШТАБ|Масштаб|Scale|М)\s*[:=]?\s*(\d+(?:[.,]\d+)?)\s*[:/]\s*(\d+(?:[.,]\d+)?)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Масштаб, написанный на листе («SCALE 1:200», «Масштаб 1:50», «М 1:100»),
    /// или null, если такой записи нет. Берётся первая правдоподобная: с левой
    /// частью 1 либо с целым отношением.
    /// </summary>
    public static double? FindScaleInText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (System.Text.RegularExpressions.Match match in ScaleInText.Matches(text))
        {
            if (!TryParseRatio(match.Groups[1].Value + ":" + match.Groups[2].Value, out var ratio))
                continue;
            // Масштабы чертежей лежат между 1:1 и 1:10000; остальное — не масштаб.
            if (ratio is >= 0.01 and <= 10_000)
                return ratio;
        }
        return null;
    }

    /// <summary>«1 : 200» для уменьшения, «2 : 1» для увеличения.</summary>
    public static string FormatRatio(double ratio, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (!(ratio > 0) || !double.IsFinite(ratio)) return "1 : 1";
        return ratio >= 1
            ? "1 : " + ratio.ToString("0.###", culture)
            : (1 / ratio).ToString("0.###", culture) + " : 1";
    }

    /// <summary>Число с заданным числом знаков в культуре пользователя (запятая у русских).</summary>
    public static string FormatValue(double value, int decimals, IFormatProvider culture) =>
        value.ToString("F" + Math.Clamp(decimals, 0, 6), culture);

    /// <summary>
    /// Направление, прижатое к ближайшему углу, кратному 45°, с сохранением
    /// проекции на это направление — так ведёт себя Shift в чертёжных программах.
    /// </summary>
    public static InkPoint ConstrainTo45(InkPoint anchor, InkPoint point)
    {
        var dx = point.XPt - anchor.XPt;
        var dy = point.YPt - anchor.YPt;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return point;
        var angle = Math.Atan2(dy, dx);
        var step = Math.PI / 4;
        var snapped = Math.Round(angle / step) * step;
        var projected = dx * Math.Cos(snapped) + dy * Math.Sin(snapped);
        return new InkPoint(anchor.XPt + projected * Math.Cos(snapped), anchor.YPt + projected * Math.Sin(snapped));
    }

    /// <summary>Угол отрезка к горизонтали в градусах, 0…180 (экранная ось Y смотрит вниз).</summary>
    public static double AngleDegrees(InkPoint a, InkPoint b)
    {
        var degrees = Math.Atan2(-(b.YPt - a.YPt), b.XPt - a.XPt) * 180 / Math.PI;
        if (degrees < 0) degrees += 180;
        return degrees >= 180 ? degrees - 180 : degrees;
    }
}

/// <summary>Чем именно курсор прилип к чертежу.</summary>
public enum SnapKind
{
    None,

    /// <summary>Конец отрезка.</summary>
    Endpoint,

    /// <summary>Пересечение двух отрезков.</summary>
    Intersection,

    /// <summary>Середина отрезка.</summary>
    Midpoint,

    /// <summary>Ближайшая точка на отрезке.</summary>
    OnSegment,

    /// <summary>Уже поставленная точка этого же измерения (замыкание контура).</summary>
    OwnVertex,
}

public readonly record struct SnapResult(double X, double Y, SnapKind Kind);

/// <summary>
/// Привязка курсора к векторной графике страницы.
///
/// Чертёж из САПР — это сотни тысяч отрезков, и точность измерения зависит от
/// попадания ровно в конец линии или в пересечение, а не «куда-то рядом».
/// Отрезки разложены по сетке ячеек, поэтому поиск рядом с курсором смотрит
/// лишь на соседние ячейки. Приоритет как в чертёжных программах: конец,
/// пересечение, середина, точка на линии.
/// </summary>
public sealed class VectorSnapIndex
{
    private readonly PdfSegment[] _segments;
    private readonly double _cell;
    private readonly int _cols;
    private readonly int _rows;
    private readonly int[] _cellStart;   // CSR: начало списка ячейки в _items
    private readonly int[] _items;
    private readonly int[] _mark;        // отметка «кандидат уже взят» для текущего запроса
    private int _query;

    /// <summary>Сколько кандидатов попарно проверяется на пересечение — ограничение против O(n²) в густых местах.</summary>
    private const int MaxIntersectionCandidates = 400;

    public VectorSnapIndex(IReadOnlyList<PdfSegment> segments, double pageWidthPt, double pageHeightPt, double cellPt = 32)
    {
        _segments = segments as PdfSegment[] ?? segments.ToArray();
        _cell = cellPt > 1 ? cellPt : 32;
        _cols = Math.Max(1, (int)Math.Ceiling(Math.Max(1, pageWidthPt) / _cell));
        _rows = Math.Max(1, (int)Math.Ceiling(Math.Max(1, pageHeightPt) / _cell));
        _mark = new int[_segments.Length];

        // Два прохода: сначала считаем, сколько отрезков в каждой ячейке, потом
        // раскладываем — без списков на каждую ячейку память не раздувается.
        var counts = new int[_cols * _rows + 1];
        for (var i = 0; i < _segments.Length; i++)
            ForEachCell(_segments[i], cell => counts[cell + 1]++);
        for (var c = 1; c < counts.Length; c++)
            counts[c] += counts[c - 1];
        _cellStart = counts;
        _items = new int[counts[^1]];
        var fill = new int[_cols * _rows];
        for (var i = 0; i < _segments.Length; i++)
        {
            var index = i;
            ForEachCell(_segments[i], cell => _items[_cellStart[cell] + fill[cell]++] = index);
        }
    }

    public int Count => _segments.Length;

    /// <summary>
    /// Ячейки, через которые проходит отрезок: точки берутся с шагом в полклетки,
    /// поэтому длинная диагональ не метит весь свой описывающий квадрат.
    /// </summary>
    private void ForEachCell(PdfSegment s, Action<int> visit)
    {
        var length = s.Length;
        var steps = Math.Max(1, (int)Math.Ceiling(length / (_cell / 2)));
        var last = -1;
        for (var k = 0; k <= steps; k++)
        {
            var t = (double)k / steps;
            var cell = CellOf(s.X1 + (s.X2 - s.X1) * t, s.Y1 + (s.Y2 - s.Y1) * t);
            if (cell == last) continue;
            last = cell;
            visit(cell);
        }
    }

    private int CellOf(double x, double y)
    {
        var col = Math.Clamp((int)Math.Floor(x / _cell), 0, _cols - 1);
        var row = Math.Clamp((int)Math.Floor(y / _cell), 0, _rows - 1);
        return row * _cols + col;
    }

    /// <summary>Лучшая точка привязки в радиусе или null, если рядом ничего нет.</summary>
    public SnapResult? Find(double x, double y, double radiusPt)
    {
        if (_segments.Length == 0 || !(radiusPt > 0)) return null;
        var candidates = Candidates(x, y, radiusPt);
        if (candidates.Count == 0) return null;
        var radius2 = radiusPt * radiusPt;

        // 1. Концы отрезков.
        var best = BestEndpoint(candidates, x, y, radius2);
        if (best != null) return best;

        // 2. Пересечения.
        best = BestIntersection(candidates, x, y, radius2);
        if (best != null) return best;

        // 3. Середины.
        double bestDistance = double.MaxValue;
        SnapResult? result = null;
        foreach (var i in candidates)
        {
            var s = _segments[i];
            if (s.Length < 2) continue; // у коротких штрихов середина — шум
            var d = Sq(s.MidX - x, s.MidY - y);
            if (d <= radius2 && d < bestDistance)
            {
                bestDistance = d;
                result = new SnapResult(s.MidX, s.MidY, SnapKind.Midpoint);
            }
        }
        if (result != null) return result;

        // 4. Ближайшая точка на отрезке.
        bestDistance = double.MaxValue;
        foreach (var i in candidates)
        {
            var s = _segments[i];
            var (px, py) = ClosestPointOnSegment(s, x, y);
            var d = Sq(px - x, py - y);
            if (d <= radius2 && d < bestDistance)
            {
                bestDistance = d;
                result = new SnapResult(px, py, SnapKind.OnSegment);
            }
        }
        return result;
    }

    private List<int> Candidates(double x, double y, double radius)
    {
        var result = new List<int>();
        _query++;
        var c0 = Math.Clamp((int)Math.Floor((x - radius) / _cell), 0, _cols - 1);
        var c1 = Math.Clamp((int)Math.Floor((x + radius) / _cell), 0, _cols - 1);
        var r0 = Math.Clamp((int)Math.Floor((y - radius) / _cell), 0, _rows - 1);
        var r1 = Math.Clamp((int)Math.Floor((y + radius) / _cell), 0, _rows - 1);
        for (var row = r0; row <= r1; row++)
        {
            for (var col = c0; col <= c1; col++)
            {
                var cell = row * _cols + col;
                for (var k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    var index = _items[k];
                    if (_mark[index] == _query) continue;
                    _mark[index] = _query;
                    result.Add(index);
                }
            }
        }
        return result;
    }

    private SnapResult? BestEndpoint(List<int> candidates, double x, double y, double radius2)
    {
        double bestDistance = double.MaxValue;
        SnapResult? result = null;
        foreach (var i in candidates)
        {
            var s = _segments[i];
            var d1 = Sq(s.X1 - x, s.Y1 - y);
            if (d1 <= radius2 && d1 < bestDistance)
            {
                bestDistance = d1;
                result = new SnapResult(s.X1, s.Y1, SnapKind.Endpoint);
            }
            var d2 = Sq(s.X2 - x, s.Y2 - y);
            if (d2 <= radius2 && d2 < bestDistance)
            {
                bestDistance = d2;
                result = new SnapResult(s.X2, s.Y2, SnapKind.Endpoint);
            }
        }
        return result;
    }

    private SnapResult? BestIntersection(List<int> candidates, double x, double y, double radius2)
    {
        var n = Math.Min(candidates.Count, MaxIntersectionCandidates);
        double bestDistance = double.MaxValue;
        SnapResult? result = null;
        for (var a = 0; a < n; a++)
        {
            var s = _segments[candidates[a]];
            for (var b = a + 1; b < n; b++)
            {
                if (!TryIntersect(s, _segments[candidates[b]], out var ix, out var iy)) continue;
                var d = Sq(ix - x, iy - y);
                if (d <= radius2 && d < bestDistance)
                {
                    bestDistance = d;
                    result = new SnapResult(ix, iy, SnapKind.Intersection);
                }
            }
        }
        return result;
    }

    /// <summary>Пересечение двух отрезков (с крошечным допуском на концах); параллельные не пересекаются.</summary>
    public static bool TryIntersect(PdfSegment a, PdfSegment b, out double x, out double y)
    {
        x = y = 0;
        var dax = a.X2 - a.X1; var day = a.Y2 - a.Y1;
        var dbx = b.X2 - b.X1; var dby = b.Y2 - b.Y1;
        var cross = dax * dby - day * dbx;
        if (Math.Abs(cross) < 1e-9) return false;
        var sx = b.X1 - a.X1; var sy = b.Y1 - a.Y1;
        var t = (sx * dby - sy * dbx) / cross;
        var u = (sx * day - sy * dax) / cross;
        const double epsilon = 1e-6;
        if (t < -epsilon || t > 1 + epsilon || u < -epsilon || u > 1 + epsilon) return false;
        x = a.X1 + dax * t;
        y = a.Y1 + day * t;
        return true;
    }

    public static (double X, double Y) ClosestPointOnSegment(PdfSegment s, double x, double y)
    {
        var dx = s.X2 - s.X1; var dy = s.Y2 - s.Y1;
        var length2 = dx * dx + dy * dy;
        if (length2 < 1e-12) return (s.X1, s.Y1);
        var t = Math.Clamp(((x - s.X1) * dx + (y - s.Y1) * dy) / length2, 0, 1);
        return (s.X1 + dx * t, s.Y1 + dy * t);
    }

    private static double Sq(double dx, double dy) => dx * dx + dy * dy;
}
