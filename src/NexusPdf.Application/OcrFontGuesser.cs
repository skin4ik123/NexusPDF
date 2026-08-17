using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Подбирает гарнитуру под то, чем строка была написана в оригинале.
///
/// Раньше весь распознанный текст писался одним системным шрифтом, и документ
/// с засечками после замены выглядел чужим: набор менялся целиком, а не только
/// буквы. Угадать точную гарнитуру по растру нельзя, но два свойства, которые
/// глаз замечает первыми, измеримы:
///
/// * НАСЫЩЕННОСТЬ — по толщине штриха относительно высоты строки;
/// * ЗАСЕЧКИ — по тому, насколько ниже базовой линии штрихи расширяются:
///   у шрифта с засечками низ буквы заканчивается горизонтальной лапкой,
///   и в нижней полосе строки чернил заметно больше, чем в середине.
///
/// Это оценка, а не распознавание шрифта: результат — «с засечками или без,
/// светлый или полужирный». Ошибка здесь стоит дёшево, а попадание сохраняет
/// вид документа.
/// </summary>
public static class OcrFontGuesser
{
    /// <summary>Гарнитуры замены: обычная и с засечками.</summary>
    private const string SansFamily = "Segoe UI";
    private const string SerifFamily = "Times New Roman";

    /// <summary>Толще этой доли высоты строки штрих считается полужирным.</summary>
    private const double BoldStrokeRatio = 0.115;

    /// <summary>Во столько раз низ строки должен быть «чернее» середины, чтобы счесть засечки.</summary>
    private const double SerifFootRatio = 1.4;

    public sealed record Guess(string Family, bool Bold);

    /// <summary>
    /// Оценивает гарнитуру строки по растру страницы. Возвращает шрифт без
    /// засечек, если измерить не удалось: он безопаснее — им набрано
    /// большинство современных документов.
    /// </summary>
    /// <param name="bandYPt">
    /// Верх фактической полосы букв. Мерить по рамке распознавания нельзя:
    /// она заметно выше самих букв, и полужирное начертание при делении на
    /// её высоту не определялось никогда. null — мерим по рамке.
    /// </param>
    /// <param name="bandHeightPt">Высота той же полосы.</param>
    public static Guess Of(
        RenderedPageImage image, double pxPerPtX, double pxPerPtY, OcrTextLine line,
        double? bandYPt = null, double? bandHeightPt = null)
    {
        var top = bandYPt ?? line.YPt;
        var tall = bandHeightPt ?? line.HeightPt;
        var x0 = Clamp((int)Math.Floor(line.XPt * pxPerPtX), 0, image.PixelWidth);
        var x1 = Clamp((int)Math.Ceiling((line.XPt + line.WidthPt) * pxPerPtX), 0, image.PixelWidth);
        var y0 = Clamp((int)Math.Floor(top * pxPerPtY), 0, image.PixelHeight);
        var y1 = Clamp((int)Math.Ceiling((top + tall) * pxPerPtY), 0, image.PixelHeight);
        var height = y1 - y0;
        if (x1 - x0 < 4 || height < 4)
            return new Guess(SansFamily, false);

        // Порог между чернилами и бумагой — посередине между их яркостями.
        var inkLuma = Luma(line.InkArgb);
        var paperLuma = Luma(line.BackgroundArgb);
        if (Math.Abs(paperLuma - inkLuma) < 12)
            return new Guess(SansFamily, false); // контраста нет, мерить нечего
        var threshold = (inkLuma + paperLuma) / 2;
        var inkIsDarker = inkLuma < paperLuma;

        bool IsInk(int x, int y)
        {
            var offset = y * image.Stride + x * 4;
            var luma = Luma(image.Bgra[offset + 2], image.Bgra[offset + 1], image.Bgra[offset]);
            return inkIsDarker ? luma < threshold : luma > threshold;
        }

        // Средняя длина горизонтального штриха в полосе строк — она же оценка
        // толщины пера: у полужирного шрифта штрих шире при той же высоте.
        double RunLength(int from, int to)
        {
            long runs = 0, ink = 0;
            for (var y = from; y < to; y++)
            {
                if (y < 0 || y >= image.PixelHeight) continue;
                var inRun = false;
                for (var x = x0; x < x1; x++)
                {
                    if (IsInk(x, y))
                    {
                        ink++;
                        if (!inRun) { runs++; inRun = true; }
                    }
                    else inRun = false;
                }
            }
            return runs == 0 ? 0 : (double)ink / runs;
        }

        // Середина строки — вертикальные штрихи букв; низ — там, где у шрифта
        // с засечками сидят лапки.
        var middle = RunLength(y0 + (int)(height * 0.35), y0 + (int)(height * 0.65));
        var foot = RunLength(y1 - Math.Max(2, (int)(height * 0.18)), y1);
        if (middle <= 0)
            return new Guess(SansFamily, false);

        var bold = middle / height > BoldStrokeRatio;
        var serif = foot / middle > SerifFootRatio;
        return new Guess(serif ? SerifFamily : SansFamily, bold);
    }

    private static double Luma(uint argb) =>
        Luma((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static double Luma(byte r, byte g, byte b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
