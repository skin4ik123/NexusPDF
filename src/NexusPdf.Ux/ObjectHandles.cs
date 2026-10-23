namespace NexusPdf.Ux;

/// <summary>Ручка рамки выделения.</summary>
public enum ResizeHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,

    /// <summary>Внутри рамки — перетаскивание целиком.</summary>
    Move,

    /// <summary>Ручка над серединой верхней стороны — поворот вокруг центра.</summary>
    Rotate,
}

/// <summary>Прямоугольник для расчётов рамки (в отображаемых пунктах страницы).</summary>
public readonly record struct HandleBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Рамка выделенного объекта: где её ручки, какая ручка под курсором и во что
/// превращается рамка при перетаскивании.
///
/// Всё это чистый расчёт и живёт отдельно от окна: именно здесь ошибка даёт
/// «объект прыгает при захвате» и «рамка выворачивается наизнанку», и ловить
/// такое глазами дорого.
/// </summary>
public static class ObjectHandles
{
    /// <summary>Сторона квадратика ручки на экране, в точках.</summary>
    public const double HandleSizeDip = 9;

    /// <summary>
    /// Допуск попадания в ручку. Ручка маленькая, а промах по ней означает
    /// перетаскивание вместо растягивания — то есть испорченный объект.
    /// </summary>
    public const double HandleToleranceDip = 6;

    /// <summary>На сколько точек экрана ручка поворота выше верхней ручки.</summary>
    public const double RotateHandleOffsetDip = 20;

    /// <summary>Все восемь ручек по порядку обхода рамки.</summary>
    public static readonly IReadOnlyList<ResizeHandle> All = new[]
    {
        ResizeHandle.TopLeft, ResizeHandle.Top, ResizeHandle.TopRight,
        ResizeHandle.Right, ResizeHandle.BottomRight, ResizeHandle.Bottom,
        ResizeHandle.BottomLeft, ResizeHandle.Left,
    };

    /// <summary>
    /// Центр ручки в координатах страницы. <paramref name="scale"/> — точек
    /// страницы в одной точке экрана: ручка поворота отстоит на экранное расстояние.
    /// </summary>
    public static (double X, double Y) CenterOf(HandleBox box, ResizeHandle handle, double scale = 1.0)
    {
        var midX = box.X + box.Width / 2;
        var midY = box.Y + box.Height / 2;
        return handle switch
        {
            ResizeHandle.TopLeft => (box.X, box.Y),
            ResizeHandle.Top => (midX, box.Y),
            ResizeHandle.TopRight => (box.Right, box.Y),
            ResizeHandle.Right => (box.Right, midY),
            ResizeHandle.BottomRight => (box.Right, box.Bottom),
            ResizeHandle.Bottom => (midX, box.Bottom),
            ResizeHandle.BottomLeft => (box.X, box.Bottom),
            ResizeHandle.Left => (box.X, midY),
            ResizeHandle.Rotate => (midX, box.Y - RotateHandleOffsetDip * (scale > 0 ? scale : 1.0)),
            _ => (midX, midY),
        };
    }

    /// <summary>
    /// Что под курсором: ручка, тело объекта или ничего.
    /// <paramref name="scale"/> — точек страницы в одной точке экрана, чтобы
    /// допуск оставался экранным при любом масштабе показа.
    /// </summary>
    public static ResizeHandle HitTest(
        HandleBox box, double x, double y, bool canResize, double scale = 1.0, bool canRotate = false)
    {
        var tolerance = HandleToleranceDip * (scale > 0 ? scale : 1.0);

        if (canRotate)
        {
            var (rx, ry) = CenterOf(box, ResizeHandle.Rotate, scale);
            if (Math.Abs(x - rx) <= tolerance && Math.Abs(y - ry) <= tolerance)
                return ResizeHandle.Rotate;
        }

        if (canResize)
        {
            foreach (var handle in All)
            {
                var (hx, hy) = CenterOf(box, handle);
                if (Math.Abs(x - hx) <= tolerance && Math.Abs(y - hy) <= tolerance)
                    return handle;
            }
        }

        return x >= box.X - tolerance && x <= box.Right + tolerance &&
               y >= box.Y - tolerance && y <= box.Bottom + tolerance
            ? ResizeHandle.Move
            : ResizeHandle.None;
    }

