using System.Runtime.InteropServices;
using System.Text;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Пересжатие изображений документа: картинки с эффективным DPI выше целевого
/// уменьшаются (box-усреднение) и кодируются в JPEG на место исходного потока
/// (FPDFImageObjLoadJpegFileInline → DCTDecode). Пропускаются изображения с
/// реальной прозрачностью (JPEG её не несёт, а /SMask остался бы от старого
/// растра) и факсовые/экзотические кодеки (CCITT/JBIG2/JPX) — их пересжатие
/// в JPEG раздувает файл или ломает вид.
/// </summary>
internal static class PdfiumImageRecompressor
{
    private const int PageObjectImage = 3; // FPDF_PAGEOBJ_IMAGE
    private const int MinSidePixels = 48;  // мелочь пересжимать бессмысленно

    public static ImageRecompressStats RecompressCore(
        string sourcePath, string? password, string targetPath, double targetDpi,
        Func<byte[], int, int, byte[]> encodeJpeg)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var doc = fpdfview.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, password);
            if (doc == null || doc.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось открыть документ для пересжатия.");
            try
            {
                var recompressed = 0;
                var skipped = 0;
                var pageCount = fpdfview.FPDF_GetPageCount(doc);
                for (var p = 0; p < pageCount; p++)
                {
                    var page = fpdfview.FPDF_LoadPage(doc, p);
                    if (page == null || page.__Instance == IntPtr.Zero)
                        throw new PdfEngineException($"Не удалось открыть страницу {p + 1}.");
                    try
                    {
                        var pageRecompressed = 0;
                        var objectCount = fpdf_edit.FPDFPageCountObjects(page);
                        for (var i = 0; i < objectCount; i++)
                        {
                            var obj = fpdf_edit.FPDFPageGetObject(page, i);
                            if (obj == null || obj.__Instance == IntPtr.Zero ||
                                fpdf_edit.FPDFPageObjGetType(obj) != PageObjectImage)
                                continue;
                            if (TryRecompressImage(page, obj, targetDpi, encodeJpeg))
                                pageRecompressed++;
                            else
                                skipped++;
                        }

                        if (pageRecompressed > 0)
                        {
                            // Без пересборки содержимого SaveAsCopy пишет
                            // страницу с ПРЕЖНИМИ потоками изображений.
                            if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                                throw new PdfEngineException(
                                    $"Не удалось зафиксировать пересжатую страницу {p + 1}.");
                            recompressed += pageRecompressed;
                        }
                    }
                    finally
                    {
                        fpdfview.FPDF_ClosePage(page);
                    }
                }

                PdfiumRenderEngine.SaveDocument(doc, targetPath);
                return new ImageRecompressStats(recompressed, skipped);
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

    private static bool TryRecompressImage(
        FpdfPageT page, FpdfPageobjectT obj, double targetDpi,
        Func<byte[], int, int, byte[]> encodeJpeg)
    {
        var meta = new FPDF_IMAGEOBJ_METADATA();
        if (fpdf_edit.FPDFImageObjGetImageMetadata(obj, page, meta) == 0)
            return false;
        if (meta.Width < MinSidePixels || meta.Height < MinSidePixels)
            return false;
        var effectiveDpi = Math.Max(meta.HorizontalDpi, meta.VerticalDpi);
        if (effectiveDpi <= 0 || effectiveDpi <= targetDpi * 1.2)
            return false; // уже достаточно компактно — трогать нечего

        if (HasUnsupportedFilter(obj))
            return false;

        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(obj);
        if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
            return false;
        try
        {
            var width = fpdfview.FPDFBitmapGetWidth(bitmap);
            var height = fpdfview.FPDFBitmapGetHeight(bitmap);
            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var format = fpdfview.FPDFBitmapGetFormat(bitmap);
            var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
            if (width < MinSidePixels || height < MinSidePixels || buffer == IntPtr.Zero)
                return false;

            var bgra = ToBgra(buffer, width, height, stride, format);
            if (bgra == null)
                return false; // формат с реальной прозрачностью или неизвестный

            var scale = targetDpi / effectiveDpi;
            var newWidth = Math.Max(1, (int)Math.Round(width * scale));
            var newHeight = Math.Max(1, (int)Math.Round(height * scale));
            if (newWidth >= width || newHeight >= height)
                return false;

            var resampled = BoxDownsample(bgra, width, height, newWidth, newHeight);
            var jpeg = encodeJpeg(resampled, newWidth, newHeight);
            return ReplaceWithJpeg(page, obj, jpeg);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    private static bool HasUnsupportedFilter(FpdfPageobjectT obj)
    {
        var count = fpdf_edit.FPDFImageObjGetImageFilterCount(obj);
        for (var i = 0; i < count; i++)
        {
            var length = fpdf_edit.FPDFImageObjGetImageFilter(obj, i, IntPtr.Zero, 0);
            if (length == 0)
                continue;
            var raw = Marshal.AllocHGlobal((int)length);
            try
            {
                fpdf_edit.FPDFImageObjGetImageFilter(obj, i, raw, length);
                var name = Marshal.PtrToStringAnsi(raw, (int)length - 1) ?? "";
                if (name is "CCITTFaxDecode" or "JBIG2Decode" or "JPXDecode")
                    return true;
            }
            finally
            {
                Marshal.FreeHGlobal(raw);
            }
        }
        return false;
    }

    /// <summary>BGRA-копия битмапа; null — если формат несёт реальную прозрачность или неизвестен.</summary>
    private static unsafe byte[]? ToBgra(IntPtr buffer, int width, int height, int stride, int format)
    {
        var result = new byte[width * height * 4];
        var src = (byte*)buffer;
        switch (format)
        {
            case 1: // Gray
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var g = row[x];
                        var o = (y * width + x) * 4;
                        result[o] = g;
                        result[o + 1] = g;
                        result[o + 2] = g;
                        result[o + 3] = 0xFF;
                    }
                }
                return result;
            case 2: // BGR
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var o = (y * width + x) * 4;
                        result[o] = row[x * 3];
                        result[o + 1] = row[x * 3 + 1];
                        result[o + 2] = row[x * 3 + 2];
                        result[o + 3] = 0xFF;
                    }
                }
                return result;
            case 3: // BGRx
            case 4: // BGRA
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var o = (y * width + x) * 4;
                        var a = row[x * 4 + 3];
                        if (format == 4 && a != 0xFF)
                            return null; // настоящая прозрачность — JPEG её потеряет
                        result[o] = row[x * 4];
                        result[o + 1] = row[x * 4 + 1];
                        result[o + 2] = row[x * 4 + 2];
                        result[o + 3] = 0xFF;
                    }
                }
                return result;
            default:
                return null;
        }
    }

    /// <summary>Уменьшение box-усреднением: каждый целевой пиксель — среднее прямоугольника источника.</summary>
    private static byte[] BoxDownsample(byte[] bgra, int width, int height, int newWidth, int newHeight)
    {
        var result = new byte[newWidth * newHeight * 4];
        for (var y = 0; y < newHeight; y++)
        {
            var srcY0 = y * height / newHeight;
            var srcY1 = Math.Max(srcY0 + 1, (y + 1) * height / newHeight);
            for (var x = 0; x < newWidth; x++)
            {
                var srcX0 = x * width / newWidth;
                var srcX1 = Math.Max(srcX0 + 1, (x + 1) * width / newWidth);
                long b = 0, g = 0, r = 0;
                var n = 0;
                for (var sy = srcY0; sy < srcY1; sy++)
                {
                    for (var sx = srcX0; sx < srcX1; sx++)
                    {
                        var o = (sy * width + sx) * 4;
                        b += bgra[o];
                        g += bgra[o + 1];
                        r += bgra[o + 2];
                        n++;
                    }
                }
                var t = (y * newWidth + x) * 4;
                result[t] = (byte)(b / n);
                result[t + 1] = (byte)(g / n);
                result[t + 2] = (byte)(r / n);
                result[t + 3] = 0xFF;
            }
        }
        return result;
    }

    private static unsafe bool ReplaceWithJpeg(FpdfPageT page, FpdfPageobjectT obj, byte[] jpeg)
    {
        int GetBlock(IntPtr _, ulong position, byte* pBuf, ulong size)
        {
            if (position + size > (ulong)jpeg.Length || size > int.MaxValue)
                return 0;
            Marshal.Copy(jpeg, (int)position, (IntPtr)pBuf, (int)size);
            return 1;
        }

        var getBlock = new PDFiumCore.Delegates.Func_int___IntPtr_ulong_bytePtr_ulong(GetBlock);
        var access = new FPDF_FILEACCESS
        {
            MFileLen = (uint)jpeg.Length,
            MGetBlock = getBlock,
            MParam = IntPtr.Zero,
        };
        var ok = fpdf_edit.FPDFImageObjLoadJpegFileInline(page, 1, obj, access) != 0;
        GC.KeepAlive(getBlock);
        return ok;
    }
}
