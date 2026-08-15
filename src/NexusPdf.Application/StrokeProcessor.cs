namespace NexusPdf.Application;

/// <summary>Точка штриха в отображаемых пунктах страницы.</summary>
public readonly record struct StrokePoint(double X, double Y);

/// <summary>
/// Обработка рукописного штриха: стабилизация дрожания, прореживание,
/// автовыпрямление и привязка к углам. Ни одна из операций не трогает PDF —
/// это чистая геометрия, поэтому её можно проверить тестами до последнего шага.
/// </summary>
public static class StrokeProcessor
{
    /// <summary>Сила стабилизации по умолчанию: заметно гасит дрожь, но перо не «уплывает».</summary>
    public const double DefaultStabilization = 0.65;

    /// <summary>Допуск прореживания в пунктах: на глаз не видно, а точек становится в разы меньше.</summary>
    public const double DefaultSimplifyTolerance = 0.4;

    /// <summary>
    /// Стабилизатор «на верёвочке»: рисуемая точка догоняет курсор с
    /// запаздыванием, поэтому мелкая дрожь руки гасится, а форма линии
    /// сохраняется. strength 0 — без сглаживания, ближе к 1 — сильнее.
    /// </summary>
    public static IReadOnlyList<StrokePoint> Stabilize(
        IReadOnlyList<StrokePoint> points, double strength = DefaultStabilization)
    {
        if (points.Count < 3 || strength <= 0)
            return points;
        var alpha = 1.0 - Math.Clamp(strength, 0.0, 0.95);
        var result = new List<StrokePoint>(points.Count) { points[0] };
        var current = points[0];
        for (var i = 1; i < points.Count; i++)
        {
            current = new StrokePoint(
                current.X + (points[i].X - current.X) * alpha,
                current.Y + (points[i].Y - current.Y) * alpha);
            result.Add(current);
        }
        // Запаздывание не должно «съедать» конец штриха: последняя точка
        // всегда там, где пользователь отпустил кнопку.
        result[^1] = points[^1];
        return result;
    }

