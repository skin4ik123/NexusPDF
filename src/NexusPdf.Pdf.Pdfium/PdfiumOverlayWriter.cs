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

    public static void ApplyOverlays(
        FpdfDocumentT document, FpdfPageT page, OverlayFontCache fonts,
        IReadOnlyList<PageOverlay> overlays, int extraQuarterTurns)
    {
        // Шрифт по умолчанию нужен слоям распознавания: у них своей гарнитуры
        // нет, они пишут системной. Надписи выбирают шрифт сами, ниже.
        var font = fonts.Default;

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

        // Замена содержимого выполняется ПЕРВОЙ: она стирает объекты страницы,
        // после чего остальные оверлеи ложатся поверх новой картинки.
        // Аннотации, ссылки и поля форм не трогаются — они не объекты
        // содержимого, а элементы /Annots.
        if (overlays.OfType<PageRasterReplacement>().LastOrDefault() is { } replacement)
        {
            for (var i = fpdf_edit.FPDFPageCountObjects(page) - 1; i >= 0; i--)
            {
                var existing = fpdf_edit.FPDFPageGetObject(page, i);
                if (existing == null || existing.__Instance == IntPtr.Zero)
                    continue;
                if (fpdf_edit.FPDFPageRemoveObject(page, existing) != 0)
                    fpdf_edit.FPDFPageObjDestroy(existing); // владение вернулось нам
            }

            // Растр кладётся на всю рамку страницы в ЕЁ ориентации: правка
            // выполнялась над отображаемым видом.
            var full = new ImageOverlay(
                replacement.Bgra, replacement.PixelWidth, replacement.PixelHeight,
                0, 0, displayWidth, displayHeight);
            ApplyImage(document, page, full, 0, rotation, left, bottom, contentWidth, contentHeight);
            contentAdded = true;
        }

        foreach (var raw in overlays)
        {
            var (overlay, extraAngle) = OverlayDisplayMapper.ToFrame(
                raw, extraQuarterTurns, displayWidth, displayHeight);
            switch (overlay)
            {
                case TextOverlay text:
                    // Гарнитура и начертание — свойства самой надписи, поэтому
                    // шрифт берётся под неё, а не общий на всю страницу.
                    ApplyText(document, page, fonts.For(text.FontFamily, text.Bold, text.Italic), text,
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
                case TextMarkupDraft markup:
                    ApplyTextMarkup(page, markup,
                        rotation, left, bottom, contentWidth, contentHeight);
                    break;
                case InkAnnotationDraft ink:
                    ApplyInk(page, ink,
                        rotation, left, bottom, contentWidth, contentHeight);
                    break;
                case PageRasterReplacement:
                    break; // уже применена выше
                case RegionEraseDraft erase:
                    // Заплатка цветом бумаги. Настоящее стирание делает
                    // растеризация при сохранении; здесь — чтобы предпросмотр
                    // и промежуточные проходы показывали будущий результат.
                    InsertFilledRect(page,
                        erase.XPt, erase.YPt, erase.WidthPt, erase.HeightPt,
                        erase.FillArgb, rotation, left, bottom, contentWidth, contentHeight);
                    contentAdded = true;
                    break;
                case TextObjectReplacement textReplacement:
                    ApplyTextObjectReplacement(document, page, fonts, textReplacement);
                    contentAdded = true;
                    break;
                case ImageObjectReplacement imageReplacement:
                    ApplyImageObjectReplacement(document, page, imageReplacement);
                    contentAdded = true;
                    break;
                case OcrTextLayerOverlay ocrLayer:
                    ApplyOcrLayer(document, page, font, ocrLayer, extraAngle,
                        rotation, left, bottom, contentWidth, contentHeight);
                    contentAdded = true;
                    break;
                case OcrEditableTextOverlay editable:
                    // Кэш шрифтов целиком, а не один шрифт: гарнитура у каждой
                    // строки своя — подобранная под начертание оригинала.
                    ApplyOcrEditableLayer(document, page, fonts, editable, extraAngle,
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

    // Подтипы разметки текста (PDF 1.7, таблица 8.20)
    private const int AnnotHighlight = 9;
    private const int AnnotUnderline = 10;
    private const int AnnotStrikeOut = 12;

    /// <summary>
    /// Разметка выделенного текста настоящей аннотацией Highlight/Underline/
    /// StrikeOut. Строки идут quadpoints'ами, поэтому многострочное выделение
    /// размечается по строкам, а не одним блоком через весь абзац, и любая
    /// программа показывает это как разметку текста, а не как фигуру поверх.
    /// </summary>
    private static void ApplyTextMarkup(
        FpdfPageT page, TextMarkupDraft markup,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        var rects = markup.Rects.Where(r => r.WidthPt > 0 && r.HeightPt > 0).ToList();
        if (rects.Count == 0)
            return;

        var subtype = markup.Kind switch
        {
            TextMarkupKind.Highlight => AnnotHighlight,
            TextMarkupKind.Underline => AnnotUnderline,
            TextMarkupKind.StrikeOut => AnnotStrikeOut,
            _ => throw new PdfEngineException($"Неизвестный вид разметки текста: {markup.Kind}."),
        };

        var annot = fpdf_annot.FPDFPageCreateAnnot(page, subtype);
        if (annot == null || annot.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать разметку текста.");
        try
        {
            var alpha = (byte)(markup.ColorArgb >> 24);
            fpdf_annot.FPDFAnnotSetColor(annot, (FPDFANNOT_COLORTYPE)ColorTypeStroke,
                (byte)(markup.ColorArgb >> 16), (byte)(markup.ColorArgb >> 8), (byte)markup.ColorArgb,
                alpha == 0 ? (byte)0xFF : alpha);

            // Общая рамка ставится до quadpoints: pdfium расширяет её сам, но
            // аннотация без /Rect не считается корректной ни одним читателем.
            double minLeft = double.MaxValue, minBottom = double.MaxValue;
            double maxRight = double.MinValue, maxTop = double.MinValue;
            foreach (var rect in rects)
            {
                var box = ToContentRect(rect.XPt, rect.YPt, rect.WidthPt, rect.HeightPt,
                    rotation, offsetX, offsetY, contentWidth, contentHeight);
                minLeft = Math.Min(minLeft, box.Left);
                minBottom = Math.Min(minBottom, box.Bottom);
                maxRight = Math.Max(maxRight, box.Right);
                maxTop = Math.Max(maxTop, box.Top);
            }
            fpdf_annot.FPDFAnnotSetRect(annot, new FS_RECTF_
            {
                Left = (float)minLeft,
                Bottom = (float)minBottom,
                Right = (float)maxRight,
                Top = (float)maxTop,
            });

            foreach (var rect in rects)
            {
                var box = ToContentRect(rect.XPt, rect.YPt, rect.WidthPt, rect.HeightPt,
                    rotation, offsetX, offsetY, contentWidth, contentHeight);
                // Порядок точек в PDF — «зигзагом»: верх-лево, верх-право,
                // низ-лево, низ-право. Перепутать его — получить пустую или
                // вывернутую разметку в других программах.
                var quad = new FS_QUADPOINTSF
                {
                    X1 = box.Left, Y1 = box.Top,
                    X2 = box.Right, Y2 = box.Top,
                    X3 = box.Left, Y3 = box.Bottom,
                    X4 = box.Right, Y4 = box.Bottom,
                };
                if (fpdf_annot.FPDFAnnotAppendAttachmentPoints(annot, quad) == 0)
                    throw new PdfEngineException("Не удалось задать область разметки текста.");
            }

            if (markup.Contents.Length > 0)
                SetAnnotString(annot, "Contents", markup.Contents);
            if (markup.Author.Length > 0)
                SetAnnotString(annot, "T", markup.Author);
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annot);
        }
    }

    private const int AnnotInk = 15; // FPDF_ANNOT_INK

    /// <summary>
    /// Прямой вызов pdfium в обход обёртки PDFiumCore. Сгенерированная
    /// сигнатура принимает ОДНУ точку по значению, а функция ждёт массив из
    /// point_count пар float; из-за этого через обёртку в PDF попадал мусор
    /// вместо штриха (проверено дампом /InkList). Здесь массив передаётся так,
    /// как его ждёт C-функция.
    /// </summary>
    [DllImport("pdfium", EntryPoint = "FPDFAnnot_AddInkStroke",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int AddInkStroke(IntPtr annot, float[] points, ulong pointCount);

    /// <summary>
    /// Рисунок от руки. Ink-аннотация хранит сами штрихи, а не картинку,
    /// поэтому линия остаётся чёткой при любом масштабе, её видно в других
    /// программах и её можно удалить, не трогая содержимое страницы.
    /// </summary>
    private static void ApplyInk(
        FpdfPageT page, InkAnnotationDraft ink,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        var strokes = ink.Strokes.Where(s => s.Count >= 2).ToList();
        if (strokes.Count == 0)
            return;
        if (!(ink.WidthPt > 0) || !double.IsFinite(ink.WidthPt))
            throw new PdfEngineException("Некорректная толщина линии рисунка.");

        var annot = fpdf_annot.FPDFPageCreateAnnot(page, AnnotInk);
        if (annot == null || annot.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать рисунок.");
        try
        {
            // Цвет и толщина задаются ДО добавления штрихов: pdfium строит
            // внешний вид Ink-аннотации в момент добавления штриха, и всё,
            // выставленное после, в картинку уже не попадёт.
            var alpha = (byte)(ink.StrokeArgb >> 24);
            fpdf_annot.FPDFAnnotSetColor(annot, (FPDFANNOT_COLORTYPE)ColorTypeStroke,
                (byte)(ink.StrokeArgb >> 16), (byte)(ink.StrokeArgb >> 8), (byte)ink.StrokeArgb,
                alpha == 0 ? (byte)0xFF : alpha);
            fpdf_annot.FPDFAnnotSetBorder(annot, 0, 0, (float)ink.WidthPt);

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var stroke in strokes)
            {
                // pdfium ждёт НЕПРЕРЫВНЫЙ массив пар float. Массив FS_POINTF_ им
                // не является: каждый элемент — отдельная обёртка над своим
                // куском native-памяти, и по указателю первого элемента pdfium
                // читал бы чужие байты. Поэтому буфер собирается вручную.
                var buffer = new float[stroke.Count * 2];
                for (var i = 0; i < stroke.Count; i++)
                {
                    if (!double.IsFinite(stroke[i].XPt) || !double.IsFinite(stroke[i].YPt))
                        throw new PdfEngineException("Некорректная точка рисунка.");
                    var (cx, cy) = DisplayedToContent(
                        stroke[i].XPt, stroke[i].YPt, rotation, contentWidth, contentHeight);
                    var x = offsetX + cx;
                    var y = offsetY + cy;
                    buffer[i * 2] = (float)x;
                    buffer[i * 2 + 1] = (float)y;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }

                if (AddInkStroke(annot.__Instance, buffer, (ulong)stroke.Count) < 0)
                    throw new PdfEngineException("Не удалось добавить штрих рисунка.");
            }

            // Рамка расширяется на пол-толщины: иначе просмотрщик обрежет
            // край линии, нарисованной ровно по границе.
            var pad = ink.WidthPt / 2 + 1;
            fpdf_annot.FPDFAnnotSetRect(annot, new FS_RECTF_
            {
                Left = (float)(minX - pad),
                Bottom = (float)(minY - pad),
                Right = (float)(maxX + pad),
                Top = (float)(maxY + pad),
            });

            if (ink.Contents.Length > 0)
                SetAnnotString(annot, "Contents", ink.Contents);
            if (ink.Author.Length > 0)
                SetAnnotString(annot, "T", ink.Author);
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

            // Рамка Tesseract — это ink-бокс чернил скана. Ink-бокс запечённых
            // глифов растягивается ровно в неё ПО ОБЕИМ осям: иначе слово из
            // ЗАГЛАВНЫХ (рамка = cap-height) получало бы глифы мельче чернил и
            // смещённые прямоугольники выделения по вертикали.
            float bl = 0, bb = 0, br = 0, bt = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref bl, ref bb, ref br, ref bt) != 0)
            {
                var measuredW = br - bl;
                var measuredH = bt - bb;
                if (measuredW > 0.01 && measuredH > 0.01)
                {
                    var sx = Math.Clamp(word.WidthPt / measuredW, 0.05, 20.0);
                    var sy = Math.Clamp(word.HeightPt / measuredH, 0.05, 20.0);
                    // Ink-бокс к началу координат, затем масштаб до рамки.
                    fpdf_edit.FPDFPageObjTransform(obj, 1, 0, 0, 1, -bl, -bb);
                    fpdf_edit.FPDFPageObjTransform(obj, sx, 0, 0, sy, 0, 0);
                }
            }

            // Левый нижний угол рамки слова (в отображаемых координатах низ —
            // это YPt + HeightPt) переносится в координаты содержимого.
            var anchorDisplayed = (X: word.XPt, Y: word.YPt + word.HeightPt);
            var (cx, cy) = DisplayedToContent(anchorDisplayed.X, anchorDisplayed.Y, rotation, contentWidth, contentHeight);
            fpdf_edit.FPDFPageObjTransform(obj, cos, sin, -sin, cos, offsetX + cx, offsetY + cy);

            fpdf_edit.FPDFPageInsertObject(page, obj); // объект переходит во владение страницы
        }
    }

    /// <summary>
    /// Закрашенный прямоугольник как ОБЪЕКТ СОДЕРЖИМОГО страницы (не
    /// аннотация): порядок отрисовки такой же, как у остальных объектов,
    /// поэтому вставленный следом текст ляжет поверх заплатки.
    /// </summary>
    private static void InsertFilledRect(
        FpdfPageT page, double xPt, double yPt, double widthPt, double heightPt, uint argb,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        var rect = ToContentRect(xPt, yPt, widthPt, heightPt,
            rotation, offsetX, offsetY, contentWidth, contentHeight);
        var left = Math.Min(rect.Left, rect.Right);
        var bottom = Math.Min(rect.Top, rect.Bottom);
        var width = Math.Abs(rect.Right - rect.Left);
        var height = Math.Abs(rect.Top - rect.Bottom);
        if (!(width > 0) || !(height > 0))
            return;

        var obj = fpdf_edit.FPDFPageObjCreateNewRect(left, bottom, width, height);
        if (obj == null || obj.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать подложку под текст.");

        fpdf_edit.FPDFPageObjSetFillColor(obj,
            (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
        // FPDF_FILLMODE_WINDING = 1, обводки нет.
        fpdf_edit.FPDFPathSetDrawMode(obj, 1, 0);
        fpdf_edit.FPDFPageInsertObject(page, obj);
    }

    /// <summary>
    /// РЕДАКТИРУЕМЫЙ текст вместо скана: место строки затягивается заплаткой
    /// фона, а поверх ставится обычный видимый текст. Закрытие оригинала
    /// обязательно — иначе поверх букв скана легли бы вторые буквы и строка
    /// двоилась бы.
    /// </summary>
    private static void ApplyOcrEditableLayer(
        FpdfDocumentT document, FpdfPageT page, OverlayFontCache fonts, OcrEditableTextOverlay layer,
        double extraAngleDeg, int rotation, double offsetX, double offsetY,
        double contentWidth, double contentHeight)
    {
        var angle = (extraAngleDeg + 90.0 * rotation) * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        foreach (var line in layer.Lines)
        {
            if (line.Text.Length == 0 ||
                !double.IsFinite(line.XPt) || !double.IsFinite(line.YPt) ||
                !(line.WidthPt > 0) || !(line.HeightPt > 0))
                continue;

            // Гарнитура подобрана под начертание оригинала при распознавании;
            // неизвестная в системе откатится на шрифт по умолчанию.
            var font = fonts.For(line.FontFamily, line.Bold, false);
            if (font == null)
                throw new PdfEngineException(
                    "Для редактируемого текста нужен системный шрифт TTF (Segoe UI/Arial), но он не найден.");

            // 1. СНАЧАЛА текст, и только если он получился — заплатка.
            //    Порядок здесь не косметика: раньше заплатка ставилась первой,
            //    и строка, для которой текст создать не удалось, оставляла на
            //    скане закрашенный прямоугольник без единой буквы. На
            //    фотографии документа это выглядит как дыра в изображении.
            var obj = fpdf_edit.FPDFPageObjCreateTextObj(document, font, (float)line.HeightPt);
            if (obj == null || obj.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось создать редактируемый текстовый объект.");

            var buffer = new ushort[line.Text.Length + 1];
            for (var i = 0; i < line.Text.Length; i++)
                buffer[i] = line.Text[i];
            if (fpdf_edit.FPDFTextSetText(obj, ref buffer[0]) == 0)
            {
                // Оригинал под этой строкой остаётся нетронутым: испортить
                // изображение хуже, чем не заменить одну строку.
                fpdf_edit.FPDFPageObjDestroy(obj);
                continue;
            }

            // 2. Закрытие оригинала — ОБЪЕКТОМ СТРАНИЦЫ, а не аннотацией:
            //    аннотации рисуются поверх всего содержимого и закрыли бы
            //    собственный текст. Если при распознавании удалось снять
            //    кусочек фона, кладём его — бумага скана неоднородна, и
            //    ровный прямоугольник виден на ней как дыра. Заливка одним
            //    цветом остаётся запасным вариантом.
            if (line.Patch is { PixelWidth: > 0, PixelHeight: > 0 } patch &&
                patch.Bgra.Length >= patch.PixelWidth * patch.PixelHeight * 4)
            {
                ApplyImage(document, page,
                    new ImageOverlay(patch.Bgra, patch.PixelWidth, patch.PixelHeight,
                        patch.XPt, patch.YPt, patch.WidthPt, patch.HeightPt),
                    extraAngleDeg, rotation, offsetX, offsetY, contentWidth, contentHeight);
            }
            else
            {
                var pad = line.PadPt;
                InsertFilledRect(page,
                    line.XPt - pad, line.YPt - pad,
                    line.WidthPt + pad * 2, line.HeightPt + pad * 2,
                    line.BackgroundArgb, rotation, offsetX, offsetY, contentWidth, contentHeight);
            }

            fpdf_edit.FPDFPageObjSetFillColor(obj,
                (byte)(line.InkArgb >> 16), (byte)(line.InkArgb >> 8), (byte)line.InkArgb,
                (byte)(line.InkArgb >> 24));

            float bl = 0, bb = 0, br = 0, bt = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref bl, ref bb, ref br, ref bt) != 0)
            {
                var measuredW = br - bl;
                var measuredH = bt - bb;
                if (measuredW > 0.01 && measuredH > 0.01)
                {
                    // Масштаб считается ТОЛЬКО по ширине и применяется к обеим
                    // осям сразу. Ширина рамки — это честная ширина набранной
                    // строки, а высота зависит от того, попались ли в ней
                    // заглавные и хвосты вниз: у строки вроде «оно все» глифы
                    // низкие, и подгонка по высоте раздувала её на всю рамку —
                    // отсюда и «иногда текст становится огромным». Единый
                    // масштаб заодно не даёт буквам сплющиваться по одной оси.
                    var scale = Math.Clamp(line.WidthPt / measuredW, 0.05, 20.0);

                    // Страховка от завышенной рамки: строка не должна вылезти
                    // за собственную высоту больше чем на четверть, иначе одна
                    // кривая рамка от распознавания наедет на соседние строки.
                    var maxScale = line.HeightPt * 1.25 / measuredH;
                    if (maxScale > 0.0 && scale > maxScale)
                        scale = maxScale;

                    fpdf_edit.FPDFPageObjTransform(obj, 1, 0, 0, 1, -bl, -bb);
                    fpdf_edit.FPDFPageObjTransform(obj, scale, 0, 0, scale, 0, 0);
                }
            }

            var (cx, cy) = DisplayedToContent(
                line.XPt, line.YPt + line.HeightPt, rotation, contentWidth, contentHeight);
            fpdf_edit.FPDFPageObjTransform(obj, cos, sin, -sin, cos, offsetX + cx, offsetY + cy);
            fpdf_edit.FPDFPageInsertObject(page, obj);
        }
    }

    /// <summary>
    /// Замена содержимого существующего текстового объекта. Шрифт, кегль,
    /// цвет и матрица принадлежат самому объекту и не трогаются, поэтому
    /// правленая строка встаёт на место прежней в том же оформлении.
    /// </summary>
    private static void ApplyTextObjectReplacement(
        FpdfDocumentT document, FpdfPageT page, OverlayFontCache fonts, TextObjectReplacement replacement)
    {
        const int pageObjectText = 1; // FPDF_PAGEOBJ_TEXT

        // Вложенный объект найти можно, а сохранить правку — нет: PDFium
        // перегенерирует поток страницы и не трогает поток формы, поэтому
        // запись молча пропала бы при сохранении. Падаем вслух.
        if (replacement.ObjectPath.Count > 1)
            throw new PdfEngineException(
                "Эта строка лежит внутри вложенного объекта документа, и сохранить её правку нельзя.");

        // Путь, а не номер: адрес объекта — цепочка индексов, а не одно число.
        var obj = PdfObjectTree.Resolve(page, replacement.ObjectPath);
        if (obj == null || obj.__Instance == IntPtr.Zero)
            throw new PdfEngineException(
                "Текст для замены не найден: содержимое страницы изменилось.");
        if (fpdf_edit.FPDFPageObjGetType(obj) != pageObjectText)
            throw new PdfEngineException(
                "Объект по указанному адресу больше не является текстом.");

        if (replacement.ChangesStyle)
        {
            RestyleTextObject(document, page, fonts, replacement, obj);
            return;
        }

        var buffer = new ushort[replacement.Text.Length + 1];
        for (var i = 0; i < replacement.Text.Length; i++)
            buffer[i] = replacement.Text[i];
        if (fpdf_edit.FPDFTextSetText(obj, ref buffer[0]) == 0)
            throw new PdfEngineException("Не удалось записать новый текст в объект страницы.");
    }

    /// <summary>
    /// Замена строки С ДРУГИМ ОФОРМЛЕНИЕМ: гарнитурой, кеглем или цветом.
    ///
    /// Установить шрифт существующему текстовому объекту PDFium не даёт — есть
    /// только чтение. Поэтому старый объект убирается со страницы, а на его
    /// место, с его же матрицей и на ту же позицию в порядке рисования, встаёт
    /// новый. Матрица переносится целиком: в ней сидит и положение строки, и
    /// её наклон, и масштаб, поэтому текст не уезжает.
    /// </summary>
    private static void RestyleTextObject(
        FpdfDocumentT document, FpdfPageT page, OverlayFontCache fonts,
        TextObjectReplacement replacement, FpdfPageobjectT original)
    {
        var matrix = new FS_MATRIX_();
        var hasMatrix = fpdf_edit.FPDFPageObjGetMatrix(original, matrix) != 0;

        float originalSize = 0;
        fpdf_edit.FPDFTextObjGetFontSize(original, ref originalSize);
        var size = replacement.FontSizePt > 0 ? replacement.FontSizePt : originalSize;
        if (!(size > 0))
            size = 12; // у объекта без кегля хоть что-то читаемое

        uint r0 = 0, g0 = 0, b0 = 0, a0 = 255;
        fpdf_edit.FPDFPageObjGetFillColor(original, ref r0, ref g0, ref b0, ref a0);
        var color = replacement.ColorArgb != 0
            ? replacement.ColorArgb
            : (a0 << 24) | (r0 << 16) | (g0 << 8) | b0;

        var font = fonts.For(replacement.FontFamily, replacement.Bold, replacement.Italic);
        if (font == null)
            throw new PdfEngineException(
                "Для смены шрифта нужен системный TTF (Segoe UI/Arial), но он не найден.");

        var created = fpdf_edit.FPDFPageObjCreateTextObj(document, font, (float)size);
        if (created == null || created.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать текстовый объект с новым шрифтом.");

        var buffer = new ushort[replacement.Text.Length + 1];
        for (var i = 0; i < replacement.Text.Length; i++)
            buffer[i] = replacement.Text[i];
        if (fpdf_edit.FPDFTextSetText(created, ref buffer[0]) == 0)
        {
            fpdf_edit.FPDFPageObjDestroy(created);
            throw new PdfEngineException("Не удалось записать текст в новый объект.");
        }

        fpdf_edit.FPDFPageObjSetFillColor(created,
            (byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)(color >> 24));

        if (hasMatrix)
            fpdf_edit.FPDFPageObjSetMatrix(created, matrix);

        // Порядок рисования сохраняется: строка, которая была под картинкой,
        // не должна после правки оказаться поверх неё.
        var index = replacement.ObjectPath[0];
        if (fpdf_edit.FPDFPageRemoveObject(page, original) == 0)
        {
            fpdf_edit.FPDFPageObjDestroy(created);
            throw new PdfEngineException("Не удалось убрать прежнюю строку со страницы.");
        }
        fpdf_edit.FPDFPageObjDestroy(original); // после снятия объект принадлежит нам

        if (fpdf_edit.FPDFPageInsertObjectAtIndex(page, created, (ulong)index) == 0)
        {
            // Не встало на своё место — кладём хотя бы поверх: потерять строку
            // хуже, чем нарушить порядок рисования.
            fpdf_edit.FPDFPageInsertObject(page, created);
        }
    }

    /// <summary>
    /// Подмена растра у СУЩЕСТВУЮЩЕГО объекта-изображения. Матрица объекта не
    /// трогается, поэтому положение, масштаб, поворот, обрезка, прозрачность и
    /// порядок отрисовки сохраняются сами собой — вся страница не растрируется.
    /// </summary>
    private static void ApplyImageObjectReplacement(
        FpdfDocumentT document, FpdfPageT page, ImageObjectReplacement replacement)
    {
        const int pageObjectImage = 3; // FPDF_PAGEOBJ_IMAGE
        var count = fpdf_edit.FPDFPageCountObjects(page);
        if (replacement.ObjectIndex < 0 || replacement.ObjectIndex >= count)
            throw new PdfEngineException(
                "Изображение для замены не найдено: содержимое страницы изменилось.");

        var obj = fpdf_edit.FPDFPageGetObject(page, replacement.ObjectIndex);
        if (obj == null || obj.__Instance == IntPtr.Zero ||
            fpdf_edit.FPDFPageObjGetType(obj) != pageObjectImage)
            throw new PdfEngineException(
                "Объект по указанному номеру больше не является изображением.");

        var stride = replacement.PixelWidth * 4;
        var pin = GCHandle.Alloc(replacement.Bgra, GCHandleType.Pinned);
        try
        {
            var bitmap = fpdfview.FPDFBitmapCreateEx(
                replacement.PixelWidth, replacement.PixelHeight, FpdfBitmapBgra,
                pin.AddrOfPinnedObject(), stride);
            if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось подготовить растр замены изображения.");
            try
            {
                if (fpdf_edit.FPDFImageObjSetBitmap(null, 0, obj, bitmap) == 0)
                    throw new PdfEngineException("Не удалось заменить изображение страницы.");
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
