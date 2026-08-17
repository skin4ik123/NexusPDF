using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Шрифты, встроенные в один компонуемый документ.
///
/// Кэш нужен не ради скорости: FPDFTextLoadFont каждым вызовом добавляет в
/// документ ЕЩЁ ОДИН объект шрифта. Без кэша страница из десяти надписей
/// одной гарнитурой встроила бы её десять раз, и файл распух бы на ровном
/// месте. Ключ — путь к файлу, поэтому «Arial обычный» с двух разных страниц
/// встраивается один раз.
/// </summary>
internal sealed class OverlayFontCache(FpdfDocumentT document)
{
    private const int FpdfFontTrueType = 2;

    private readonly Dictionary<string, FpdfFontT?> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Шрифт по умолчанию. null — если в системе нет ни одной гарнитуры каталога.</summary>
    public FpdfFontT? Default => Load(PdfFontCatalog.ResolveDefaultPath());

    /// <summary>
    /// Шрифт запрошенной гарнитуры и начертания. Неизвестная гарнитура
    /// откатывается на шрифт по умолчанию: текст должен появиться в документе
    /// даже тогда, когда выбранного шрифта в системе не оказалось.
    /// </summary>
    public FpdfFontT? For(string family, bool bold, bool italic)
    {
        if (!string.IsNullOrWhiteSpace(family))
        {
            var font = Load(PdfFontCatalog.ResolvePath(family, bold, italic));
            if (font != null)
                return font;
        }
        return Load(PdfFontCatalog.ResolveDefaultPath(bold, italic)) ?? Default;
    }

    /// <summary>
    /// Освобождает все загруженные шрифты. Вызывается ПОСЛЕ сохранения: до
    /// этого момента объекты страниц на них ещё ссылаются.
    /// </summary>
    public void CloseAll()
    {
        foreach (var font in _byPath.Values)
        {
            if (font != null)
                fpdf_edit.FPDFFontClose(font);
        }
        _byPath.Clear();
    }

    private unsafe FpdfFontT? Load(string? path)
    {
        if (path == null)
            return null;
        if (_byPath.TryGetValue(path, out var cached))
            return cached;

        FpdfFontT? font = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            fixed (byte* data = bytes)
            {
                var loaded = fpdf_edit.FPDFTextLoadFont(
                    document, data, (uint)bytes.Length, FpdfFontTrueType, 1);
                if (loaded != null && loaded.__Instance != IntPtr.Zero)
                    font = loaded;
            }
        }
        catch (IOException)
        {
            // Файл шрифта занят или недоступен — это не повод ронять сохранение
            // всего документа: вызывающий откатится на шрифт по умолчанию.
        }
        catch (UnauthorizedAccessException)
        {
        }

        _byPath[path] = font;
        return font;
    }
}