    /// <summary>Прореживание Рамера—Дугласа—Пекера: убирает точки, не меняющие вид линии.</summary>
    public static IReadOnlyList<StrokePoint> Simplify(
        IReadOnlyList<StrokePoint> points, double tolerance = DefaultSimplifyTolerance)
    {
        if (points.Count < 3 || tolerance <= 0)
            return points;
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        SimplifyRange(points, 0, points.Count - 1, tolerance, keep);

        var result = new List<StrokePoint>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i])
                result.Add(points[i]);
        return result;
    }

    private static void SimplifyRange(
        IReadOnlyList<StrokePoint> points, int first, int last, double tolerance, bool[] keep)
    {
        if (last <= first + 1)
            return;
        var maxDistance = -1.0;
        var maxIndex = -1;
        for (var i = first + 1; i < last; i++)
        {
            var distance = PerpendicularDistance(points[i], points[first], points[last]);
            if (distance > maxDistance)
            {
                maxDistance = distance;
                maxIndex = i;
            }
        }
        if (maxDistance <= tolerance || maxIndex < 0)
            return;
        keep[maxIndex] = true;
        SimplifyRange(points, first, maxIndex, tolerance, keep);
        SimplifyRange(points, maxIndex, last, tolerance, keep);
    }

    /// <summary>Расстояние от точки до отрезка (не до прямой): у коротких штрихов это важно.</summary>
    public static double PerpendicularDistance(StrokePoint point, StrokePoint a, StrokePoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
            return Distance(point, a);
        var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0.0, 1.0);
        return Distance(point, new StrokePoint(a.X + t * dx, a.Y + t * dy));
    }

    public static double Distance(StrokePoint a, StrokePoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Штрих был выпрямлен автоматически (и его можно вернуть свободным).</summary>
    public sealed record StraightenResult(
        IReadOnlyList<StrokePoint> Points, bool WasStraightened, bool WasSnapped);

    /// <summary>Максимальное отклонение от хорды, при котором штрих считается прямым: 2 % длины, но не меньше 1,5 пт.</summary>
    public static double StraightenThreshold(double chordLength) => Math.Max(1.5, chordLength * 0.02);

    /// <summary>Отклонение от 0/90°, в пределах которого линию доводят до идеальной.</summary>
    public const double SnapDegrees = 4.0;

    /// <summary>
    /// Автовыпрямление: если пользователь явно вёл прямую, штрих заменяется
    /// отрезком, а почти горизонтальная/вертикальная линия доводится до
    /// точной. Слишком короткие и явно кривые штрихи не трогаются.
    /// </summary>
    public static StraightenResult AutoStraighten(IReadOnlyList<StrokePoint> points)
    {
        if (points.Count < 3)
            return new StraightenResult(points, false, false);

        var start = points[0];
        var end = points[^1];
        var chord = Distance(start, end);
        if (chord < 10)
            return new StraightenResult(points, false, false); // росчерк, а не линия

        // Замкнутый или сильно петляющий штрих прямой быть не может.
        var pathLength = 0.0;
        for (var i = 1; i < points.Count; i++)
            pathLength += Distance(points[i - 1], points[i]);
        if (pathLength > chord * 1.25)
            return new StraightenResult(points, false, false);

        var threshold = StraightenThreshold(chord);
        foreach (var point in points)
        {
            if (PerpendicularDistance(point, start, end) > threshold)
                return new StraightenResult(points, false, false);
        }

        var (snappedEnd, snapped) = SnapToAxis(start, end);
        return new StraightenResult(new[] { start, snappedEnd }, true, snapped);
    }

    /// <summary>Доводит почти горизонтальную/вертикальную линию до точной.</summary>
    public static (StrokePoint End, bool Snapped) SnapToAxis(StrokePoint start, StrokePoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var degrees = Math.Abs(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        if (degrees > 90) degrees = 180 - degrees;
        if (degrees <= SnapDegrees)
            return (new StrokePoint(end.X, start.Y), true);
        if (degrees >= 90 - SnapDegrees)
            return (new StrokePoint(start.X, end.Y), true);
        return (end, false);
    }

    /// <summary>
    /// Привязка к 45° при удержании Shift. Конец линии — ПРОЕКЦИЯ курсора на
    /// ближайшую ось, а не поворот вектора: при почти горизонтальном движении
    /// линия кончается ровно под курсором, и рука не «промахивается» мимо цели.
    /// </summary>
    public static StrokePoint SnapTo45(StrokePoint start, StrokePoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (dx * dx + dy * dy < 1e-12)
            return end;
        var step = Math.PI / 4;
        var angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
        var ux = Math.Cos(angle);
        var uy = Math.Sin(angle);
        var projection = dx * ux + dy * uy; // длина вдоль выбранной оси
        return new StrokePoint(start.X + ux * projection, start.Y + uy * projection);
    }

    public enum StrokeKind { Pencil, Line, Arrow }

    /// <summary>
    /// Готовый к записи штрих: что ляжет в документ, что вернуть по кнопке
    /// «вернуть свободный штрих» и сработало ли автовыпрямление.
    /// </summary>
    public sealed record StrokeCommit(
        IReadOnlyList<IReadOnlyList<StrokePoint>> Strokes,
        IReadOnlyList<IReadOnlyList<StrokePoint>> FreeStrokes,
        bool WasStraightened);

    /// <summary>
    /// Весь путь от сырых точек мыши до штрихов документа: сглаживание,
    /// выпрямление, прореживание и наконечник стрелки. Возвращает null, если
    /// жест был случайным кликом, а не линией.
    /// </summary>
    public static StrokeCommit? Commit(
        IReadOnlyList<StrokePoint> raw, StrokeKind kind,
        double stabilization, bool autoStraighten, double widthPt)
    {
        if (raw.Count < 2)
            return null;
        if (Distance(raw[0], raw[^1]) < 1 && raw.Count < 4)
            return null;

        if (kind != StrokeKind.Pencil)
        {
            // Линия и стрелка берут только концы жеста.
            var segment = new[] { raw[0], raw[^1] };
            var withHead = AddHead(segment, kind, widthPt);
            return new StrokeCommit(withHead, withHead, false);
        }

        var smooth = Stabilize(raw, stabilization);
        var free = new[] { Simplify(smooth) };

        if (autoStraighten)
        {
            var straightened = AutoStraighten(smooth);
            if (straightened.WasStraightened)
                return new StrokeCommit(new[] { straightened.Points }, free, true);
        }
        return new StrokeCommit(free, free, false);
    }

    private static IReadOnlyList<IReadOnlyList<StrokePoint>> AddHead(
        IReadOnlyList<StrokePoint> body, StrokeKind kind, double widthPt)
    {
        if (kind != StrokeKind.Arrow || body.Count < 2)
            return new[] { body };
        var strokes = new List<IReadOnlyList<StrokePoint>> { body };
        strokes.AddRange(ArrowHead(body[^2], body[^1], widthPt));
        return strokes;
    }

    /// <summary>Длина наконечника стрелки: пропорциональна линии, но в разумных пределах.</summary>
    public static double ArrowHeadLength(double lineLength, double widthPt) =>
        Math.Clamp(Math.Max(lineLength * 0.18, widthPt * 4), 6.0, 28.0);

    /// <summary>
    /// Два усика наконечника стрелки на конце отрезка. Возвращаются
    /// отдельными штрихами: у Ink-аннотации это законный способ нарисовать
    /// стрелку, не завися от того, как просмотрщик рисует /Line.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<StrokePoint>> ArrowHead(
        StrokePoint from, StrokePoint to, double widthPt)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6)
            return Array.Empty<IReadOnlyList<StrokePoint>>();

        var head = ArrowHeadLength(length, widthPt);
        var angle = Math.Atan2(dy, dx);
        const double spread = Math.PI / 7; // ~26°, узкий классический наконечник
        var left = new StrokePoint(
            to.X - Math.Cos(angle - spread) * head,
            to.Y - Math.Sin(angle - spread) * head);
        var right = new StrokePoint(
            to.X - Math.Cos(angle + spread) * head,
            to.Y - Math.Sin(angle + spread) * head);
        return new IReadOnlyList<StrokePoint>[]
        {
            new[] { left, to },
            new[] { right, to },
        };
    }
}
