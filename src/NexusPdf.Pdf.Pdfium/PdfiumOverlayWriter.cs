using System.Runtime.InteropServices;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Запекание наложенного контента (новый текст, изображения) в страницы
/// компонуемого документа. Координаты оверлеев заданы в отображаемой
/// ориентации страницы (от левого верхнего угла); здесь они переводятся в
/// систему координат содержимого с учётом итогового /Rotate.
/// Текст пишется TTF-шрифтом из системы (Segoe UI) как CID — кириллица,
/// латиница и смешанные строки встраиваются подмножеством шрифта.
/// </summary>
internal static class PdfiumOverlayWriter
{
    private const int FpdfFontTrueType = 2;
    private const int FpdfBitmapBgra = 4;

    /// <summary>Загружает системный TTF в документ. null — если ни один кандидат не найден.</summary>
    public static unsafe FpdfFontT? LoadOverlayFont(FpdfDocumentT document)
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var candidate in new[] { "segoeui.ttf", "arial.ttf", "tahoma.ttf", "calibri.ttf" })
        {
            var path = Path.Combine(fontsDir, candidate);
            if (!File.Exists(path))
                continue;
            var bytes = File.ReadAllBytes(path);
            fixed (byte* data = bytes)
            {
                var font = fpdf_edit.FPDFTextLoadFont(document, data, (uint)bytes.Length, FpdfFontTrueType, 1);
                if (font != null && font.__Instance != IntPtr.Zero)
                    return font;
            }
        }
        return null;
    }

    public static void ApplyOverlays(
        FpdfDocumentT document, FpdfPageT page, FpdfFontT? font,
        IReadOnlyList<PageOverlay> overlays, int extraQuarterTurns)
    {
        var rotation = ((fpdf_edit.FPDFPageGetRotation(page) % 4) + 4) % 4;

        // Экран (клик, предпросмотр, рендер) живёт в рамке CropBox∩MediaBox —
        // именно её и берём; MediaBox используется только как запасной вариант.
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
            if (rotation % 2 == 1)
                (right, top) = (top, right); // ширина/высота были отданы в отображаемой ориентации
        }
        var contentWidth = right - left;
        var contentHeight = top - bottom;

        // Итоговая отображаемая рамка.
        var displayWidth = rotation % 2 == 0 ? contentWidth : contentHeight;
        var displayHeight = rotation % 2 == 0 ? contentHeight : contentWidth;

        foreach (var overlay in overlays)
        {
            var delta = ((extraQuarterTurns - overlay.PlacedRotation) % 4 + 4) % 4;
            switch (overlay)
            {
                case TextOverlay text:
                    ApplyText(document, page, font,
                        RemapText(text, delta, displayWidth, displayHeight),
                        rotation, left, bottom, contentWidth, contentHeight);
                    break;
                case ImageOverlay image:
                {
                    var (remapped, extraAngle) = RemapImage(image, delta, displayWidth, displayHeight);
                    ApplyImage(document, page, remapped, extraAngle,
                        rotation, left, bottom, contentWidth, contentHeight);
                    break;
                }
            }
        }

        if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
            throw new PdfEngineException("Не удалось сгенерировать содержимое страницы с наложенным контентом.");
    }

    /// <summary>Перевод точки из рамки размещения в итоговую отображаемую рамку (страницу довернули на delta четвертей).</summary>
    private static (double X, double Y) RemapPoint(
        double x, double y, int delta, double finalWidth, double finalHeight)
    {
        // Рамка размещения: при нечётной delta её стороны переставлены
        // относительно итоговой.
        var (w, h) = delta % 2 == 0 ? (finalWidth, finalHeight) : (finalHeight, finalWidth);
        for (var i = 0; i < delta; i++)
        {
            (x, y) = (h - y, x);
            (w, h) = (h, w);
        }
        return (x, y);
    }

    private static TextOverlay RemapText(TextOverlay text, int delta, double finalWidth, double finalHeight)
    {
        if (delta == 0) return text;
        // Базовая линия вычисляется в рамке размещения, переносится в итоговую
        // рамку, затем анкер восстанавливается так, чтобы стандартный сдвиг
        // +0.75·fs в итоговой рамке попал в ту же точку.
        var baseline = RemapPoint(text.XPt, text.YPt + text.FontSizePt * 0.75, delta, finalWidth, finalHeight);
        return text with
        {
            XPt = baseline.X,
            YPt = baseline.Y - text.FontSizePt * 0.75,
            RotationDegrees = text.RotationDegrees - 90.0 * delta,
        };
    }

    private static (ImageOverlay Overlay, double ExtraAngleDeg) RemapImage(
        ImageOverlay image, int delta, double finalWidth, double finalHeight)
    {
        if (delta == 0) return (image, 0);
        var center = RemapPoint(
            image.XPt + image.WidthPt / 2, image.YPt + image.HeightPt / 2,
            delta, finalWidth, finalHeight);
        var remapped = image with
        {
            XPt = center.X - image.WidthPt / 2,
            YPt = center.Y - image.HeightPt / 2,
        };
        return (remapped, -90.0 * delta);
    }

    /// <summary>Перевод точки из отображаемых координат (сверху-слева, y вниз) в координаты содержимого (y вверх).</summary>
    private static (double X, double Y) DisplayedToContent(
        double dx, double dy, int rotation, double contentWidth, double contentHeight)
    {
        return rotation switch
        {
            1 => (dy, dx),
            2 => (contentWidth - dx, dy),
            3 => (contentWidth - dy, contentHeight - dx),
            _ => (dx, contentHeight - dy),
        };
    }

    private static void ApplyText(
        FpdfDocumentT document, FpdfPageT page, FpdfFontT? font, TextOverlay text,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        if (font == null)
            throw new PdfEngineException(
                "Для добавления текста нужен системный шрифт TTF (Segoe UI/Arial), но он не найден.");
        if (!double.IsFinite(text.XPt) || !double.IsFinite(text.YPt) ||
            !double.IsFinite(text.FontSizePt) || text.FontSizePt <= 0)
            throw new PdfEngineException("Некорректные параметры текстового оверлея.");

        var obj = fpdf_edit.FPDFPageObjCreateTextObj(document, font, (float)text.FontSizePt);
        if (obj == null || obj.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать текстовый объект.");

        var buffer = new ushort[text.Text.Length + 1];
        for (var i = 0; i < text.Text.Length; i++)
            buffer[i] = text.Text[i];
        if (fpdf_edit.FPDFTextSetText(obj, ref buffer[0]) == 0)
        {
            fpdf_edit.FPDFPageObjDestroy(obj);
            throw new PdfEngineException("Не удалось задать текст объекта.");
        }

        var a = (byte)(text.ColorArgb >> 24);
        var r = (byte)(text.ColorArgb >> 16);
        var g = (byte)(text.ColorArgb >> 8);
        var b = (byte)text.ColorArgb;
        fpdf_edit.FPDFPageObjSetFillColor(obj, r, g, b, a);

        // Точка привязки: верх первой строки → базовая линия ≈ 0.75 размера ниже.
        var baselineDisplayed = (X: text.XPt, Y: text.YPt + text.FontSizePt * 0.75);
        var (cx, cy) = DisplayedToContent(baselineDisplayed.X, baselineDisplayed.Y, rotation, contentWidth, contentHeight);

        // Чтобы текст выглядел повёрнутым на β в отображении, в содержимом он
        // поворачивается на β + 90°·q (отображение доворачивает страницу на 90°·q по часовой).
        var angle = (text.RotationDegrees + 90.0 * rotation) * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        fpdf_edit.FPDFPageObjTransform(obj, cos, sin, -sin, cos, offsetX + cx, offsetY + cy);

        fpdf_edit.FPDFPageInsertObject(page, obj); // объект переходит во владение страницы
    }

    private static void ApplyImage(
        FpdfDocumentT document, FpdfPageT page, ImageOverlay image, double extraAngleDeg,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        var obj = fpdf_edit.FPDFPageObjNewImageObj(document);
        if (obj == null || obj.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать объект изображения.");

        try
        {
            var stride = image.PixelWidth * 4;
            var pin = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
            try
            {
                var bitmap = fpdfview.FPDFBitmapCreateEx(
                    image.PixelWidth, image.PixelHeight, FpdfBitmapBgra, pin.AddrOfPinnedObject(), stride);
                if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                    throw new PdfEngineException("Не удалось подготовить растр изображения.");
                try
                {
                    if (fpdf_edit.FPDFImageObjSetBitmap(null, 0, obj, bitmap) == 0)
                        throw new PdfEngineException("Не удалось поместить изображение в объект.");
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                pin.Free();
            }

            // Единичный квадрат изображения масштабируется до отображаемого
            // прямоугольника, доворачивается на 90°·q (+ добавка при повороте
            // страницы после размещения) и центрируется в точке, соответствующей
            // центру прямоугольника в координатах содержимого.
            var centerDisplayed = (X: image.XPt + image.WidthPt / 2, Y: image.YPt + image.HeightPt / 2);
            var (ccx, ccy) = DisplayedToContent(centerDisplayed.X, centerDisplayed.Y, rotation, contentWidth, contentHeight);

            var angle = (90.0 * rotation + extraAngleDeg) * Math.PI / 180.0;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var a = cos * image.WidthPt;
            var b = sin * image.WidthPt;
            var c = -sin * image.HeightPt;
            var d = cos * image.HeightPt;
            var e = offsetX + ccx - 0.5 * (a + c);
            var f = offsetY + ccy - 0.5 * (b + d);
            fpdf_edit.FPDFPageObjTransform(obj, a, b, c, d, e, f);

            fpdf_edit.FPDFPageInsertObject(page, obj); // объект переходит во владение страницы
        }
        catch
        {
            fpdf_edit.FPDFPageObjDestroy(obj);
            throw;
        }
    }
}
