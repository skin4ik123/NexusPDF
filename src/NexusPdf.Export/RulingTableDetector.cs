using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <summary>
/// Таблица по НАРИСОВАННЫМ линиям.
///
/// Это самый честный из способов: границы ячеек не угадываются по пробелам, а
/// берутся из самого документа. Поэтому он же единственный, который правильно
/// разбирает объединённые ячейки: если между двумя клетками сетки границы нет —
/// это одна ячейка, а не две.
/// </summary>
public static class RulingTableDetector
{
    /// <summary>Насколько близко должны стоять линии, чтобы считаться одной.</summary>
    private const double SnapTolerance = 2.5;

    /// <summary>Разрыв между отрезками одной линии, который ещё можно сшить.</summary>
    private const double JoinTolerance = 3.0;

    /// <summary>
    /// Какую долю стороны ячейки должна закрывать линия, чтобы считаться её
    /// границей. Не 100%: в реальных документах отрезки чуть не дотягиваются.
    /// </summary>
    private const double BorderCoverage = 0.7;

    public static IReadOnlyList<ExtractedTable> Detect(
        IReadOnlyList<PdfRulingLine> rulings, IReadOnlyList<PdfTextWord> words)
    {
        var horizontal = Merge(rulings.Where(r => r.IsHorizontal).ToList());
        var vertical = Merge(rulings.Where(r => !r.IsHorizontal).ToList());
        if (horizontal.Count < 2 || vertical.Count < 2) return Array.Empty<ExtractedTable>();

        var tables = new List<ExtractedTable>();
        foreach (var (rows, columns) in Components(horizontal, vertical))
        {
            var table = BuildTable(rows, columns, words);
            if (table != null) tables.Add(table);
        }

        return tables
            .OrderByDescending(t => t.Bounds.Top)
            .ThenBy(t => t.Bounds.Left)
            .ToList();
    }

    /// <summary>
    /// Сшивание отрезков в целые линии: одну границу таблицы генераторы часто
    /// рисуют десятком кусочков — по кусочку на ячейку.
    /// </summary>
    internal static IReadOnlyList<PdfRulingLine> Merge(IReadOnlyList<PdfRulingLine> lines)
    {
        if (lines.Count == 0) return Array.Empty<PdfRulingLine>();

        var result = new List<PdfRulingLine>();
        foreach (var group in Cluster(lines))
        {
            var segments = group.OrderBy(l => l.Start).ToList();
            var position = segments.Average(l => l.Position);
            var thickness = segments.Max(l => l.ThicknessPt);
            var start = segments[0].Start;
            var end = segments[0].End;
            foreach (var segment in segments.Skip(1))
            {
                if (segment.Start <= end + JoinTolerance)
                {
                    end = Math.Max(end, segment.End);
                    continue;
                }
                result.Add(new PdfRulingLine(segments[0].IsHorizontal, position, start, end, thickness));
                start = segment.Start;
                end = segment.End;
            }
            result.Add(new PdfRulingLine(segments[0].IsHorizontal, position, start, end, thickness));
        }
        return result;
    }

    private static IEnumerable<List<PdfRulingLine>> Cluster(IReadOnlyList<PdfRulingLine> lines)
    {
        var sorted = lines.OrderBy(l => l.Position).ToList();
        var current = new List<PdfRulingLine> { sorted[0] };
        var anchor = sorted[0].Position;
        foreach (var line in sorted.Skip(1))
        {
            if (line.Position - anchor <= SnapTolerance)
            {
                current.Add(line);
                continue;
            }
            yield return current;
            current = new List<PdfRulingLine> { line };
            anchor = line.Position;
        }
        yield return current;
    }

