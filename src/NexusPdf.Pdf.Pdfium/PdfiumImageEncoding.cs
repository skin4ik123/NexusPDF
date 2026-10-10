using System.Runtime.InteropServices;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

internal static class PdfiumImageEncoding
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WindowsGetBlock(IntPtr parameter, uint position, IntPtr buffer, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileAccess
    {
        public uint FileLength;
        public IntPtr GetBlock;
        public IntPtr Parameter;
    }

    [DllImport("pdfium", EntryPoint = "FPDFImageObj_LoadJpegFileInline", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LoadInline(IntPtr pages, int count, IntPtr image, ref WindowsFileAccess access);

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
        // PDFiumCore generates unsigned long as ulong, but Windows uses a
        // 32-bit unsigned long even in x64. Read the callback using its real ABI.
        if (OperatingSystem.IsWindows())
        {
            int ReadBlock(IntPtr _, uint position, IntPtr buffer, uint size)
            {
                if (position > jpeg.Length || size > jpeg.Length - position) return 0;
                Marshal.Copy(jpeg, (int)position, buffer, (int)size);
                return 1;
            }
            var callback = new WindowsGetBlock(ReadBlock);
            var windowsAccess = new WindowsFileAccess
            {
                FileLength = (uint)jpeg.Length,
                GetBlock = Marshal.GetFunctionPointerForDelegate(callback),
            };
            var pagePointer = page.__Instance;
            var success = LoadInline((IntPtr)(&pagePointer), 1, obj.__Instance, ref windowsAccess) != 0;
            GC.KeepAlive(callback);
            Console.WriteLine($"JPEG_PROBE bytes={jpeg.Length} loaded={success} windows=true");
            return success;
        }

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
