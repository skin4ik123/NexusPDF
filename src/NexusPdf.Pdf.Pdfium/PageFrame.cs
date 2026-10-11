using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Рамка страницы для перевода координат между содержимым (начало — левый
/// нижний угол CropBox∩MediaBox, Y вверх) и отображением (левый верхний угол
/// повёрнутой страницы, Y вниз). Та же геометрия, что в запекании оверлеев:
/// координата, прочитанная отсюда, обязана совпасть с тем, куда запекание
/// положит точку.
/// </summary>
internal readonly record struct PageFrame(
    int Rotation, double Left, double Bottom, double ContentWidth, double ContentHeight)
{
    public double DisplayWidth => Rotation % 2 == 0 ? ContentWidth : ContentHeight;
    public double DisplayHeight => Rotation % 2 == 0 ? ContentHeight : ContentWidth;

    /// <param name="extraQuarterTurns">Добавочный поворот показа поверх /Rotate страницы.</param>
    public static PageFrame Of(FpdfPageT page, int extraQuarterTurns)
    {
        var rotation = ((fpdf_edit.FPDFPageGetRotation(page) + extraQuarterTurns) % 4 + 4) % 4;

        float left = 0, bottom = 0, right = 0, top = 0;
        var hasBox = fpdf_transformpage.FPDFPageGetCropBox(page, ref left, ref bottom, ref right, ref top) != 0;
        float mLeft = 0, mBottom = 0, mRight = 0, mTop = 0;
        var hasMedia = fpdf_transformpage.FPDFPageGetMediaBox(page, ref mLeft, ref mBottom, ref mRight, ref mTop) != 0;
        if (hasBox && hasMedia)
        {
            left = Math.Max(left, mLeft);
            bottom = Math.Max(bottom, mBottom);
            right = Math.Min(right, mRight);
            top = Math.Min(top, mTop);
        }
        else if (!hasBox && hasMedia)
        {
            (left, bottom, right, top) = (mLeft, mBottom, mRight, mTop);
        }
        else if (!hasBox)
        {
            left = 0;
            bottom = 0;
            right = (float)fpdfview.FPDF_GetPageWidthF(page);
            top = (float)fpdfview.FPDF_GetPageHeightF(page);
            if (fpdf_edit.FPDFPageGetRotation(page) % 2 != 0)
                (right, top) = (top, right); // размер отдан в отображаемой ориентации
        }
        return new PageFrame(rotation, left, bottom, right - left, top - bottom);
    }

    /// <summary>Точка содержимого (абсолютные координаты страницы) → отображаемые пункты.</summary>
    public (double X, double Y) ToDisplayed(double x, double y)
    {
        var cx = x - Left;
        var cy = y - Bottom;
        return Rotation switch
        {
            1 => (cy, cx),
            2 => (ContentWidth - cx, cy),
            3 => (ContentHeight - cy, ContentWidth - cx),
            _ => (cx, ContentHeight - cy),
        };
    }

    /// <summary>Отображаемые пункты → точка содержимого (абсолютные координаты страницы).</summary>
    public (double X, double Y) ToContent(double dx, double dy)
    {
        var (cx, cy) = Rotation switch
        {
            1 => (dy, dx),
            2 => (ContentWidth - dx, dy),
            3 => (ContentWidth - dy, ContentHeight - dx),
            _ => (dx, ContentHeight - dy),
        };
        return (Left + cx, Bottom + cy);
    }
}
