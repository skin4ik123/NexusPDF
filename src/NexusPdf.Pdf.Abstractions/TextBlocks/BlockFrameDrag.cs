namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>Что под курсором у рамки редактора блока.</summary>
public enum FrameZone { Inside, Border, Handle, Rotate, Outside }

/// <summary>Ручка размера рамки (те же восемь, что у выделенного объекта).</summary>
public enum FrameHandle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>
/// Рамка блока во время набора: край — перенос, квадраты — размер, кружок
/// над рамкой — поворот, внутри — текст. Без экрана: допуски приходят уже
/// в пунктах страницы (экранные точки, делённые на масштаб).
/// </summary>
public static class BlockFrameDrag
{
    private const double SnapDegrees = 3;

    private static readonly FrameHandle[] Handles =
    [
        FrameHandle.TopLeft, FrameHandle.Top, FrameHandle.TopRight, FrameHandle.Right,
        FrameHandle.BottomRight, FrameHandle.Bottom, FrameHandle.BottomLeft, FrameHandle.Left,
    ];

    /// <summary>Центр ручки на рамке (в тех же координатах, что рамка).</summary>
    public static (double X, double Y) CenterOf(OverlayBox frame, FrameHandle handle)
    {
        var (midX, midY) = (frame.XPt + frame.WidthPt / 2, frame.YPt + frame.HeightPt / 2);
        var (right, bottom) = (frame.XPt + frame.WidthPt, frame.YPt + frame.HeightPt);
        return handle switch
        {
            FrameHandle.TopLeft => (frame.XPt, frame.YPt),
            FrameHandle.Top => (midX, frame.YPt),
            FrameHandle.TopRight => (right, frame.YPt),
            FrameHandle.Right => (right, midY),
            FrameHandle.BottomRight => (right, bottom),
            FrameHandle.Bottom => (midX, bottom),
            FrameHandle.BottomLeft => (frame.XPt, bottom),
            FrameHandle.Left => (frame.XPt, midY),
            _ => (midX, midY),
        };
    }

    /// <param name="frameLocal">Рамка в осях блока (без поворота).</param>
    /// <param name="borderPt">Ширина полосы края — поровну внутрь и наружу.</param>
    /// <param name="handlePt">Половина стороны квадрата, в котором ловится ручка.</param>
    /// <param name="rotateOffsetPt">Насколько ручка поворота выше середины верхнего края.</param>
    public static (FrameZone Zone, FrameHandle Handle) HitTest(OverlayBox frameLocal, double x, double y,
        double borderPt, double handlePt, double rotateOffsetPt)
    {
        var top = CenterOf(frameLocal, FrameHandle.Top);
        if (Near(top.X, top.Y - rotateOffsetPt)) return (FrameZone.Rotate, FrameHandle.None);
        foreach (var handle in Handles)
        {
            var (hx, hy) = CenterOf(frameLocal, handle);
            if (Near(hx, hy)) return (FrameZone.Handle, handle);
        }

        var half = borderPt / 2;
        var (left, right) = (frameLocal.XPt, frameLocal.XPt + frameLocal.WidthPt);
        var (upper, lower) = (frameLocal.YPt, frameLocal.YPt + frameLocal.HeightPt);
        var withinOuter = x >= left - half && x <= right + half && y >= upper - half && y <= lower + half;
        if (!withinOuter) return (FrameZone.Outside, FrameHandle.None);
        var withinInner = x > left + half && x < right - half && y > upper + half && y < lower - half;
        return (withinInner ? FrameZone.Inside : FrameZone.Border, FrameHandle.None);

        bool Near(double hx, double hy) => Math.Abs(x - hx) <= handlePt && Math.Abs(y - hy) <= handlePt;
    }

    /// <summary>
    /// Блок после перетаскивания: всегда от блока в начале перетаскивания,
    /// чтобы ошибки округления не копились. Сдвиг и курсор — в отображаемых
    /// пунктах страницы. Размер меняется в осях самого блока; ширина после
    /// этого фиксирована, а высота — только если её тянули. Поворот прямых
    /// углов «притягивает» в пределах 3°.
    /// </summary>
    public static TextBlockOverlay Apply(TextBlockOverlay start, FrameZone zone, FrameHandle handle,
        double dxDisplayed, double dyDisplayed, double pointerXDisplayed, double pointerYDisplayed)
    {
        switch (zone)
        {
            case FrameZone.Border:
                return start with { XPt = start.XPt + dxDisplayed, YPt = start.YPt + dyDisplayed };

            case FrameZone.Rotate:
            {
                var (cx, cy) = (start.XPt + start.WidthPt / 2, start.YPt + start.HeightPt / 2);
                var (px, py) = (pointerXDisplayed - cx, pointerYDisplayed - cy);
                if (Math.Abs(px) < 1e-9 && Math.Abs(py) < 1e-9) return start;
                var degrees = Math.Atan2(px, -py) * 180 / Math.PI;
                var right = Math.Round(degrees / 90) * 90;
                if (Math.Abs(degrees - right) < SnapDegrees) degrees = right;
                return OverlayGeometry.Rotated(start, degrees) as TextBlockOverlay ?? start;
            }

            case FrameZone.Handle when handle != FrameHandle.None:
            {
                // Сдвиг мыши — в оси блока: повёрнутый блок растягивается вдоль своих сторон.
                var angle = -start.RotationDegrees * Math.PI / 180;
                var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
                var (dx, dy) = (dxDisplayed * cos - dyDisplayed * sin, dxDisplayed * sin + dyDisplayed * cos);
                double x = start.XPt, y = start.YPt, width = start.WidthPt, height = start.HeightPt;
                if (handle is FrameHandle.TopLeft or FrameHandle.Left or FrameHandle.BottomLeft) { x += dx; width -= dx; }
                if (handle is FrameHandle.TopRight or FrameHandle.Right or FrameHandle.BottomRight) width += dx;
                if (handle is FrameHandle.TopLeft or FrameHandle.Top or FrameHandle.TopRight) { y += dy; height -= dy; }
                if (handle is FrameHandle.BottomLeft or FrameHandle.Bottom or FrameHandle.BottomRight) height += dy;
                return OverlayGeometry.Resized(start, new OverlayBox(x, y, width, height)) as TextBlockOverlay ?? start;
            }

            default:
                return start;
        }
    }
}
