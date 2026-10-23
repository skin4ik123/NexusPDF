using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Описание страницы для правки текста и заполнения бланков: текстовые
/// объекты с буквами и шрифтом, тонкие линии и границы таблиц — всё в
/// отображаемых пунктах. Объекты внутри форм (Form XObject) тоже: их место
/// на странице пересчитывается матрицами всех форм над ними.
/// </summary>
internal sealed partial class PdfiumDocumentHandle
{
    /// <summary>Тоньше — линия (подчёркивание, строка бланка), толще — уже полоса.</summary>
    private const double MaxThinLinePt = 1.5;
    private const double MinThinLineLengthPt = 2;
    private const int TextRenderInvisible = 3;
    private const int TextRenderClip = 7;
    private const int FontFlagItalic = 1 << 6;
    private const int FontFlagForceBold = 1 << 18;

    public Task<PageTextLayout> GetPageTextLayoutAsync(int pageIndex, int extraQuarterTurns, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() => ReadPageTextLayout(pageIndex, extraQuarterTurns, ct), ct);
        }
    }

    private PageTextLayout ReadPageTextLayout(int pageIndex, int extraQuarterTurns, CancellationToken ct)
    {
        var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
        if (page == null || page.__Instance == IntPtr.Zero)
            return PageTextLayout.Empty;
        var textPage = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            var frame = PageFrame.Of(page, extraQuarterTurns);
            var chars = CharsByObject(textPage, ct);
            var objects = new List<PageTextObject>();
            var lines = new List<PageLineObject>();
            var rulings = new List<PageRuling>();
            var nodes = PdfObjectTree.TextAndPathObjects(page);
            for (var i = 0; i < nodes.Count; i++)
            {
                if (i % 256 == 0)
                    ct.ThrowIfCancellationRequested();
                var node = nodes[i];
                if (fpdf_edit.FPDFPageObjGetType(node.Object) == PageObjectText)
                {
                    if (chars.TryGetValue(node.Object.__Instance, out var indices) &&
                        DescribeText(node, indices, textPage!, frame) is { } text)
                        objects.Add(text);
                    continue;
                }
                if (!IsVisible(node.Object) ||
                    !PdfObjectTree.TryGetPageBounds(node.Object, node.ToPage, out var l, out var b, out var r, out var t))
                    continue;
                var box = Displayed(frame, l, b, r, t);
                if (Math.Min(box.WidthPt, box.HeightPt) < MaxThinLinePt &&
                    Math.Max(box.WidthPt, box.HeightPt) >= MinThinLineLengthPt)
                    lines.Add(new PageLineObject(node.Path, box, PathColor(node.Object)));
                AddRulings(node, frame, l, b, r, t, rulings);
            }
            return new PageTextLayout(objects, lines, rulings);
        }
        finally
        {
            if (textPage != null && textPage.__Instance != IntPtr.Zero)
                fpdf_text.FPDFTextClosePage(textPage);
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>Номера букв страницы по объектам — только записанные, без додуманных PDFium пробелов.</summary>
    private static Dictionary<IntPtr, List<int>> CharsByObject(FpdfTextpageT? textPage, CancellationToken ct)
    {
        var result = new Dictionary<IntPtr, List<int>>();
        if (textPage == null || textPage.__Instance == IntPtr.Zero)
            return result;
        var count = fpdf_text.FPDFTextCountChars(textPage);
        for (var i = 0; i < count; i++)
        {
            if (i % 4096 == 0)
                ct.ThrowIfCancellationRequested();
            if (fpdf_text.FPDFTextIsGenerated(textPage, i) != 0)
                continue;
            var obj = fpdf_text.FPDFTextGetTextObject(textPage, i);
            if (obj == null || obj.__Instance == IntPtr.Zero)
                continue;
            if (!result.TryGetValue(obj.__Instance, out var list))
                result[obj.__Instance] = list = [];
            list.Add(i);
        }
        return result;
    }

    private static unsafe PageTextObject? DescribeText(
        PdfObjectTree.Node node, List<int> indices, FpdfTextpageT textPage, PageFrame frame)
    {
        var obj = node.Object;
        var matrix = new FS_MATRIX_();
        if (fpdf_edit.FPDFPageObjGetMatrix(obj, matrix) == 0)
            return null;
        // Матрица текста уже включает cm страницы; формы над ним — в node.ToPage.
        var full = new PdfObjectTree.Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F).Then(node.ToPage);
        var (ox, oy) = (full.E, full.F);
        var length = Math.Sqrt(full.A * full.A + full.B * full.B);
        if (!(length > 1e-9))
            return null;
        var (ux, uy) = (full.A / length, full.B / length);

        var text = new System.Text.StringBuilder();
        var advances = new List<double>(indices.Count);
        for (var k = 0; k < indices.Count; k++)
        {
            var code = fpdf_text.FPDFTextGetUnicode(textPage, indices[k]);
            var units = 1;
            if (code > 0xFFFF && code <= 0x10FFFF)
            {
                text.Append(char.ConvertFromUtf32((int)code));
                units = 2;
            }
            else if (code is > 0 and <= 0xFFFF)
                text.Append((char)code);
            else
                continue;

            double x = 0, y = 0;
            fpdf_text.FPDFTextGetCharOrigin(textPage, indices[k], ref x, ref y);
            double advance;
            if (k + 1 < indices.Count)
            {
                double nx = 0, ny = 0;
                fpdf_text.FPDFTextGetCharOrigin(textPage, indices[k + 1], ref nx, ref ny);
                advance = (nx - x) * ux + (ny - y) * uy;
            }
            else
            {
                // У последней буквы следующей нет: до дальнего края её рамки.
                var box = new FS_RECTF_();
                advance = fpdf_text.FPDFTextGetLooseCharBox(textPage, indices[k], box) != 0
                    ? new[] { (box.Left, box.Top), (box.Right, box.Top), (box.Right, box.Bottom), (box.Left, box.Bottom) }
                        .Max(c => (c.Item1 - x) * ux + (c.Item2 - y) * uy)
                    : 0;
            }
            advances.Add(Math.Max(0, advance));
            if (units == 2) advances.Add(0);
        }
        if (text.Length == 0)
            return null;

        float size = 0;
        fpdf_edit.FPDFTextObjGetFontSize(obj, ref size);
        var fontSize = size * Math.Sqrt(Math.Abs(full.A * full.D - full.B * full.C));

        if (!PdfObjectTree.TryGetPageBounds(obj, node.ToPage, out var l, out var b, out var r, out var t))
            return null;
        var (dx, dy) = frame.ToDisplayed(ox, oy);
        var (ex, ey) = frame.ToDisplayed(ox + ux, oy + uy);
        var angle = Math.Atan2(ey - dy, ex - dx) * 180 / Math.PI;

        uint cr = 0, cg = 0, cb = 0, ca = 255;
        fpdf_edit.FPDFPageObjGetFillColor(obj, ref cr, ref cg, ref cb, ref ca);
        var mode = (int)fpdf_edit.FPDFTextObjGetTextRenderMode(obj);

        var fontName = "";
        bool bold = false, italic = false, type3 = false;
        var font = fpdf_edit.FPDFTextObjGetFont(obj);
        if (font != null && font.__Instance != IntPtr.Zero)
        {
            fontName = ReadFontName(font);
            var flags = fpdf_edit.FPDFFontGetFlags(font);
            var italicAngle = 0;
            fpdf_edit.FPDFFontGetItalicAngle(font, ref italicAngle);
            bold = fpdf_edit.FPDFFontGetWeight(font) >= 600 || (flags & FontFlagForceBold) != 0 ||
                   NameSays(fontName, "Bold", "Black", "Heavy", "Semibold", "Demi");
            italic = (flags & FontFlagItalic) != 0 || italicAngle != 0 || NameSays(fontName, "Italic", "Oblique");
            // У Type3 «встроенный» шрифт — это процедуры рисования, файла шрифта нет.
            ulong dataLength = 0;
            type3 = fpdf_edit.FPDFFontGetIsEmbedded(font) == 1 &&
                    fpdf_edit.FPDFFontGetFontData(font, null, 0, ref dataLength) != 0 && dataLength == 0;
        }

        var value = text.ToString();
        return new PageTextObject(
            node.Path, value, Displayed(frame, l, b, r, t), dx, dy, angle, fontSize, fontName,
            bold, italic, (ca << 24) | ((cr & 0xFF) << 16) | ((cg & 0xFF) << 8) | (cb & 0xFF),
            mode is TextRenderInvisible or TextRenderClip, type3, node.Path.Length > 1,
            advances, PageTextFingerprint.Of(value, ox, oy, fontSize));
    }

    private static bool NameSays(string name, params string[] words) =>
        words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static uint PathColor(FpdfPageobjectT obj)
    {
        var fill = 0;
        var stroke = 0;
        fpdf_edit.FPDFPathGetDrawMode(obj, ref fill, ref stroke);
        uint r = 0, g = 0, b = 0, a = 255;
        if (fill != 0)
            fpdf_edit.FPDFPageObjGetFillColor(obj, ref r, ref g, ref b, ref a);
        else
            fpdf_edit.FPDFPageObjGetStrokeColor(obj, ref r, ref g, ref b, ref a);
        return (a << 24) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
    }

    /// <summary>Прямоугольник содержимого → описанная рамка в отображаемых пунктах.</summary>
    private static OverlayBox Displayed(PageFrame frame, double left, double bottom, double right, double top)
    {
        var corners = new[]
        {
            frame.ToDisplayed(left, bottom), frame.ToDisplayed(right, bottom),
            frame.ToDisplayed(right, top), frame.ToDisplayed(left, top),
        };
        var (minX, maxX) = (corners.Min(c => c.X), corners.Max(c => c.X));
        var (minY, maxY) = (corners.Min(c => c.Y), corners.Max(c => c.Y));
        return new OverlayBox(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Границы таблиц — по тем же правилам, что для экспорта, но в координатах
    /// страницы с учётом форм и уже повёрнутые к показу.
    /// </summary>
    private static void AddRulings(PdfObjectTree.Node node, PageFrame frame,
        double left, double bottom, double right, double top, List<PageRuling> rulings)
    {
        var (width, height) = (right - left, top - bottom);
        if (double.IsNaN(width) || double.IsNaN(height))
            return;
        if (height <= MaxRulingThicknessPt && width >= MinRulingLengthPt)
        {
            Add(left, (top + bottom) / 2, right, (top + bottom) / 2);
            return;
        }
        if (width <= MaxRulingThicknessPt && height >= MinRulingLengthPt)
        {
            Add((left + right) / 2, bottom, (left + right) / 2, top);
            return;
        }
        if (!IsStrokedRectangle(node, left, bottom, right, top))
            return;
        if (width >= MinRulingLengthPt)
        {
            Add(left, top, right, top);
            Add(left, bottom, right, bottom);
        }
        if (height >= MinRulingLengthPt)
        {
            Add(left, bottom, left, top);
            Add(right, bottom, right, top);
        }

        void Add(double x1, double y1, double x2, double y2)
        {
            var (ax, ay) = frame.ToDisplayed(x1, y1);
            var (bx, by) = frame.ToDisplayed(x2, y2);
            rulings.Add(Math.Abs(by - ay) <= Math.Abs(bx - ax)
                ? new PageRuling(true, (ay + by) / 2, Math.Min(ax, bx), Math.Max(ax, bx))
                : new PageRuling(false, (ax + bx) / 2, Math.Min(ay, by), Math.Max(ay, by)));
        }
    }

    /// <summary>Обведённый прямоугольник: 4–6 прямых отрезков, все узлы — в углах рамки.</summary>
    private static bool IsStrokedRectangle(PdfObjectTree.Node node, double left, double bottom, double right, double top)
    {
        var obj = node.Object;
        var fill = 0;
        var stroke = 0;
        if (fpdf_edit.FPDFPathGetDrawMode(obj, ref fill, ref stroke) == 0 || stroke == 0)
            return false;
        var segments = fpdf_edit.FPDFPathCountSegments(obj);
        if (segments is < 4 or > 6)
            return false;
        // Узлы пути — в его собственных координатах: до страницы их ведут
        // матрица пути и матрицы форм над ним.
        var matrix = new FS_MATRIX_();
        var toPage = fpdf_edit.FPDFPageObjGetMatrix(obj, matrix) != 0
            ? new PdfObjectTree.Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F).Then(node.ToPage)
            : node.ToPage;
        const double corner = 1.0;
        for (var i = 0; i < segments; i++)
        {
            var segment = fpdf_edit.FPDFPathGetPathSegment(obj, i);
            if (segment == null || segment.__Instance == IntPtr.Zero || fpdf_edit.FPDFPathSegmentGetType(segment) == 1)
                return false;
            float sx = 0, sy = 0;
            if (fpdf_edit.FPDFPathSegmentGetPoint(segment, ref sx, ref sy) == 0)
                return false;
            var (x, y) = toPage.Apply(sx, sy);
            var atX = Math.Abs(x - left) <= corner || Math.Abs(x - right) <= corner;
            var atY = Math.Abs(y - bottom) <= corner || Math.Abs(y - top) <= corner;
            if (!atX || !atY)
                return false;
        }
        return true;
    }
}
