namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Пересчёт координат оверлея из рамки, в которой он был размещён
/// (PlacedRotation), в текущую отображаемую рамку страницы. Используется и
/// движком при запекании, и предпросмотром в интерфейсе — чтобы экран и
/// результат сохранения совпадали по одному и тому же коду.
/// </summary>
public static class OverlayDisplayMapper
{
    public const double NoteIconSizePt = 20;
    public const double TextBaselineFactor = 0.75;

    /// <summary>Перевод точки из рамки размещения в целевую рамку (страницу довернули на delta четвертей по часовой).</summary>
    public static (double X, double Y) RemapPoint(
        double x, double y, int delta, double finalWidth, double finalHeight)
    {
        var (w, h) = delta % 2 == 0 ? (finalWidth, finalHeight) : (finalHeight, finalWidth);
        for (var i = 0; i < delta; i++)
        {
            (x, y) = (h - y, x);
            (w, h) = (h, w);
        }
        return (x, y);
    }

    /// <summary>
    /// Возвращает оверлей с координатами в целевой рамке и добавочный угол
    /// (для изображений, у которых нет собственного поля поворота).
    /// </summary>
    public static (PageOverlay Overlay, double ExtraAngleDeg) ToFrame(
        PageOverlay overlay, int currentRotationOffset, double finalWidth, double finalHeight)
    {
        var delta = ((currentRotationOffset - overlay.PlacedRotation) % 4 + 4) % 4;
        if (delta == 0)
            return (overlay, 0);

        switch (overlay)
        {
            case TextOverlay text:
            {
                // Переносится базовая линия; анкер восстанавливается так, чтобы
                // стандартный сдвиг +0.75·fs в целевой рамке попал в ту же точку.
                var baseline = RemapPoint(
                    text.XPt, text.YPt + text.FontSizePt * TextBaselineFactor,
                    delta, finalWidth, finalHeight);
                return (text with
                {
                    XPt = baseline.X,
                    YPt = baseline.Y - text.FontSizePt * TextBaselineFactor,
                    RotationDegrees = text.RotationDegrees - 90.0 * delta,
                }, 0);
            }
            case ImageOverlay image:
            {
                var center = RemapPoint(
                    image.XPt + image.WidthPt / 2, image.YPt + image.HeightPt / 2,
                    delta, finalWidth, finalHeight);
                return (image with
                {
                    XPt = center.X - image.WidthPt / 2,
                    YPt = center.Y - image.HeightPt / 2,
                }, -90.0 * delta);
            }
            case NoteAnnotationDraft note:
            {
                // Значок — фиксированная рамка 20×20: переносится её центр,
                // иначе после поворота значок съезжал бы на свой размер.
                var center = RemapPoint(
                    note.XPt + NoteIconSizePt / 2, note.YPt + NoteIconSizePt / 2,
                    delta, finalWidth, finalHeight);
                return (note with
                {
                    XPt = center.X - NoteIconSizePt / 2,
                    YPt = center.Y - NoteIconSizePt / 2,
                }, 0);
            }
            case RegionEraseDraft erase:
            {
                var e1 = RemapPoint(erase.XPt, erase.YPt, delta, finalWidth, finalHeight);
                var e2 = RemapPoint(
                    erase.XPt + erase.WidthPt, erase.YPt + erase.HeightPt,
                    delta, finalWidth, finalHeight);
                return (erase with
                {
                    XPt = Math.Min(e1.X, e2.X),
                    YPt = Math.Min(e1.Y, e2.Y),
                    WidthPt = Math.Abs(e2.X - e1.X),
                    HeightPt = Math.Abs(e2.Y - e1.Y),
                }, 0);
            }
            case RedactionDraft redaction:
            {
                var p1 = RemapPoint(redaction.XPt, redaction.YPt, delta, finalWidth, finalHeight);
                var p2 = RemapPoint(
                    redaction.XPt + redaction.WidthPt, redaction.YPt + redaction.HeightPt,
                    delta, finalWidth, finalHeight);
                return (redaction with
                {
                    XPt = Math.Min(p1.X, p2.X),
                    YPt = Math.Min(p1.Y, p2.Y),
                    WidthPt = Math.Abs(p2.X - p1.X),
                    HeightPt = Math.Abs(p2.Y - p1.Y),
                }, 0);
            }
            case ShapeAnnotationDraft shape:
            {
                var p1 = RemapPoint(shape.XPt, shape.YPt, delta, finalWidth, finalHeight);
                var p2 = RemapPoint(
                    shape.XPt + shape.WidthPt, shape.YPt + shape.HeightPt,
                    delta, finalWidth, finalHeight);
                return (shape with
                {
                    XPt = Math.Min(p1.X, p2.X),
                    YPt = Math.Min(p1.Y, p2.Y),
                    WidthPt = Math.Abs(p2.X - p1.X),
                    HeightPt = Math.Abs(p2.Y - p1.Y),
                }, 0);
            }
            case TextMarkupDraft markup:
            {
                // Каждая строка выделения переносится своей рамкой: разметка
                // из нескольких строк не должна схлопываться в один блок.
                var rects = new List<TextMarkupRect>(markup.Rects.Count);
                foreach (var rect in markup.Rects)
                {
                    var p1 = RemapPoint(rect.XPt, rect.YPt, delta, finalWidth, finalHeight);
                    var p2 = RemapPoint(
                        rect.XPt + rect.WidthPt, rect.YPt + rect.HeightPt,
                        delta, finalWidth, finalHeight);
                    rects.Add(new TextMarkupRect(
                        Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
                        Math.Abs(p2.X - p1.X), Math.Abs(p2.Y - p1.Y)));
                }
                return (markup with { Rects = rects }, 0);
            }
            case InkAnnotationDraft ink:
            {
                // У рисунка нет рамки — переносится каждая точка штриха.
                var strokes = new List<IReadOnlyList<InkPoint>>(ink.Strokes.Count);
                foreach (var stroke in ink.Strokes)
                {
                    var moved = new List<InkPoint>(stroke.Count);
                    foreach (var point in stroke)
                    {
                        var (x, y) = RemapPoint(point.XPt, point.YPt, delta, finalWidth, finalHeight);
                        moved.Add(new InkPoint(x, y));
                    }
                    strokes.Add(moved);
                }
                return (ink with { Strokes = strokes }, 0);
            }
            case OcrEditableTextOverlay editable:
            {
                var lines = new List<OcrTextLine>(editable.Lines.Count);
                foreach (var line in editable.Lines)
                {
                    var anchor = RemapPoint(
                        line.XPt, line.YPt + line.HeightPt, delta, finalWidth, finalHeight);
                    // Заплатка живёт по своему прямоугольнику (он обрезан по
                    // буквам), поэтому переносится отдельно от рамки строки.
                    var patch = line.Patch;
                    if (patch != null)
                    {
                        var corner = RemapPoint(
                            patch.XPt, patch.YPt + patch.HeightPt, delta, finalWidth, finalHeight);
                        patch = patch with { XPt = corner.X, YPt = corner.Y - patch.HeightPt };
                    }
                    lines.Add(line with
                    {
                        XPt = anchor.X,
                        YPt = anchor.Y - line.HeightPt,
                        Patch = patch,
                    });
                }
                return (editable with { Lines = lines }, -90.0 * delta);
            }
            case OcrTextLayerOverlay ocr:
            {
                // Переносится якорь запекания — левый нижний угол рамки слова
                // (ровно та же точка, что использует ApplyOcrLayer); сам текст
                // доворачивается на -90°·delta, чтобы остаться поверх
                // повернувшихся глифов скана.
                var words = new List<OcrWordBox>(ocr.Words.Count);
                foreach (var word in ocr.Words)
                {
                    var anchor = RemapPoint(
                        word.XPt, word.YPt + word.HeightPt,
                        delta, finalWidth, finalHeight);
                    words.Add(word with
                    {
                        XPt = anchor.X,
                        YPt = anchor.Y - word.HeightPt,
                    });
                }
                return (ocr with { Words = words }, -90.0 * delta);
            }
            default:
                return (overlay, 0);
        }
    }
}
