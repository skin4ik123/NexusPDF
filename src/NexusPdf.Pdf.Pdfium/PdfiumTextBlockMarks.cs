using System.Runtime.InteropServices;
using System.Text;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>Параметры метки NexusTextBlock одного объекта страницы.</summary>
internal sealed record TextBlockMark(string Id, string? Version, string? Ref, string? Data, string? Hash);

/// <summary>
/// Метки текстовых блоков на объектах страницы. Значения меток пишет кто
/// угодно, поэтому чтение ограничено по размеру и ничего не бросает.
/// </summary>
internal static class PdfiumTextBlockMarks
{
    public const string Name = "NexusTextBlock";

    // Пределы длины значений (байты UTF-16). Описание блока — до миллиона
    // знаков текста в длинах фрагментов, этого хватает с запасом.
    private const int MaxShortValueBytes = 256;
    private const int MaxDataBytes = 32 * 1024 * 1024;
    private const int MaxMarksPerObject = 64;

    public static IntPtr Add(FpdfPageobjectT obj)
    {
        var mark = NativeAddMark(obj.__Instance, Utf8Z(Name));
        if (mark == IntPtr.Zero)
            throw new PdfEngineException("Не удалось пометить объект текстового блока.");
        return mark;
    }

    public static void Set(FpdfDocumentT document, FpdfPageobjectT obj, IntPtr mark, string key, string value)
    {
        if (NativeSetMarkString(document.__Instance, obj.__Instance, mark, Utf8Z(key), Utf8Z(value)) == 0)
            throw new PdfEngineException("Не удалось записать описание текстового блока.");
    }

    /// <summary>Метка блока на объекте; null — объект не из блока или у метки нет Id.</summary>
    public static TextBlockMark? Read(FpdfPageobjectT obj)
    {
        var count = Math.Min(NativeCountMarks(obj.__Instance), MaxMarksPerObject);
        for (var i = 0; i < count; i++)
        {
            var mark = NativeGetMark(obj.__Instance, (ulong)i);
            if (mark == IntPtr.Zero || ReadName(mark) != Name)
                continue;
            var id = ReadParam(mark, "Id", MaxShortValueBytes);
            if (string.IsNullOrEmpty(id))
                return null;
            return new TextBlockMark(id,
                ReadParam(mark, "V", MaxShortValueBytes),
                ReadParam(mark, "R", MaxShortValueBytes),
                ReadParam(mark, "Data", MaxDataBytes),
                ReadParam(mark, "Hash", MaxShortValueBytes));
        }
        return null;
    }

    private static string? ReadName(IntPtr mark)
    {
        ulong needed = 0;
        if (NativeGetName(mark, null, 0, ref needed) == 0 || needed < 2 || needed > MaxShortValueBytes)
            return null;
        var buffer = new byte[needed];
        ulong written = 0;
        return NativeGetName(mark, buffer, needed, ref written) == 0 ? null : Utf16(buffer, written);
    }

    /// <summary>Строковый параметр метки; null — нет такого, не строка или слишком длинный.</summary>
    private static string? ReadParam(IntPtr mark, string key, int maxBytes)
    {
        var name = Utf8Z(key);
        ulong needed = 0;
        if (NativeGetParamString(mark, name, null, 0, ref needed) == 0 || needed > (ulong)maxBytes)
            return null;
        if (needed < 2)
            return "";
        var buffer = new byte[needed];
        ulong written = 0;
        return NativeGetParamString(mark, name, buffer, needed, ref written) == 0 ? null : Utf16(buffer, written);
    }

    private static string Utf16(byte[] buffer, ulong written)
    {
        var length = (int)Math.Min(written, (ulong)buffer.Length) & ~1;
        return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
    }

    private static byte[] Utf8Z(string value) => Encoding.UTF8.GetBytes(value + "\0");

    // Свои объявления: строки уходят и приходят байтами как есть, а
    // unsigned long передаётся 64-битным — на Windows PDFium читает и пишет
    // младшие 4 байта, на Linux все 8, обнулённый ulong верен в обоих случаях.
    [DllImport("pdfium", EntryPoint = "FPDFPageObj_AddMark", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NativeAddMark(IntPtr pageObject, byte[] name);

    [DllImport("pdfium", EntryPoint = "FPDFPageObjMark_SetStringParam", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeSetMarkString(IntPtr document, IntPtr pageObject, IntPtr mark, byte[] key, byte[] value);

    [DllImport("pdfium", EntryPoint = "FPDFPageObj_CountMarks", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeCountMarks(IntPtr pageObject);

    [DllImport("pdfium", EntryPoint = "FPDFPageObj_GetMark", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NativeGetMark(IntPtr pageObject, ulong index);

    [DllImport("pdfium", EntryPoint = "FPDFPageObjMark_GetName", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeGetName(IntPtr mark, byte[]? buffer, ulong length, ref ulong outLength);

    [DllImport("pdfium", EntryPoint = "FPDFPageObjMark_GetParamStringValue", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeGetParamString(IntPtr mark, byte[] key, byte[]? buffer, ulong length, ref ulong outLength);
}
