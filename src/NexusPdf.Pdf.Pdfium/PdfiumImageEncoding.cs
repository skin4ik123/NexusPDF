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
        var access = new FPDF_FILEACCESS
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