    /// <summary>
    /// Рамка после перетаскивания ручки. Тянуть можно через противоположную
    /// сторону: рамка при этом выворачивается, и её приводит в порядок
    /// нормализация на стороне модели.
    /// </summary>
    public static HandleBox Drag(HandleBox box, ResizeHandle handle, double dx, double dy)
    {
        double x = box.X, y = box.Y, width = box.Width, height = box.Height;

        switch (handle)
        {
            case ResizeHandle.Move:
                return new HandleBox(x + dx, y + dy, width, height);

            case ResizeHandle.TopLeft:
                x += dx; y += dy; width -= dx; height -= dy; break;
            case ResizeHandle.Top:
                y += dy; height -= dy; break;
            case ResizeHandle.TopRight:
                y += dy; width += dx; height -= dy; break;
            case ResizeHandle.Right:
                width += dx; break;
            case ResizeHandle.BottomRight:
                width += dx; height += dy; break;
            case ResizeHandle.Bottom:
                height += dy; break;
            case ResizeHandle.BottomLeft:
                x += dx; width -= dx; height += dy; break;
            case ResizeHandle.Left:
                x += dx; width -= dx; break;
            default:
                return box;
        }
        return new HandleBox(x, y, width, height);
    }

    /// <summary>
    /// Угол поворота, на который указывает курсор: градусы по часовой стрелке
    /// от направления «вверх» из центра рамки, в [0, 360).
    /// </summary>
    public static double RotationAngle(HandleBox box, double x, double y)
    {
        var dx = x - (box.X + box.Width / 2);
        var dy = y - (box.Y + box.Height / 2);
        var degrees = Math.Atan2(dx, -dy) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    /// <summary>Курсор для ручки — подсказка о том, куда потянется рамка.</summary>
    public static string CursorFor(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => "SizeNWSE",
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => "SizeNESW",
        ResizeHandle.Top or ResizeHandle.Bottom => "SizeNS",
        ResizeHandle.Left or ResizeHandle.Right => "SizeWE",
        ResizeHandle.Move => "SizeAll",
        ResizeHandle.Rotate => "Hand",
        _ => "Arrow",
    };
}

/// <summary>
/// Привязка к сетке и направляющим. Нужна ровно для одного: поставить два
/// объекта на одну линию мышью без привязки практически невозможно.
/// </summary>
public static class Snapping
{
    /// <summary>Шаг сетки по умолчанию — 10 пунктов (около 3,5 мм).</summary>
    public const double DefaultGridPt = 10;

    /// <summary>Расстояние, с которого объект «прилипает» к линии.</summary>
    public const double SnapDistancePt = 6;

    /// <summary>
    /// Ближайший узел сетки, если он ближе порога.
    ///
    /// Порог ограничен третью шага намеренно: если он больше половины шага,
    /// прилипает ЛЮБОЕ положение и поставить объект между узлами становится
    /// невозможно — сетка из помощи превращается в клетку.
    /// </summary>
    public static double ToGrid(double value, double step)
    {
        if (!(step > 0)) return value;
        var limit = Math.Min(SnapDistancePt, step / 3);
        var snapped = Math.Round(value / step) * step;
        return Math.Abs(snapped - value) <= limit ? snapped : value;
    }

    /// <summary>Ближайшая направляющая, если она ближе порога.</summary>
    public static double ToGuides(double value, IReadOnlyList<double> guides)
    {
        var best = value;
        var bestDistance = SnapDistancePt;
        foreach (var guide in guides)
        {
            var distance = Math.Abs(guide - value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = guide;
            }
        }
        return best;
    }

    /// <summary>
    /// Привязка рамки: сначала направляющие (их ставит пользователь осознанно),
    /// потом сетка. Привязывается ближайший край — за него объект и «цепляется».
    /// </summary>
    public static HandleBox Apply(
        HandleBox box,
        bool useGrid, double gridStepPt,
        IReadOnlyList<double> verticalGuides,
        IReadOnlyList<double> horizontalGuides)
    {
        var x = ToGuides(box.X, verticalGuides);
        var y = ToGuides(box.Y, horizontalGuides);

        if (Math.Abs(x - box.X) < double.Epsilon && useGrid)
            x = ToGrid(box.X, gridStepPt);
        if (Math.Abs(y - box.Y) < double.Epsilon && useGrid)
            y = ToGrid(box.Y, gridStepPt);

        return new HandleBox(x, y, box.Width, box.Height);
    }
}
