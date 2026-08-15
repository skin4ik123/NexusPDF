namespace NexusPdf.Pdf.Abstractions;

/// <summary>Прямоугольник в отображаемых пунктах страницы (начало — левый верхний угол).</summary>
public readonly record struct OverlayBox(double XPt, double YPt, double WidthPt, double HeightPt)
{
    public double Right => XPt + WidthPt;
    public double Bottom => YPt + HeightPt;

    public bool Contains(double x, double y) =>
        x >= XPt && x <= Right && y >= YPt && y <= Bottom;

    public OverlayBox Inflated(double margin) =>
        new(XPt - margin, YPt - margin, WidthPt + margin * 2, HeightPt + margin * 2);

    public override string ToString() => $"{XPt:F1};{YPt:F1} {WidthPt:F1}×{HeightPt:F1}";
}

/// <summary>Что можно делать с наложенным объектом мышью.</summary>
public readonly record struct OverlayAbilities(bool CanSelect, bool CanMove, bool CanResize)
{
    public static readonly OverlayAbilities None = new(false, false, false);
    public static readonly OverlayAbilities SelectOnly = new(true, false, false);
    public static readonly OverlayAbilities MoveOnly = new(true, true, false);
    public static readonly OverlayAbilities Full = new(true, true, true);
}

/// <summary>
/// Рамки наложенных объектов: где объект лежит, можно ли его двигать и
/// растягивать, и как выглядит результат перетаскивания.
///
/// Живёт рядом с моделью, а не в окне, по той же причине, что и
/// <see cref="OverlayDisplayMapper"/>: рамка на экране и то, что окажется в
/// файле, обязаны считаться одним кодом.
/// </summary>
public static class OverlayGeometry
{
    /// <summary>
    /// Ширина одного знака как доля кегля. Точную ширину даёт только шрифт, а
    /// рамка нужна ещё до отрисовки — поэтому надпись можно двигать, но не
    /// растягивать: иначе рамка врала бы о результате.
    /// </summary>
    private const double AverageGlyphWidthFactor = 0.5;

    /// <summary>Высота строки надписи относительно кегля.</summary>
    private const double LineHeightFactor = 1.2;

    /// <summary>Наименьший размер объекта — меньше него не ухватить мышью.</summary>
    public const double MinimumSizePt = 4;

    public static OverlayAbilities AbilitiesOf(PageOverlay overlay) => overlay switch
    {
        ImageOverlay => OverlayAbilities.Full,
        ShapeAnnotationDraft => OverlayAbilities.Full,
        RedactionDraft => OverlayAbilities.Full,
        InkAnnotationDraft => OverlayAbilities.Full,
        TextOverlay => OverlayAbilities.MoveOnly,
        NoteAnnotationDraft => OverlayAbilities.MoveOnly,
        // Разметка привязана к строкам текста: сдвинуть её отдельно от текста
        // нельзя — она перестанет быть разметкой этого текста. Выделить и
        // удалить можно.
        TextMarkupDraft => OverlayAbilities.SelectOnly,
        _ => OverlayAbilities.None,
    };

    /// <summary>Рамка объекта или null, если у него её нет (правки на всю страницу).</summary>
    public static OverlayBox? BoundsOf(PageOverlay overlay) => overlay switch
    {
        TextOverlay text => new OverlayBox(
            text.XPt, text.YPt,
            Math.Max(MinimumSizePt, text.Text.Length * text.FontSizePt * AverageGlyphWidthFactor),
            text.FontSizePt * LineHeightFactor),

        ImageOverlay image => new OverlayBox(image.XPt, image.YPt, image.WidthPt, image.HeightPt),

        NoteAnnotationDraft note => new OverlayBox(
            note.XPt, note.YPt, OverlayDisplayMapper.NoteIconSizePt, OverlayDisplayMapper.NoteIconSizePt),

        ShapeAnnotationDraft shape => new OverlayBox(shape.XPt, shape.YPt, shape.WidthPt, shape.HeightPt),

        RedactionDraft redaction => new OverlayBox(
            redaction.XPt, redaction.YPt, redaction.WidthPt, redaction.HeightPt),

        InkAnnotationDraft ink => InkBounds(ink),

        TextMarkupDraft markup => MarkupBounds(markup),

        _ => null,
    };

    /// <summary>Рамка по самим точкам штрихов, без запаса на толщину линии.</summary>
    private static OverlayBox? StrokeBounds(InkAnnotationDraft ink)
    {
        double left = double.MaxValue, top = double.MaxValue;
        double right = double.MinValue, bottom = double.MinValue;
        foreach (var stroke in ink.Strokes)
        {
            foreach (var point in stroke)
            {
                left = Math.Min(left, point.XPt);
                right = Math.Max(right, point.XPt);
                top = Math.Min(top, point.YPt);
                bottom = Math.Max(bottom, point.YPt);
            }
        }
        return right < left ? null : new OverlayBox(left, top, right - left, bottom - top);
    }

    private static double InkPad(InkAnnotationDraft ink) => Math.Max(ink.WidthPt, 1) / 2;

