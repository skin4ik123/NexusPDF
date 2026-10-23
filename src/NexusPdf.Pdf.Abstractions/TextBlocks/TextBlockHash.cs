using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Отпечаток объектов блока: место куска в тексте (R), шрифт, кегль, цвет,
/// наклон и точка привязки каждого объекта. Считается одинаково при записи и
/// при чтении, по значениям самих объектов страницы, — поэтому чужая правка
/// (сдвиг, другой шрифт, цвет, поворот) даёт другой отпечаток, и блок не
/// открывается как блок поверх неё.
///
/// Текста в отпечатке нет намеренно. Текст и так собирается из самих
/// объектов (чужая правка текста не затирается), а отпечаток с текстом
/// позволил бы подобрать короткий текст, вымаранный другой программой, если
/// объект с описанием блока уцелел.
/// </summary>
public static class TextBlockHash
{
    /// <param name="Ref">Ссылка R объекта ("абзац:начало" или "d:номер").</param>
    /// <param name="FontName">Имя шрифта объекта; у линий — пусто.</param>
    public sealed record Piece(
        string Ref, string FontName, double FontSizePt, uint ColorArgb,
        double A, double B, double C, double D, double XPt, double YPt);

    /// <summary>
    /// SHA-256 (hex, нижний регистр). Точка привязки и кегль округляются до
    /// 0,01, наклон — до 0,001: при записи значения стоят ровно на этой сетке,
    /// и шум float после записи в файл и чтения (~0,00001) не доходит до
    /// границы округления. Порядок объектов не важен: после пересохранения
    /// другой программой он может поменяться, а смысл — нет.
    /// </summary>
    public static string Compute(IEnumerable<Piece> pieces)
    {
        var lines = pieces
            .Select(p => string.Join('\u001F',
                p.Ref,
                p.FontName,
                Round(p.FontSizePt, 2),
                p.ColorArgb.ToString("x8", CultureInfo.InvariantCulture),
                Round(p.A, 3), Round(p.B, 3), Round(p.C, 3), Round(p.D, 3),
                Round(p.XPt, 2),
                Round(p.YPt, 2)))
            .Order(StringComparer.Ordinal);
        var canonical = string.Join('\u001E', lines);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // + 0.0 убирает «минус ноль», который иначе печатался бы со знаком.
    private static string Round(double value, int decimals) =>
        (Math.Round(value, decimals, MidpointRounding.AwayFromZero) + 0.0)
            .ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