    /// <summary>
    /// Связные наборы пересекающихся линий: на странице может быть несколько
    /// независимых таблиц, и мешать их в одну сетку нельзя.
    /// </summary>
    private static IEnumerable<(List<PdfRulingLine> Rows, List<PdfRulingLine> Columns)> Components(
        IReadOnlyList<PdfRulingLine> horizontal, IReadOnlyList<PdfRulingLine> vertical)
    {
        var parent = new int[horizontal.Count + vertical.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[rb] = ra; }

        for (var h = 0; h < horizontal.Count; h++)
            for (var v = 0; v < vertical.Count; v++)
                if (Intersect(horizontal[h], vertical[v]))
                    Union(h, horizontal.Count + v);

        var groups = new Dictionary<int, (List<PdfRulingLine> Rows, List<PdfRulingLine> Columns)>();
        for (var i = 0; i < parent.Length; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var group))
                groups[root] = group = (new List<PdfRulingLine>(), new List<PdfRulingLine>());
            if (i < horizontal.Count) group.Rows.Add(horizontal[i]);
            else group.Columns.Add(vertical[i - horizontal.Count]);
        }

        return groups.Values.Where(g => g.Rows.Count >= 2 && g.Columns.Count >= 2);
    }

    private static bool Intersect(PdfRulingLine h, PdfRulingLine v) =>
        v.Position >= h.Start - SnapTolerance && v.Position <= h.End + SnapTolerance &&
        h.Position >= v.Start - SnapTolerance && h.Position <= v.End + SnapTolerance;

    private static ExtractedTable? BuildTable(
        List<PdfRulingLine> rows, List<PdfRulingLine> columns, IReadOnlyList<PdfTextWord> words)
    {
        var ys = Collapse(rows.Select(r => r.Position).OrderByDescending(y => y));
        var xs = Collapse(columns.Select(c => c.Position).OrderBy(x => x));
        if (ys.Count < 2 || xs.Count < 2) return null;

        var rowCount = ys.Count - 1;
        var columnCount = xs.Count - 1;
        var bounds = new PdfTextRect(xs[0], ys[0], xs[^1], ys[^1]);

        // Слова таблицы отбираются один раз: перебирать все слова страницы для
        // каждой ячейки — это квадрат от размера страницы.
        var inside = words
            .Where(w => Inside(w, bounds))
            .ToList();

        var taken = new bool[rowCount, columnCount];
        var cells = new List<TableCell>();

        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < columnCount; c++)
            {
                if (taken[r, c]) continue;

                // Ячейка растёт вправо, пока справа нет границы, и вниз, пока
                // граница отсутствует по всей её ширине.
                var lastColumn = c;
                while (lastColumn + 1 < columnCount &&
                       !HasVertical(columns, xs[lastColumn + 1], ys[r + 1], ys[r]))
                    lastColumn++;

                var lastRow = r;
                while (lastRow + 1 < rowCount &&
                       !HasHorizontal(rows, ys[lastRow + 1], xs[c], xs[lastColumn + 1]))
                    lastRow++;

                for (var rr = r; rr <= lastRow; rr++)
                    for (var cc = c; cc <= lastColumn; cc++)
                        taken[rr, cc] = true;

                var box = new PdfTextRect(xs[c], ys[r], xs[lastColumn + 1], ys[lastRow + 1]);
                var content = inside.Where(w => Inside(w, box)).ToList();
                cells.Add(new TableCell(
                    r, c, lastRow - r + 1, lastColumn - c + 1,
                    CellText.Compose(content), box,
                    content.Count > 0 && content.All(w => w.IsBold)));
            }
        }

        // Пустая сетка — это рамка вокруг картинки или бланк без данных.
        if (cells.All(c => c.Text.Length == 0)) return null;

        return new ExtractedTable(
            rowCount, columnCount, cells, xs, TableSource.Ruling, bounds, Confidence: 1.0);
    }

    /// <summary>
    /// Линии сетки по одной на позицию.
    ///
    /// Одну границу таблицы генераторы сплошь и рядом рисуют не целой линией, а
    /// отдельным отрезком на каждую строку. Сшивание отрезков оставляет их
    /// разными линиями с ОДИНАКОВОЙ позицией, и сетка получает десяток колонок
    /// нулевой ширины вместо одной границы: у морского чек-листа RLM из четырёх
    /// колонок выходило десять, а графы для отметок схлопывались в нитку.
    ///
    /// Порядок значений сохраняется — вызывающий уже отсортировал их так, как
    /// ему нужно (строки сверху вниз, колонки слева направо).
    /// </summary>
    private static List<double> Collapse(IEnumerable<double> positions)
    {
        var result = new List<double>();
        foreach (var position in positions)
        {
            if (result.Count > 0 && Math.Abs(position - result[^1]) <= SnapTolerance) continue;
            result.Add(position);
        }
        return result;
    }

    private static bool HasVertical(List<PdfRulingLine> columns, double x, double bottom, double top)
    {
        var need = (top - bottom) * BorderCoverage;
        foreach (var line in columns)
        {
            if (Math.Abs(line.Position - x) > SnapTolerance) continue;
            var covered = Math.Min(top, line.End) - Math.Max(bottom, line.Start);
            if (covered >= need) return true;
        }
        return false;
    }

    private static bool HasHorizontal(List<PdfRulingLine> rows, double y, double left, double right)
    {
        var need = (right - left) * BorderCoverage;
        foreach (var line in rows)
        {
            if (Math.Abs(line.Position - y) > SnapTolerance) continue;
            var covered = Math.Min(right, line.End) - Math.Max(left, line.Start);
            if (covered >= need) return true;
        }
        return false;
    }

    /// <summary>Слово принадлежит ячейке по своей середине: так его не делят пополам границы.</summary>
    private static bool Inside(PdfTextWord word, PdfTextRect box)
    {
        var x = (word.RectPt.Left + word.RectPt.Right) / 2.0;
        var y = word.CenterY;
        return x >= box.Left - SnapTolerance && x <= box.Right + SnapTolerance &&
               y <= box.Top + SnapTolerance && y >= box.Bottom - SnapTolerance;
    }
}

/// <summary>Сборка текста ячейки: слова строками, строки сверху вниз.</summary>
internal static class CellText
{
    public static string Compose(IReadOnlyList<PdfTextWord> words)
    {
        if (words.Count == 0) return string.Empty;

        // Повёрнутая подпись читается вдоль СВОЕЙ оси, и порядок слов в ней
        // обратен привычному: снизу вверх при повороте против часовой, сверху
        // вниз — по часовой. По обычному правилу «сверху вниз, слева направо»
        // «DETAIL OF JOB» превратилось бы в «OF DETAIL JOB».
        var rotated = words.Where(w => w.IsRotated).ToList();
        if (rotated.Count * 2 > words.Count)
        {
            var counterClockwise = rotated.Count(w => w.RotationQuarters == 1) >= rotated.Count / 2.0;
            var ordered = counterClockwise
                ? words.OrderBy(w => w.RectPt.Left).ThenBy(w => w.RectPt.Bottom)
                : words.OrderByDescending(w => w.RectPt.Right).ThenByDescending(w => w.RectPt.Top);
            return string.Join(" ", ordered.Select(w => w.Text)).Trim();
        }

        var lines = TextLineBuilder.Build(words);
        return string.Join("\n", lines.Select(l => l.Text)).Trim();
    }
}