    private static OverlayBox? InkBounds(InkAnnotationDraft ink)
    {
        if (StrokeBounds(ink) is not { } points) return null;
        // Рамка с запасом на толщину линии: иначе штрих торчит из выделения.
        var pad = InkPad(ink);
        return new OverlayBox(points.XPt - pad, points.YPt - pad,
            Math.Max(MinimumSizePt, points.WidthPt + pad * 2),
            Math.Max(MinimumSizePt, points.HeightPt + pad * 2));
    }

    private static OverlayBox? MarkupBounds(TextMarkupDraft markup)
    {
        if (markup.Rects.Count == 0) return null;
        var left = markup.Rects.Min(r => r.XPt);
        var top = markup.Rects.Min(r => r.YPt);
        var right = markup.Rects.Max(r => r.XPt + r.WidthPt);
        var bottom = markup.Rects.Max(r => r.YPt + r.HeightPt);
        return new OverlayBox(left, top, right - left, bottom - top);
    }

    /// <summary>Сдвиг объекта на указанное расстояние; null — объект не двигается.</summary>
    public static PageOverlay? Moved(PageOverlay overlay, double dx, double dy)
    {
        if (!AbilitiesOf(overlay).CanMove) return null;
        return overlay switch
        {
            TextOverlay text => text with { XPt = text.XPt + dx, YPt = text.YPt + dy },
            ImageOverlay image => image with { XPt = image.XPt + dx, YPt = image.YPt + dy },
            NoteAnnotationDraft note => note with { XPt = note.XPt + dx, YPt = note.YPt + dy },
            ShapeAnnotationDraft shape => shape with { XPt = shape.XPt + dx, YPt = shape.YPt + dy },
            RedactionDraft redaction => redaction with
            {
                XPt = redaction.XPt + dx, YPt = redaction.YPt + dy,
            },
            InkAnnotationDraft ink => ink with
            {
                Strokes = ink.Strokes
                    .Select(s => (IReadOnlyList<InkPoint>)s
                        .Select(p => new InkPoint(p.XPt + dx, p.YPt + dy)).ToList())
                    .ToList(),
            },
            _ => null,
        };
    }

    /// <summary>
    /// Объект, вписанный в новую рамку; null — объект не растягивается.
    /// Рамка приводится к неотрицательным размерам: тянуть можно в любую
    /// сторону, а хранить вывернутый прямоугольник нельзя.
    /// </summary>
    public static PageOverlay? Resized(PageOverlay overlay, OverlayBox box)
    {
        if (!AbilitiesOf(overlay).CanResize) return null;

        var normalized = Normalize(box);
        return overlay switch
        {
            ImageOverlay image => image with
            {
                XPt = normalized.XPt, YPt = normalized.YPt,
                WidthPt = normalized.WidthPt, HeightPt = normalized.HeightPt,
            },
            ShapeAnnotationDraft shape => shape with
            {
                XPt = normalized.XPt, YPt = normalized.YPt,
                WidthPt = normalized.WidthPt, HeightPt = normalized.HeightPt,
            },
            RedactionDraft redaction => redaction with
            {
                XPt = normalized.XPt, YPt = normalized.YPt,
                WidthPt = normalized.WidthPt, HeightPt = normalized.HeightPt,
            },
            InkAnnotationDraft ink => ScaleInk(ink, normalized),
            _ => null,
        };
    }

    private static PageOverlay? ScaleInk(InkAnnotationDraft ink, OverlayBox targetBounds)
    {
        if (StrokeBounds(ink) is not { } source)
            return null;

        // Рамка объекта включает запас на толщину линии, поэтому целиться надо
        // не в неё, а в её внутреннюю часть: иначе рисунок каждый раз чуть-чуть
        // не доезжает до рамки, за которую его тянули.
        var pad = InkPad(ink);
        var target = new OverlayBox(
            targetBounds.XPt + pad, targetBounds.YPt + pad,
            Math.Max(0.01, targetBounds.WidthPt - pad * 2),
            Math.Max(0.01, targetBounds.HeightPt - pad * 2));

        // Штрих в одну точку или строго вертикальная линия: масштабировать
        // нечего, но и терять рисунок нельзя — он просто переезжает.
        var scaleX = source.WidthPt > 0.001 ? target.WidthPt / source.WidthPt : 1;
        var scaleY = source.HeightPt > 0.001 ? target.HeightPt / source.HeightPt : 1;
        return ink with
        {
            Strokes = ink.Strokes
                .Select(s => (IReadOnlyList<InkPoint>)s
                    .Select(p => new InkPoint(
                        target.XPt + (p.XPt - source.XPt) * scaleX,
                        target.YPt + (p.YPt - source.YPt) * scaleY))
                    .ToList())
                .ToList(),
        };
    }

    /// <summary>Приводит рамку к неотрицательным размерам не меньше минимального.</summary>
    public static OverlayBox Normalize(OverlayBox box)
    {
        var x = Math.Min(box.XPt, box.XPt + box.WidthPt);
        var y = Math.Min(box.YPt, box.YPt + box.HeightPt);
        var width = Math.Max(MinimumSizePt, Math.Abs(box.WidthPt));
        var height = Math.Max(MinimumSizePt, Math.Abs(box.HeightPt));
        return new OverlayBox(x, y, width, height);
    }
}
