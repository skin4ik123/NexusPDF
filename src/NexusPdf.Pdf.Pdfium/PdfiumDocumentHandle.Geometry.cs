using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Векторная геометрия страницы для привязки курсора при измерении.
/// </summary>
internal sealed partial class PdfiumDocumentHandle
{
    // Коды из fpdf_edit.h (FPDF_SEGMENT_*): LINETO 0, BEZIERTO 1, MOVETO 2.
    private const int SegmentLine = 0;
    private const int SegmentBezier = 1;
    private const int SegmentMove = 2;

    /// <summary>Короче этого отрезок — шум сглаживания, а не линия чертежа.</summary>
    private const double MinSegmentPt = 0.05;

    /// <summary>
    /// Залитый без обводки путь с рамкой меньше этого — контур буквы: чертёж из
    /// САПР пишет текст кривыми, и привязка к нему только мешает попасть в
    /// линию под надписью.
    /// </summary>
    private const double GlyphOutlineMaxDiagonalPt = 12;

    /// <summary>На сколько отрезков раскладывается кривая Безье.</summary>
    private const int BezierSteps = 8;

    public Task<IReadOnlyList<PdfSegment>> GetPageSegmentsAsync(int pageIndex, int extraQuarterTurns, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfSegment>>(
                () => CollectSegments(pageIndex, extraQuarterTurns, ct), ct);
        }
    }

    private IReadOnlyList<PdfSegment> CollectSegments(int pageIndex, int extraQuarterTurns, CancellationToken ct)
    {
        var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
        if (page == null || page.__Instance == IntPtr.Zero)
            return Array.Empty<PdfSegment>();
        try
        {
            var frame = PageFrame.Of(page, extraQuarterTurns);
            var result = new List<PdfSegment>(4096);
            var count = fpdf_edit.FPDFPageCountObjects(page);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var obj = fpdf_edit.FPDFPageGetObject(page, i);
                if (obj == null || obj.__Instance == IntPtr.Zero) continue;
                CollectSegmentsFrom(obj, PdfObjectTree.Transform.Identity, frame, result, 0, ct);
            }
            return result;
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Точки сегментов PDFium отдаёт в системе координат самого пути, а не
    /// страницы: сверху лежит матрица объекта, а над ней — матрицы всех форм,
    /// внутри которых он нарисован. Без их перемножения чертёж, завёрнутый в
    /// Form XObject (так экспортируют многие САПР), дал бы отрезки не там.
    /// </summary>
    private static void CollectSegmentsFrom(
        FpdfPageobjectT obj, PdfObjectTree.Transform toPage, PageFrame frame,
        List<PdfSegment> result, int depth, CancellationToken ct)
    {
        var type = fpdf_edit.FPDFPageObjGetType(obj);
        if (type == PageObjectForm)
        {
            if (depth >= MaxFormDepth) return;
            var inner = toPage;
            var formMatrix = new FS_MATRIX_();
            if (fpdf_edit.FPDFPageObjGetMatrix(obj, formMatrix) != 0)
            {
                inner = new PdfObjectTree.Transform(
                    formMatrix.A, formMatrix.B, formMatrix.C, formMatrix.D, formMatrix.E, formMatrix.F).Then(toPage);
            }
            var children = fpdf_edit.FPDFFormObjCountObjects(obj);
            for (var i = 0; i < children; i++)
            {
                ct.ThrowIfCancellationRequested();
                var child = fpdf_edit.FPDFFormObjGetObject(obj, (uint)i);
                if (child == null || child.__Instance == IntPtr.Zero) continue;
                CollectSegmentsFrom(child, inner, frame, result, depth + 1, ct);
            }
            return;
        }

        if (type != PageObjectPath || !IsSnappable(obj)) return;

        var total = toPage;
        var matrix = new FS_MATRIX_();
        if (fpdf_edit.FPDFPageObjGetMatrix(obj, matrix) != 0)
            total = new PdfObjectTree.Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F).Then(toPage);

        var segments = fpdf_edit.FPDFPathCountSegments(obj);
        double startX = 0, startY = 0, currentX = 0, currentY = 0;
        var hasCurrent = false;
        // Кривая Безье приходит тремя точками подряд: две управляющие и конец.
        Span<double> bezier = stackalloc double[4];
        var bezierCount = 0;

        for (var k = 0; k < segments; k++)
        {
            var segment = fpdf_edit.FPDFPathGetPathSegment(obj, k);
            if (segment == null || segment.__Instance == IntPtr.Zero) continue;
            float px = 0, py = 0;
            if (fpdf_edit.FPDFPathSegmentGetPoint(segment, ref px, ref py) == 0) continue;
            var (x, y) = total.Apply(px, py);

            switch (fpdf_edit.FPDFPathSegmentGetType(segment))
            {
                case SegmentMove:
                    bezierCount = 0;
                    startX = currentX = x;
                    startY = currentY = y;
                    hasCurrent = true;
                    break;
                case SegmentLine:
                    bezierCount = 0;
                    if (hasCurrent)
                        Add(result, frame, currentX, currentY, x, y);
                    else
                    {
                        startX = x;
                        startY = y;
                    }
                    currentX = x;
                    currentY = y;
                    hasCurrent = true;
                    break;
                case SegmentBezier:
                    if (!hasCurrent)
                    {
                        startX = currentX = x;
                        startY = currentY = y;
                        hasCurrent = true;
                        break;
                    }
                    if (bezierCount < 4)
                    {
                        bezier[bezierCount++] = x;
                        bezier[bezierCount++] = y;
                        break;
                    }
                    Flatten(result, frame, currentX, currentY, bezier[0], bezier[1], bezier[2], bezier[3], x, y);
                    bezierCount = 0;
                    currentX = x;
                    currentY = y;
                    break;
            }

            if (fpdf_edit.FPDFPathSegmentGetClose(segment) != 0 && hasCurrent)
            {
                Add(result, frame, currentX, currentY, startX, startY);
                currentX = startX;
                currentY = startY;
            }
        }
    }

    /// <summary>
    /// Путь годится для привязки, если он виден и не похож на контур буквы.
    /// Невидимые (без заливки и обводки) пути — вспомогательная геометрия,
    /// к которой курсор прилипал бы на пустом месте.
    /// </summary>
    private static bool IsSnappable(FpdfPageobjectT obj)
    {
        var fillMode = 0;
        var stroke = 0;
        if (fpdf_edit.FPDFPathGetDrawMode(obj, ref fillMode, ref stroke) == 0) return false;
        if (fillMode == 0 && stroke == 0) return false;

        if (stroke != 0)
        {
            uint r = 0, g = 0, b = 0, a = 0;
            if (fpdf_edit.FPDFPageObjGetStrokeColor(obj, ref r, ref g, ref b, ref a) != 0 && a == 0 && fillMode == 0)
                return false;
            return true;
        }

        uint fr = 0, fg = 0, fb = 0, fa = 0;
        if (fpdf_edit.FPDFPageObjGetFillColor(obj, ref fr, ref fg, ref fb, ref fa) != 0 && fa == 0)
            return false;

        float left = 0, bottom = 0, right = 0, top = 0;
        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) == 0) return true;
        var w = right - left;
        var h = top - bottom;
        return Math.Sqrt(w * w + h * h) >= GlyphOutlineMaxDiagonalPt;
    }

    private static void Add(List<PdfSegment> result, PageFrame frame, double x1, double y1, double x2, double y2)
    {
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2)) return;
        var (dx1, dy1) = frame.ToDisplayed(x1, y1);
        var (dx2, dy2) = frame.ToDisplayed(x2, y2);
        var segment = new PdfSegment(dx1, dy1, dx2, dy2);
        if (segment.Length < MinSegmentPt) return;
        result.Add(segment);
    }

    private static void Flatten(
        List<PdfSegment> result, PageFrame frame,
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var previousX = x0;
        var previousY = y0;
        for (var i = 1; i <= BezierSteps; i++)
        {
            var t = (double)i / BezierSteps;
            var u = 1 - t;
            var x = u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
            var y = u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
            Add(result, frame, previousX, previousY, x, y);
            previousX = x;
            previousY = y;
        }
    }
}
