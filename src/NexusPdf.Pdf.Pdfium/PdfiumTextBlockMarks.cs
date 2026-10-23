using System.Runtime.InteropServices;
using System.Text;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>Короткие параметры метки NexusTextBlock одного объекта страницы.</summary>
internal sealed record TextBlockMark(string Id, string? Version, string? Ref);

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

    /// <summary>
    /// Чтение меток одной страницы с общим бюджетом. PDFium перекодирует
    /// значение целиком при каждом запросе, а одно значение могут разделять
    /// тысячи объектов (словарь /Properties) — поэтому считается каждый
    /// запрошенный байт, и после исчерпания бюджета метки больше не читаются.
    /// </summary>
    public sealed class Reader(long budgetBytes = 64L * 1024 * 1024)
    {
        private long _budget = budgetBytes;

        public bool Exhausted => _budget <= 0;

        /// <summary>Метка блока на объекте; null — объект не из блока, у метки нет Id или бюджет исчерпан.</summary>
        public TextBlockMark? Read(FpdfPageobjectT obj)
        {
            if (Exhausted)
                return null;
            var mark = Find(obj);
            if (mark == IntPtr.Zero)
                return null;
            var id = ReadParam(mark, "Id", MaxShortValueBytes);
            return string.IsNullOrEmpty(id)
                ? null
                : new TextBlockMark(id, ReadParam(mark, "V", MaxShortValueBytes), ReadParam(mark, "R", MaxShortValueBytes));
        }

        /// <summary>Описание и отпечаток блока с этого объекта; Data == null — их здесь нет.</summary>
        public (string? Data, string? Hash) ReadPayload(FpdfPageobjectT obj)
        {
            if (Exhausted)
                return (null, null);
            var mark = Find(obj);
            if (mark == IntPtr.Zero)
                return (null, null);
            var data = ReadParam(mark, "Data", MaxDataBytes);
            return data == null ? (null, null) : (data, ReadParam(mark, "Hash", MaxShortValueBytes));
        }

        private IntPtr Find(FpdfPageobjectT obj)
        {
            var count = Math.Min(NativeCountMarks(obj.__Instance), MaxMarksPerObject);
            for (var i = 0; i < count && !Exhausted; i++)
            {
                var mark = NativeGetMark(obj.__Instance, (ulong)i);
                if (mark != IntPtr.Zero && ReadName(mark) == Name)
                    return mark;
            }
            return IntPtr.Zero;
        }

        private string? ReadName(IntPtr mark)
        {
            ulong needed = 0;
            if (NativeGetName(mark, null, 0, ref needed) == 0 || !Spend(needed) || needed < 2 || needed > MaxShortValueBytes)
                return null;
            var buffer = new byte[needed];
            ulong written = 0;
            return NativeGetName(mark, buffer, needed, ref written) == 0 || !Spend(needed) ? null : Utf16(buffer, written);
        }

        /// <summary>Строковый параметр метки; null — нет такого, не строка, слишком длинный или бюджет исчерпан.</summary>
        private string? ReadParam(IntPtr mark, string key, int maxBytes)
        {
            var name = Utf8Z(key);
            ulong needed = 0;
            if (NativeGetParamString(mark, name, null, 0, ref needed) == 0 || !Spend(needed) || needed > (ulong)maxBytes)
                return null;
            if (needed < 2)
                return "";
            var buffer = new byte[needed];
            ulong written = 0;
            return NativeGetParamString(mark, name, buffer, needed, ref written) == 0 || !Spend(needed)
                ? null
                : Utf16(buffer, written);
        }

        private bool Spend(ulong bytes)
        {
            _budget -= (long)Math.Min(bytes, (ulong)long.MaxValue / 2);
            return _budget >= 0;
        }
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
