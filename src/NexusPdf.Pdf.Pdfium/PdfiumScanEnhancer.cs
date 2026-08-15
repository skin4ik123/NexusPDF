using System.Runtime.InteropServices;
using NexusPdf.Imaging;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Улучшение отсканированных страниц: выравнивание наклона, удаление мусора
/// сканера, выравнивание фона.
///
/// Наклон исправляется НЕ перерисовкой страницы в картинку, а поворотом всех
/// её объектов. Это принципиально: страница-скан обычно несёт ещё и невидимый
/// текстовый слой распознавания, и растеризация убила бы поиск по документу, а
/// заодно и качество. Поворот объектов сохраняет и то, и другое — пиксели
/// вообще не пересчитываются.
///
/// Мусор и фон — это уже работа с пикселями, поэтому они применяются к
/// РАСТРАМ страницы: каждый крупный image-объект вынимается, чистится и
/// кладётся обратно на своё место без изменения размещения.
/// </summary>
internal static class PdfiumScanEnhancer
{
    private const int PageObjectImage = 3;

    /// <summary>DPI отрисовки для поиска наклона. Выше не уточняет, ниже — теряет строки.</summary>
    private const double DetectDpi = 120;

    /// <summary>
    /// Какую часть страницы должен занимать растр, чтобы считаться сканом.
    /// Логотип в углу договора чистить не надо — там нет шума сканера, зато
    /// есть аккуратная графика, которую чистка испортит.
    /// </summary>
    private const double ScanCoverage = 0.5;

