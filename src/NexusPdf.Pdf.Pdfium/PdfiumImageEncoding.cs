using System.Runtime.InteropServices;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

internal static class PdfiumImageEncoding
{
    // BGRx keeps the same four-byte stride without introducing a soft mask.
    // A real alpha channel must remain BGRA, including fully transparent pixels.
    public static int BitmapFormat(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 255) return 4;
        return 3;
    }

    /// <summary>Прозрачность по альфе ОТРЕНДЕРЕННОГО битмапа: /SMask вкомпонована именно в него.</summary>
    public static bool HasTransparency(FpdfDocumentT doc, FpdfPageT page, FpdfPageobjectT obj)
    {
        var rendered = fpdf_edit.FPDFImageObjGetRenderedBitmap(doc, page, obj);
        if (rendered == null || rendered.__Instance == IntPtr.Zero)
            return true; // не смогли проверить — не рискуем
        try
        {
            var format = fpdfview.FPDFBitmapGetFormat(rendered);
            if (format != 4)
                return false; // без альфа-канала прозрачности нет
            var width = fpdfview.FPDFBitmapGetWidth(rendered);
            var height = fpdfview.FPDFBitmapGetHeight(rendered);
            var stride = fpdfview.FPDFBitmapGetStride(rendered);
            var buffer = fpdfview.FPDFBitmapGetBuffer(rendered);
            if (buffer == IntPtr.Zero)
                return true;
            unsafe
            {
                var src = (byte*)buffer;
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        if (row[x * 4 + 3] != 0xFF)
                            return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(rendered);
        }
    }

    public static unsafe bool LoadJpeg(FpdfPageT page, FpdfPageobjectT obj, byte[] jpeg)
    {
        int GetBlock(IntPtr _, ulong position, byte* buffer, ulong size)
        {
            if (position > (ulong)jpeg.Length || size > (ulong)jpeg.Length - position)
                return 0;
            Marshal.Copy(jpeg, (int)position, (IntPtr)buffer, (int)size);
            return 1;
        }

        var getBlock = new PDFiumCore.Delegates.Func_int___IntPtr_ulong_bytePtr_ulong(GetBlock);
        using var access = new FPDF_FILEACCESS
        {
            MFileLen = (uint)jpeg.Length,
            MGetBlock = getBlock,
            MParam = IntPtr.Zero,
        };
        // Inline loading copies the JPEG bytes before the callback goes away.
        var loaded = fpdf_edit.FPDFImageObjLoadJpegFileInline(page, 1, obj, access) != 0;
        GC.KeepAlive(getBlock);
        return loaded;
    }
}
