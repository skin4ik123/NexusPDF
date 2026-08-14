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

        var contentAdded = false;
        foreach (var raw in overlays)
        {
            var (overlay, extraAngle) = OverlayDisplayMapper.ToFrame(
                raw, extraQuarterTurns, displayWidth, displayHeight);
            switch (overlay)
            {
                case TextOverlay text:
                    ApplyText(document, page, font, text,
                        rotation, left, bottom, contentWidth, contentHeight);
                    contentAdded = true;
                    break;
                case ImageOverlay image:
                    ApplyImage(document, page, image, extraAngle,
                        rotation, left, bottom, contentWidth, contentHeight);
                    contentAdded = true;
                    break;
                case NoteAnnotationDraft note:
                    ApplyNote(page, note,
                        rotation, left, bottom, contentWidth, contentHeight);
                    break;
                case ShapeAnnotationDraft shape:
                    ApplyShape(page, shape,
                        rotation, left, bottom, contentWidth, contentHeight);
                    break;
                case OcrTextLayerOverlay ocrLayer:
                    ApplyOcrLayer(document, page, font, ocrLayer, extraAngle,
                        rotation, left, bottom, contentWidth, contentHeight);
                    contentAdded = true;
                    break;
            }
        }

        // GenerateContent нужен только когда менялись объекты содержимого;
        // аннотации живут отдельно от content stream.
        if (contentAdded && fpdf_edit.FPDFPageGenerateContent(page) == 0)
            throw new PdfEngineException("Не удалось сгенерировать содержимое страницы с наложенным контентом.");
    }

    // Подтипы аннотаций по PDF 1.7 (fpdf_annot.h)
    private const int AnnotText = 1;
    private const int AnnotSquare = 5;
    private const int AnnotCircle = 6;
    private const int ColorTypeStroke = 0;
    private const int ColorTypeInterior = 1;

    /// <summary>Прямоугольник аннотации в координатах содержимого из отображаемого прямоугольника.</summary>
    private static FS_RECTF_ ToContentRect(
        double dx, double dy, double w, double h,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        var p1 = DisplayedToContent(dx, dy, rotation, contentWidth, contentHeight);
        var p2 = DisplayedToContent(dx + w, dy + h, rotation, contentWidth, contentHeight);
        return new FS_RECTF_
        {
            Left = (float)(offsetX + Math.Min(p1.X, p2.X)),
            Right = (float)(offsetX + Math.Max(p1.X, p2.X)),
            Bottom = (float)(offsetY + Math.Min(p1.Y, p2.Y)),
            Top = (float)(offsetY + Math.Max(p1.Y, p2.Y)),
        };
    }

    private static void SetAnnotString(FpdfAnnotationT annot, string key, string value)
    {
        var buffer = new ushort[value.Length + 1];
        for (var i = 0; i < value.Length; i++)
            buffer[i] = value[i];
        fpdf_annot.FPDFAnnotSetStringValue(annot, key, ref buffer[0]);
    }

    private static void ApplyNote(
        FpdfPageT page, NoteAnnotationDraft note,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        var annot = fpdf_annot.FPDFPageCreateAnnot(page, AnnotText);
        if (annot == null || annot.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать заметку.");
        try
        {
            const double iconSize = OverlayDisplayMapper.NoteIconSizePt;
            var rect = ToContentRect(note.XPt, note.YPt, iconSize, iconSize,
                rotation, offsetX, offsetY, contentWidth, contentHeight);
            fpdf_annot.FPDFAnnotSetRect(annot, rect);
            fpdf_annot.FPDFAnnotSetColor(annot, (FPDFANNOT_COLORTYPE)ColorTypeStroke, 0xF5, 0xC5, 0x18, 0xFF);
            SetAnnotString(annot, "Contents", note.Contents);
            if (note.Author.Length > 0)
                SetAnnotString(annot, "T", note.Author);
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annot);
        }
    }

    private static void ApplyShape(
        FpdfPageT page, ShapeAnnotationDraft shape,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        var annot = fpdf_annot.FPDFPageCreateAnnot(page, shape.IsEllipse ? AnnotCircle : AnnotSquare);
        if (annot == null || annot.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать фигурную аннотацию.");
        try
        {
            var rect = ToContentRect(shape.XPt, shape.YPt, shape.WidthPt, shape.HeightPt,
                rotation, offsetX, offsetY, contentWidth, contentHeight);
            fpdf_annot.FPDFAnnotSetRect(annot, rect);

            var sa = (byte)(shape.StrokeArgb >> 24);
            fpdf_annot.FPDFAnnotSetColor(annot, (FPDFANNOT_COLORTYPE)ColorTypeStroke,
                (byte)(shape.StrokeArgb >> 16), (byte)(shape.StrokeArgb >> 8), (byte)shape.StrokeArgb, sa);
            var fa = (byte)(shape.FillArgb >> 24);
            if (fa > 0)
                fpdf_annot.FPDFAnnotSetColor(annot, (FPDFANNOT_COLORTYPE)ColorTypeInterior,
                    (byte)(shape.FillArgb >> 16), (byte)(shape.FillArgb >> 8), (byte)shape.FillArgb, fa);
            fpdf_annot.FPDFAnnotSetBorder(annot, 0, 0, (float)shape.BorderWidthPt);
            if (shape.Contents.Length > 0)
                SetAnnotString(annot, "Contents", shape.Contents);
            if (shape.Author.Length > 0)
                SetAnnotString(annot, "T", shape.Author);
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annot);
        }
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

    // PDF text render mode Tr 3 — глифы не рисуются, но участвуют в
    // текстовом слое (поиск/выделение/копирование).
    private const int TextRenderModeInvisible = 3;

    private static void ApplyOcrLayer(
        FpdfDocumentT document, FpdfPageT page, FpdfFontT? font, OcrTextLayerOverlay layer,
        double extraAngleDeg, int rotation, double offsetX, double offsetY,
        double contentWidth, double contentHeight)
    {
        if (font == null)
            throw new PdfEngineException(
                "Для текстового слоя OCR нужен системный шрифт TTF (Segoe UI/Arial), но он не найден.");

        var angle = (extraAngleDeg + 90.0 * rotation) * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        foreach (var word in layer.Words)
        {
            if (word.Text.Length == 0 ||
                !double.IsFinite(word.XPt) || !double.IsFinite(word.YPt) ||
                !(word.WidthPt > 0) || !(word.HeightPt > 0))
                continue;

            var fontSize = (float)word.HeightPt;
            var obj = fpdf_edit.FPDFPageObjCreateTextObj(document, font, fontSize);
            if (obj == null || obj.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось создать текстовый объект слоя OCR.");

            var buffer = new ushort[word.Text.Length + 1];
            for (var i = 0; i < word.Text.Length; i++)
                buffer[i] = word.Text[i];
            if (fpdf_edit.FPDFTextSetText(obj, ref buffer[0]) == 0)
            {
                fpdf_edit.FPDFPageObjDestroy(obj);
                continue; // слово с непечатаемыми символами — пропускаем, не роняя сохранение
            }

            fpdf_edit.FPDFTextObjSetTextRenderMode(obj, (FPDF_TEXT_RENDERMODE)TextRenderModeInvisible);

            // Слово растягивается по горизонтали под ширину распознанной рамки,
            // чтобы прямоугольники выделения совпадали со сканом.
            float bl = 0, bb = 0, br = 0, bt = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref bl, ref bb, ref br, ref bt) != 0)
            {
                var measured = br - bl;
                if (measured > 0.01)
                {
                    var sx = Math.Clamp(word.WidthPt / measured, 0.05, 20.0);
                    fpdf_edit.FPDFPageObjTransform(obj, sx, 0, 0, 1, 0, 0);
                }
            }

            var baselineDisplayed = (X: word.XPt, Y: word.YPt + word.HeightPt * OverlayDisplayMapper.TextBaselineFactor);
            var (cx, cy) = DisplayedToContent(baselineDisplayed.X, baselineDisplayed.Y, rotation, contentWidth, contentHeight);
            fpdf_edit.FPDFPageObjTransform(obj, cos, sin, -sin, cos, offsetX + cx, offsetY + cy);

            fpdf_edit.FPDFPageInsertObject(page, obj); // объект переходит во владение страницы
        }
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
