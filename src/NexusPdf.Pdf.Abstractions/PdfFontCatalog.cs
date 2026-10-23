using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Шрифты для надписей, штампов и правки строк — прежний API поверх
/// каталога всех установленных шрифтов (<see cref="FontCatalog"/>).
///
/// Сюда попадают только шрифты, которые встраиваются в PDF и умеют
/// кириллицу: обещание прежнего закрытого списка сохраняется, а сам список
/// больше не ограничен восемью гарнитурами Windows.
/// </summary>
public static class PdfFontCatalog
{
    /// <summary>Гарнитура по умолчанию — ею пишется текст, если выбора не сделали.</summary>
    public const string DefaultFamily = FontCatalog.DefaultFamily;

    /// <summary>Каталог шрифтов системы.</summary>
    public static string FontsDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>Гарнитуры, которыми можно писать: встраиваются, с кириллицей, контуры TrueType.</summary>
    public static IReadOnlyList<string> AvailableFamilies() =>
        FontCatalog.System.Families
            .Where(f => f.Faces.Any(Usable))
            .Select(f => f.Name)
            .ToList();

    /// <summary>
    /// Полный путь к файлу нужного начертания или null, если гарнитуры нет
    /// (или она лежит внутри коллекции .ttc — такие пишутся новым путём через
    /// <see cref="FontFaceRef"/>). Недостающее начертание — ближайшее.
    /// </summary>
    public static string? ResolvePath(string family, bool bold, bool italic)
    {
        var face = FontCatalog.System.Resolve(family, bold, italic);
        return face != null && Usable(face) && face.Ref.FaceIndex == 0 &&
               !face.Ref.Path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
            ? face.Ref.Path
            : null;
    }

    /// <summary>
    /// Путь к шрифту по умолчанию: сначала заданная гарнитура, затем любая
    /// подходящая. Возвращает null, только если подходящих шрифтов нет вовсе.
    /// </summary>
    public static string? ResolveDefaultPath(bool bold = false, bool italic = false)
    {
        var preferred = ResolvePath(DefaultFamily, bold, italic);
        if (preferred != null)
            return preferred;
        foreach (var family in FontCatalog.System.FallbackFamilies)
        {
            var path = ResolvePath(family, bold, italic);
            if (path != null)
                return path;
        }
        return null;
    }

    private static bool Usable(FontFaceInfo face) => face.Embeddable && face.Supported && face.HasCyrillic;
}
