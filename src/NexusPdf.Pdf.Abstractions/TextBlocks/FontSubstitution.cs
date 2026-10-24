using System.Text.RegularExpressions;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Каким установленным шрифтом писать текст, набранный в документе шрифтом,
/// которого в Windows нет. Имена шрифтов в PDF — это PostScript-имена с
/// приставкой подмножества и начертанием («ABCDEF+Helvetica-Bold»).
/// </summary>
public static partial class FontSubstitution
{
    public static string? FamilyFor(string pdfFontName, IFontProvider fonts)
    {
        if (string.IsNullOrWhiteSpace(pdfFontName)) return null;
        var name = SubsetPrefix().Replace(pdfFontName.Trim(), "");
        name = name.Split(',', '-')[0];
        foreach (var suffix in new[] { "PSMT", "PS", "MT" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        if (name.Length == 0) return null;

        // Стандартные шрифты PDF и их метрически совместимые замены Windows.
        var standard =
            name.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Arial", StringComparison.OrdinalIgnoreCase) ? "Arial"
            : name.StartsWith("Times", StringComparison.OrdinalIgnoreCase) ? "Times New Roman"
            : name.StartsWith("Courier", StringComparison.OrdinalIgnoreCase) ? "Courier New"
            : null;
        if (standard != null && fonts.Resolve(standard, false, false) is { } found)
            return found.Family;

        var compact = Compact(name);
        return fonts.FallbackFamilies.FirstOrDefault(family => Compact(family) == compact);
    }

    /// <summary>Имя шрифта документа для человека: без приставки подмножества и начертания («ABCDEF+Calibri-Bold» → «Calibri»).</summary>
    public static string DisplayName(string pdfFontName)
    {
        if (string.IsNullOrWhiteSpace(pdfFontName)) return "";
        return SubsetPrefix().Replace(pdfFontName.Trim(), "").Split(',', '-')[0].Trim();
    }

    private static string Compact(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    [GeneratedRegex("^[A-Z]{6}\\+")]
    private static partial Regex SubsetPrefix();
}
