using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
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
internal sealed class OverlayFontCache(FpdfDocumentT document, IFontProvider provider)
{
    private const int FpdfFontTrueType = 2;

    private readonly Dictionary<string, FpdfFontT?> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<FontFaceRef, FpdfFontT?> _byFace = new();

    /// <summary>Каталог, по которому верстаются текстовые блоки.</summary>
    public IFontProvider Provider => provider;

    /// <summary>Шрифт по умолчанию. null — если в системе нет ни одной гарнитуры каталога.</summary>
    public FpdfFontT? Default =>
        FromProvider(FontCatalog.DefaultFamily, false, false) ?? Load(PdfFontCatalog.ResolveDefaultPath());

    /// <summary>
    /// Шрифт запрошенной гарнитуры и начертания — из того же каталога, что и
    /// список выбора (в том числе начертания из коллекций .ttc). Неизвестная
    /// гарнитура откатывается на шрифт по умолчанию: текст должен появиться в
    /// документе даже тогда, когда выбранного шрифта в системе не оказалось.
    /// </summary>
    public FpdfFontT? For(string family, bool bold, bool italic) =>
        FromProvider(family, bold, italic)
        ?? (string.IsNullOrWhiteSpace(family) ? null : Load(PdfFontCatalog.ResolvePath(family, bold, italic)))
        ?? FromProvider(FontCatalog.DefaultFamily, bold, italic)
        ?? Load(PdfFontCatalog.ResolveDefaultPath(bold, italic))
        ?? Default;

    /// <summary>Начертание из каталога, если его можно вписать в PDF.</summary>
    private FpdfFontT? FromProvider(string family, bool bold, bool italic) =>
        !string.IsNullOrWhiteSpace(family) &&
        provider.Resolve(family, bold, italic) is { Embeddable: true, Supported: true } face
            ? ForFace(face.Ref)
            : null;

    /// <summary>
    /// Освобождает все загруженные шрифты. Вызывается ПОСЛЕ сохранения: до
    /// этого момента объекты страниц на них ещё ссылаются.
    /// </summary>
    public void CloseAll()
    {
        foreach (var font in _byPath.Values.Concat(_byFace.Values))
        {
            if (font != null)
                fpdf_edit.FPDFFontClose(font);
        }
        _byPath.Clear();
        _byFace.Clear();
    }

    /// <summary>
    /// Шрифт одного начертания из каталога блоков. Встраивается только это
    /// начертание (из коллекции .ttc вырезается своё), один раз на документ.
    /// null — файл стал недоступен или PDFium его не принял.
    /// </summary>
    public unsafe FpdfFontT? ForFace(FontFaceRef face)
    {
        if (_byFace.TryGetValue(face, out var cached))
            return cached;

        FpdfFontT? font = null;
        try
        {
            var bytes = provider.Load(face).ExtractFace();
            fixed (byte* data = bytes)
            {
                var loaded = fpdf_edit.FPDFTextLoadFont(
                    document, data, (uint)bytes.Length, FpdfFontTrueType, 1);
                if (loaded != null && loaded.__Instance != IntPtr.Zero)
                    font = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            // Шрифт пропал или испорчен после сканирования — вызывающий
            // сообщит об этом, а не уронит сохранение молча.
        }

        _byFace[face] = font;
        return font;
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
