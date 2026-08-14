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
            case OcrTextLayerOverlay ocr:
            {
                // Каждое слово переносится как текстовый оверлей: базовая
                // линия в целевую рамку, сам текст доворачивается на -90°·delta
                // (слова должны остаться поверх повернувшихся глифов скана).
                var words = new List<OcrWordBox>(ocr.Words.Count);
                foreach (var word in ocr.Words)
                {
                    var baseline = RemapPoint(
                        word.XPt, word.YPt + word.HeightPt * TextBaselineFactor,
                        delta, finalWidth, finalHeight);
                    words.Add(word with
                    {
                        XPt = baseline.X,
                        YPt = baseline.Y - word.HeightPt * TextBaselineFactor,
                    });
                }
                return (ocr with { Words = words }, -90.0 * delta);
            }
            default:
                return (overlay, 0);
        }
    }
}
