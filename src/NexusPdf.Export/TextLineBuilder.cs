using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <summary>
/// Слова → строки.
///
/// Строк в PDF нет: есть слова с координатами. Слова считаются одной строкой,
/// если их вертикальные размахи перекрываются — а не если совпадают базовые
/// линии: у нижних индексов, крупных буквиц и разных шрифтов в одной строке
/// базовая линия своя.
/// </summary>
public static class TextLineBuilder
{
    /// <summary>Доля перекрытия по высоте, при которой слова — одна строка.</summary>
    private const double SameLineOverlap = 0.4;

    public static IReadOnlyList<TextLine> Build(IEnumerable<PdfTextWord> words)
    {
        var ordered = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Height > 0)
            .OrderByDescending(w => w.CenterY)
            .ThenBy(w => w.RectPt.Left)
            .ToList();
        if (ordered.Count == 0) return Array.Empty<TextLine>();

        var lines = new List<List<PdfTextWord>>();
        var currentTop = double.NaN;
        var currentBottom = double.NaN;

        foreach (var word in ordered)
        {
            var joins = lines.Count > 0 && Overlaps(currentTop, currentBottom, word);
            if (!joins)
            {
                lines.Add(new List<PdfTextWord>());
                currentTop = word.RectPt.Top;
                currentBottom = word.RectPt.Bottom;
            }
            else
            {
                // Размах строки растёт вместе со словами: иначе высокая первая
                // буква сделала бы строку слишком «жадной» или слишком узкой.
                currentTop = Math.Max(currentTop, word.RectPt.Top);
                currentBottom = Math.Min(currentBottom, word.RectPt.Bottom);
            }
            lines[^1].Add(word);
        }

        return lines
            .Select(l => new TextLine(l.OrderBy(w => w.RectPt.Left).ToList()))
            .ToList();
    }

    private static bool Overlaps(double top, double bottom, PdfTextWord word)
    {
        var overlap = Math.Min(top, word.RectPt.Top) - Math.Max(bottom, word.RectPt.Bottom);
        if (overlap <= 0) return false;
        var smallest = Math.Min(top - bottom, word.Height);
        return smallest <= 0 || overlap >= smallest * SameLineOverlap;
    }

    /// <summary>Во сколько кеглей должен быть разрыв, чтобы это была уже другая колонка.</summary>
    private const double ColumnGapEm = 3.0;

    /// <summary>И не меньше этого в пунктах — чтобы мелкий шрифт не рвался зря.</summary>
    private const double MinColumnGapPt = 18.0;

    /// <summary>
    /// Разрезание строк по колонкам.
    ///
    /// Строка собирается по вертикальному перекрытию, и в неё попадает всё, что
    /// стоит на этой высоте — включая соседнюю колонку. В шапке бланка заголовок
    /// «THE REPUBLIC OF LIBERIA» и адрес справа лежат на одной высоте, и в Word
    /// уезжала склейка «THE REPUBLIC OF LIBERIA Dulles, Suite 200 Virginia
    /// 20166, USA», которую невозможно читать.
    ///
    /// Признак — разрыв в несколько кеглей: обычный пробел это четверть кегля,
    /// а между колонками там были 25 и 62 пункта при кегле 7,6. Нижняя граница
    /// в пунктах нужна, чтобы у мелкого шрифта не рвалось каждое второе слово.
    ///
    /// Вызывать это можно только ПОСЛЕ поиска таблиц по просветам: тому нужны
    /// целые строки, по ним он и находит границы колонок.
    /// </summary>
    public static IReadOnlyList<TextLine> SplitColumns(IReadOnlyList<TextLine> lines)
    {
        var result = new List<TextLine>();
        foreach (var line in lines)
        {
            if (line.Words.Count < 2) { result.Add(line); continue; }

            var threshold = Math.Max(line.FontSize * ColumnGapEm, MinColumnGapPt);
            var segment = new List<PdfTextWord> { line.Words[0] };
            var right = line.Words[0].RectPt.Right;

            foreach (var word in line.Words.Skip(1))
            {
                if (word.RectPt.Left - right > threshold)
                {
                    result.Add(new TextLine(segment));
                    segment = new List<PdfTextWord>();
                }
                segment.Add(word);
                right = Math.Max(right, word.RectPt.Right);
            }
            result.Add(new TextLine(segment));
        }
        return result;
    }

    /// <summary>
    /// Разбиение строк на блоки по вертикальным разрывам: между абзацами и
    /// таблицами расстояние заметно больше, чем между строками внутри них.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TextLine>> SplitIntoBlocks(IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0) return Array.Empty<IReadOnlyList<TextLine>>();
        if (lines.Count == 1) return new[] { lines };

        var gaps = new List<double>();
        for (var i = 1; i < lines.Count; i++)
            gaps.Add(Math.Max(0, lines[i - 1].Bottom - lines[i].Top));
        var typical = TextLine.Median(gaps, 0);

        // Порог держится и когда строки стоят вплотную (медиана 0): тогда
        // разрывом считается любой заметный по меркам кегля просвет.
        var threshold = Math.Max(typical * 1.8 + 1.0, lines[0].FontSize * 0.9);

        var blocks = new List<IReadOnlyList<TextLine>>();
        var current = new List<TextLine> { lines[0] };
        for (var i = 1; i < lines.Count; i++)
        {
            if (gaps[i - 1] > threshold)
            {
                blocks.Add(current);
                current = new List<TextLine>();
            }
            current.Add(lines[i]);
        }
        blocks.Add(current);
        return blocks;
    }
}
