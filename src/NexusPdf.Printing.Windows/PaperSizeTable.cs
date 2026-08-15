using System.Printing;
using NexusPdf.Printing;

namespace NexusPdf.Printing.Windows;

/// <summary>
/// Физические размеры стандартных форматов в пунктах PDF.
/// Нужна там, где драйвер перечисляет формат по имени, но не сообщает размер.
/// Здесь только форматы с закреплённым стандартом размером: угадывать «примерно
/// такой» нельзя — на чертеже это ошибка в миллиметрах.
/// </summary>
public static class PaperSizeTable
{
    private const double MmToPt = 72.0 / 25.4;
    private const double InchToPt = 72.0;

    private static SizePt Mm(double w, double h) => new(w * MmToPt, h * MmToPt);
    private static SizePt Inch(double w, double h) => new(w * InchToPt, h * InchToPt);

    private static readonly Dictionary<PageMediaSizeName, SizePt> Sizes = new()
    {
        [PageMediaSizeName.ISOA0] = Mm(841, 1189),
        [PageMediaSizeName.ISOA1] = Mm(594, 841),
        [PageMediaSizeName.ISOA2] = Mm(420, 594),
        [PageMediaSizeName.ISOA3] = Mm(297, 420),
        [PageMediaSizeName.ISOA4] = Mm(210, 297),
        [PageMediaSizeName.ISOA5] = Mm(148, 210),
        [PageMediaSizeName.ISOA6] = Mm(105, 148),
        [PageMediaSizeName.ISOB4] = Mm(250, 353),
        [PageMediaSizeName.ISOB5Envelope] = Mm(176, 250),
        [PageMediaSizeName.JISB4] = Mm(257, 364),
        [PageMediaSizeName.JISB5] = Mm(182, 257),

        [PageMediaSizeName.NorthAmericaLetter] = Inch(8.5, 11),
        [PageMediaSizeName.NorthAmericaLegal] = Inch(8.5, 14),
        [PageMediaSizeName.NorthAmericaTabloid] = Inch(11, 17),
        [PageMediaSizeName.NorthAmericaExecutive] = Inch(7.25, 10.5),
        [PageMediaSizeName.NorthAmericaStatement] = Inch(5.5, 8.5),
        [PageMediaSizeName.NorthAmerica4x6] = Inch(4, 6),
        [PageMediaSizeName.NorthAmerica5x7] = Inch(5, 7),
        [PageMediaSizeName.NorthAmerica8x10] = Inch(8, 10),
        [PageMediaSizeName.NorthAmericaNumber10Envelope] = Inch(4.125, 9.5),
    };

    public static bool TryGet(PageMediaSizeName name, out SizePt size) => Sizes.TryGetValue(name, out size);

    /// <summary>Имя формата по физическому размеру — для показа размера страницы PDF.</summary>
    public static string? Describe(SizePt size, double tolerancePt = 3.0)
    {
        foreach (var (name, known) in Sizes)
        {
            if (Matches(size, known, tolerancePt) || Matches(size, known.Swapped, tolerancePt))
                return name.ToString();
        }
        return null;
    }

    private static bool Matches(SizePt a, SizePt b, double tolerance) =>
        Math.Abs(a.WidthPt - b.WidthPt) <= tolerance && Math.Abs(a.HeightPt - b.HeightPt) <= tolerance;
}