    public static ScanEnhanceStats EnhanceCore(
        string sourcePath, string? password, string targetPath,
        ScanEnhanceOptions options, IProgress<int>? progress, CancellationToken ct)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var doc = fpdfview.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, password);
            if (doc == null || doc.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось открыть документ для улучшения сканов.");
            try
            {
                var stats = Process(doc, options, progress, ct);
                PdfiumRenderEngine.SaveDocument(doc, targetPath);
                return stats;
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(doc);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>
    /// Измерение наклона без единой правки: окно показывает пользователю, что
    /// именно найдено, ДО того как он согласится менять документ.
    /// </summary>
    public static IReadOnlyList<PageSkew> MeasureSkewCore(
        string sourcePath, string? password, IReadOnlyList<int>? pages, CancellationToken ct)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var doc = fpdfview.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, password);
            if (doc == null || doc.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось открыть документ для разбора наклона.");
            try
            {
                var result = new List<PageSkew>();
                var pageCount = fpdfview.FPDF_GetPageCount(doc);
                var wanted = pages is { Count: > 0 } ? new HashSet<int>(pages) : null;
                for (var index = 0; index < pageCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (wanted != null && !wanted.Contains(index)) continue;
                    var page = fpdfview.FPDF_LoadPage(doc, index);
                    if (page == null || page.__Instance == IntPtr.Zero) continue;
                    try
                    {
                        var skew = DetectSkew(page, ct);
                        result.Add(new PageSkew(index, skew.AngleDegrees, skew.Confidence));
                    }
                    finally
                    {
                        fpdfview.FPDF_ClosePage(page);
                    }
                }
                return result;
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(doc);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    private static ScanEnhanceStats Process(
        FpdfDocumentT doc, ScanEnhanceOptions options, IProgress<int>? progress, CancellationToken ct)
    {
        var pageCount = fpdfview.FPDF_GetPageCount(doc);
        var wanted = options.Pages is { Count: > 0 } ? new HashSet<int>(options.Pages) : null;
        var processed = 0;
        var straightened = 0;
        var maxAngle = 0.0;
        var imagesCleaned = 0;
        var speckles = 0;

        for (var index = 0; index < pageCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (wanted != null && !wanted.Contains(index)) continue;

            var page = fpdfview.FPDF_LoadPage(doc, index);
            if (page == null || page.__Instance == IntPtr.Zero)
                continue;
            try
            {
                processed++;
                var changed = false;

                if (options.Deskew)
                {
                    var skew = DetectSkew(page, ct);
                    if (skew.IsWorthFixing)
                    {
                        RotatePageObjects(page, skew.AngleDegrees);
                        straightened++;
                        maxAngle = Math.Max(maxAngle, Math.Abs(skew.AngleDegrees));
                        changed = true;
                    }
                }

                if (options.Despeckle || options.LevelBackground)
                {
                    var (cleaned, removed) = CleanPageImages(doc, page, options, ct);
                    imagesCleaned += cleaned;
                    speckles += removed;
                    changed |= cleaned > 0;
                }

                if (changed && fpdf_edit.FPDFPageGenerateContent(page) == 0)
                    throw new PdfEngineException($"Не удалось сохранить правки страницы {index + 1}.");
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
            progress?.Report(index + 1);
        }

        return new ScanEnhanceStats(processed, straightened, maxAngle, imagesCleaned, speckles);
    }

    /// <summary>Наклон страницы по её отрисовке — так видно и картинку, и текстовый слой.</summary>
    public static SkewEstimate DetectSkew(FpdfPageT page, CancellationToken ct)
    {
        var widthPt = fpdfview.FPDF_GetPageWidthF(page);
        var heightPt = fpdfview.FPDF_GetPageHeightF(page);
        if (widthPt < 1 || heightPt < 1) return new SkewEstimate(0, 0);

        var width = Math.Clamp((int)Math.Round(widthPt / 72.0 * DetectDpi), 64, 2000);
        var height = Math.Clamp((int)Math.Round(heightPt / 72.0 * DetectDpi), 64, 2600);

        var bitmap = fpdfview.FPDFBitmapCreate(width, height, 0);
        if (bitmap == null || bitmap.__Instance == IntPtr.Zero) return new SkewEstimate(0, 0);
        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);
            ct.ThrowIfCancellationRequested();

            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
            if (buffer == IntPtr.Zero) return new SkewEstimate(0, 0);

            // Сразу в полутон: детектору цвет не нужен, а копировать вчетверо
            // больше памяти на каждой странице — заметная разница на 300 листах.
            var gray = new GrayImage(width, height);
            var row = new byte[stride];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(buffer + y * stride, row, 0, stride);
                for (var x = 0; x < width; x++)
                {
                    var o = x * 4;
                    gray[x, y] = (byte)((row[o + 2] * 299 + row[o + 1] * 587 + row[o] * 114) / 1000);
                }
            }
            return SkewDetector.Detect(gray);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    /// <summary>
    /// Поворот ВСЕГО содержимого страницы вокруг её центра. Матрица считается
    /// в точках PDF (ось Y вверх), поэтому знак угла здесь обратный экранному.
    /// </summary>
    private static void RotatePageObjects(FpdfPageT page, double angleDegrees)
    {
        var widthPt = fpdfview.FPDF_GetPageWidthF(page);
        var heightPt = fpdfview.FPDF_GetPageHeightF(page);
        var radians = -angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = widthPt / 2.0;
        var cy = heightPt / 2.0;

        // Поворот вокруг центра = перенос в центр, поворот, перенос обратно.
        var e = cx - cx * cos + cy * sin;
        var f = cy - cx * sin - cy * cos;

        var count = fpdf_edit.FPDFPageCountObjects(page);
        for (var i = 0; i < count; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj == null || obj.__Instance == IntPtr.Zero) continue;
            fpdf_edit.FPDFPageObjTransform(obj, cos, sin, -sin, cos, e, f);
        }
    }

    /// <summary>Чистка растров страницы; возвращает (сколько картинок, сколько пятен).</summary>
    private static (int Cleaned, int Speckles) CleanPageImages(
        FpdfDocumentT doc, FpdfPageT page, ScanEnhanceOptions options, CancellationToken ct)
    {
        var pageArea = (double)fpdfview.FPDF_GetPageWidthF(page) * fpdfview.FPDF_GetPageHeightF(page);
        if (pageArea <= 0) return (0, 0);

        var cleaned = 0;
        var speckles = 0;
        var count = fpdf_edit.FPDFPageCountObjects(page);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj == null || obj.__Instance == IntPtr.Zero ||
                fpdf_edit.FPDFPageObjGetType(obj) != PageObjectImage)
                continue;

            // Только крупные растры: страница-скан — это картинка во весь лист.
            var matrix = new FS_MATRIX_();
            if (fpdf_edit.FPDFPageObjGetMatrix(obj, matrix) == 0) continue;
            var placedArea = Math.Abs((double)matrix.A * matrix.D - (double)matrix.B * matrix.C);
            if (placedArea < pageArea * ScanCoverage) continue;

            var bitmap = fpdf_edit.FPDFImageObjGetBitmap(obj);
            if (bitmap == null || bitmap.__Instance == IntPtr.Zero) continue;
            byte[]? bgra;
            int width, height;
            try
            {
                width = fpdfview.FPDFBitmapGetWidth(bitmap);
                height = fpdfview.FPDFBitmapGetHeight(bitmap);
                bgra = ToBgra(bitmap, width, height);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }
            if (bgra == null || width < 32 || height < 32) continue;

            if (options.LevelBackground)
                ScanCleanup.LevelBackground(bgra, width, height);
            if (options.Despeckle)
                speckles += ScanCleanup.Despeckle(bgra, width, height, options.MaxSpeckleArea);

            if (WriteBitmap(page, obj, bgra, width, height))
                cleaned++;
        }
        return (cleaned, speckles);
    }

    private static byte[]? ToBgra(FpdfBitmapT bitmap, int width, int height)
    {
        if (width < 1 || height < 1) return null;
        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
        var format = fpdfview.FPDFBitmapGetFormat(bitmap);
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        if (buffer == IntPtr.Zero) return null;

        var result = new byte[(long)width * height * 4];
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(buffer + y * stride, row, 0, stride);
            var target = (long)y * width * 4;
            switch (format)
            {
                case 1: // серый
                    for (var x = 0; x < width; x++)
                    {
                        var v = row[x];
                        result[target + x * 4] = v;
                        result[target + x * 4 + 1] = v;
                        result[target + x * 4 + 2] = v;
                        result[target + x * 4 + 3] = 255;
                    }
                    break;
                case 2: // BGR
                    for (var x = 0; x < width; x++)
                    {
                        result[target + x * 4] = row[x * 3];
                        result[target + x * 4 + 1] = row[x * 3 + 1];
                        result[target + x * 4 + 2] = row[x * 3 + 2];
                        result[target + x * 4 + 3] = 255;
                    }
                    break;
                case 3: // BGRx
                case 4: // BGRA
                    for (var x = 0; x < width; x++)
                    {
                        result[target + x * 4] = row[x * 4];
                        result[target + x * 4 + 1] = row[x * 4 + 1];
                        result[target + x * 4 + 2] = row[x * 4 + 2];
                        result[target + x * 4 + 3] = format == 4 ? row[x * 4 + 3] : (byte)255;
                    }
                    break;
                default:
                    return null;
            }
        }
        return result;
    }

    private static bool WriteBitmap(
        FpdfPageT page, FpdfPageobjectT obj, byte[] bgra, int width, int height)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var bitmap = fpdfview.FPDFBitmapCreateEx(
                width, height, 3, handle.AddrOfPinnedObject(), width * 4);
            if (bitmap == null || bitmap.__Instance == IntPtr.Zero) return false;
            try
            {
                return fpdf_edit.FPDFImageObjSetBitmap(page, 1, obj, bitmap) != 0;
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
