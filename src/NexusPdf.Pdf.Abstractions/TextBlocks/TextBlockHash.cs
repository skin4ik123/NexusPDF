using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Отпечаток объектов блока: текст, кегль, цвет и точка привязки каждого
/// объекта. Считается одинаково при записи и при чтении, по значениям самих
/// объектов страницы, — поэтому любая чужая правка (текст, сдвиг, цвет) даёт
/// другой отпечаток, и блок не открывается как блок поверх чужих изменений.
/// </summary>
public static class TextBlockHash
{
    /// <summary>
    /// SHA-256 (hex, нижний регистр). Значения округляются до 0,01: точки
    /// привязки при записи стоят ровно на этой сетке, поэтому шум float после
    /// записи в файл и чтения (~0,00001) никогда не доходит до границы
    /// округления. Порядок объектов не важен: после пересохранения другой
    /// программой он может поменяться, а смысл — нет. Прямоугольники
    /// подчёркивания передаются с пустым текстом и нулевым кеглем.
    /// </summary>
    public static string Compute(IEnumerable<(string Text, double FontSizePt, uint ColorArgb, double XPt, double YPt)> pieces)
    {
        var lines = pieces
            .Select(p => string.Join('\u001F',
                p.Text,
                Round(p.FontSizePt),
                p.ColorArgb.ToString("x8", CultureInfo.InvariantCulture),
                Round(p.XPt),
                Round(p.YPt)))
            .Order(StringComparer.Ordinal);
        var canonical = string.Join('\u001E', lines);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // + 0.0 убирает «минус ноль», который иначе печатался бы как "-0.00".
    private static string Round(double value) =>
        (Math.Round(value, 2, MidpointRounding.AwayFromZero) + 0.0).ToString("0.00", CultureInfo.InvariantCulture);
}
