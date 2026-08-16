using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <summary>
/// Приведение геометрии страницы к тому виду, в каком её видит человек.
///
/// В PDF координаты объектов живут в НЕповёрнутой системе страницы, а
/// /Rotate поворачивает лист только при показе. Экспорт обязан работать с
/// повёрнутым видом: иначе у скана, снятого боком, лист выходит нужного
/// размера, но текст в нём уложен поперёк — строки читаются как колонки, а
/// поля считаются не с тех сторон.
///
/// Четверти здесь — ПО часовой стрелке, как /Rotate в самом PDF.
/// </summary>
public static class PageRotation
{
    public static int Normalize(int quarters) => ((quarters % 4) + 4) % 4;

    /// <summary>Размер после поворота: нечётные четверти меняют стороны местами.</summary>
    public static (double Width, double Height) Size(double rawWidth, double rawHeight, int quarters) =>
        Normalize(quarters) % 2 == 0 ? (rawWidth, rawHeight) : (rawHeight, rawWidth);

    /// <summary>Точка неповёрнутой страницы → точка отображаемой.</summary>
    public static (double X, double Y) Point(double x, double y, int quarters, double rawWidth, double rawHeight) =>
        Normalize(quarters) switch
        {
            1 => (y, rawWidth - x),                     // лист повернули вправо
            2 => (rawWidth - x, rawHeight - y),
            3 => (rawHeight - y, x),                    // лист повернули влево
            _ => (x, y),
        };

    public static PdfTextRect Rect(PdfTextRect rect, int quarters, double rawWidth, double rawHeight)
    {
        if (Normalize(quarters) == 0) return rect;
        var (x1, y1) = Point(rect.Left, rect.Top, quarters, rawWidth, rawHeight);
        var (x2, y2) = Point(rect.Right, rect.Bottom, quarters, rawWidth, rawHeight);
        return new PdfTextRect(Math.Min(x1, x2), Math.Max(y1, y2), Math.Max(x1, x2), Math.Min(y1, y2));
    }

    public static PdfTextWord Word(PdfTextWord word, int quarters, double rawWidth, double rawHeight)
    {
        if (Normalize(quarters) == 0) return word;
        return word with
        {
            RectPt = Rect(word.RectPt, quarters, rawWidth, rawHeight),
            // Поворот листа вычитается из поворота текста: подпись, которая на
            // боковом листе шла снизу вверх, на выпрямленном идёт как обычная
            // строка.
            RotationQuarters = Normalize(word.RotationQuarters - quarters),
        };
    }

    /// <summary>
    /// Линия таблицы. У повёрнутой на четверть страницы горизонтальные
    /// границы становятся вертикальными, поэтому ориентация пересчитывается
    /// по итоговым сторонам, а не переносится как есть.
    /// </summary>
    public static PdfRulingLine Ruling(PdfRulingLine line, int quarters, double rawWidth, double rawHeight)
    {
        if (Normalize(quarters) == 0) return line;

        var half = line.ThicknessPt / 2.0;
        var box = line.IsHorizontal
            ? new PdfTextRect(line.Start, line.Position + half, line.End, line.Position - half)
            : new PdfTextRect(line.Position - half, line.End, line.Position + half, line.Start);
        var moved = Rect(box, quarters, rawWidth, rawHeight);

        var width = moved.Right - moved.Left;
        var height = moved.Top - moved.Bottom;
        return width >= height
            ? new PdfRulingLine(true, (moved.Top + moved.Bottom) / 2.0, moved.Left, moved.Right, Math.Max(height, 0.1))
            : new PdfRulingLine(false, (moved.Left + moved.Right) / 2.0, moved.Bottom, moved.Top, Math.Max(width, 0.1));
    }

    public static PdfFormFieldValue Field(PdfFormFieldValue field, int quarters, double rawWidth, double rawHeight) =>
        Normalize(quarters) == 0 ? field : field with { RectPt = Rect(field.RectPt, quarters, rawWidth, rawHeight) };

    public static PdfPageLink Link(PdfPageLink link, int quarters, double rawWidth, double rawHeight) =>
        Normalize(quarters) == 0 ? link : link with { RectPt = Rect(link.RectPt, quarters, rawWidth, rawHeight) };

    /// <summary>
    /// Картинка поворачивается ЦЕЛИКОМ — и рамка, и пиксели. На боковом скане
    /// вся страница и есть одна картинка: развернуть рамку, забыв про
    /// содержимое, значит положить фотографию в документ набок.
    /// </summary>
    public static PdfPageImage Image(PdfPageImage image, int quarters, double rawWidth, double rawHeight)
    {
        var turns = Normalize(quarters);
        if (turns == 0) return image;

        var (pixels, width, height) = RotatePixels(image.Bgra, image.PixelWidth, image.PixelHeight, turns);
        return new PdfPageImage(pixels, width, height, Rect(image.RectPt, quarters, rawWidth, rawHeight));
    }

    /// <summary>Растр BGRA (строки сверху вниз) на четверть по часовой.</summary>
    public static (byte[] Bgra, int Width, int Height) RotatePixels(
        byte[] bgra, int width, int height, int quarters)
    {
        var turns = Normalize(quarters);
        if (turns == 0 || width <= 0 || height <= 0) return (bgra, width, height);

        var (newWidth, newHeight) = turns % 2 == 0 ? (width, height) : (height, width);
        var result = new byte[(long)newWidth * newHeight * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (nx, ny) = turns switch
                {
                    1 => (height - 1 - y, x),
                    2 => (width - 1 - x, height - 1 - y),
                    _ => (y, width - 1 - x),
                };
                var from = (y * width + x) * 4;
                var to = (ny * newWidth + nx) * 4;
                result[to] = bgra[from];
                result[to + 1] = bgra[from + 1];
                result[to + 2] = bgra[from + 2];
                result[to + 3] = bgra[from + 3];
            }
        }
        return (result, newWidth, newHeight);
    }

    public static PdfAnnotationInfo Annotation(PdfAnnotationInfo note, int quarters, double rawWidth, double rawHeight) =>
        Normalize(quarters) == 0 || note.RectPt == null
            ? note
            : note with { RectPt = Rect(note.RectPt, quarters, rawWidth, rawHeight) };
}
