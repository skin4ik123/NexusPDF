using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <summary>
/// Таблица без линий — по вертикальным просветам.
///
/// Границ в документе нет, поэтому это уже не факт, а восстановление: колонка
/// найдена там, где просвет между словами держится во ВСЕХ строках блока. Такой
/// признак не спутает колонку с обычным пробелом внутри предложения — пробелы в
/// разных строках стоят в разных местах и сквозного просвета не образуют.
///
/// Чтобы не выдавать за таблицу обычный абзац, результат проверяется на
/// заполненность и возвращается вместе с честной оценкой уверенности.
/// </summary>
public static class WhitespaceTableDetector
{
    /// <summary>Меньше трёх строк — это не таблица, а пара подписей рядом.</summary>
    private const int MinRows = 3;

    /// <summary>Доля непустых ячеек, ниже которой сетка неправдоподобна.</summary>
    private const double MinFill = 0.5;

    /// <summary>Минимальный просвет между колонками в долях кегля.</summary>
    private const double MinGapEm = 0.9;

    public static IReadOnlyList<ExtractedTable> Detect(IReadOnlyList<TextLine> lines)
    {
        var tables = new List<ExtractedTable>();
        foreach (var block in TextLineBuilder.SplitIntoBlocks(lines))
        {
            var table = FromBlock(block);
            if (table != null) tables.Add(table);
        }
        return tables;
    }

    /// <summary>Границы колонок блока или пустой список, если сквозных просветов нет.</summary>
    internal static IReadOnlyList<double> FindColumnEdges(IReadOnlyList<TextLine> block)
    {
        if (block.Count == 0) return Array.Empty<double>();

        var fontSize = TextLine.Median(block.Select(l => l.FontSize).ToList(), 10);
        var minGap = Math.Max(4.0, fontSize * MinGapEm);

        var spans = block
            .SelectMany(l => l.Words)
            .Select(w => (Left: w.RectPt.Left, Right: w.RectPt.Right))
            .OrderBy(s => s.Left)
            .ToList();
        if (spans.Count == 0) return Array.Empty<double>();

        var left = spans[0].Left;
        var right = spans[0].Right;
        var edges = new List<double> { left };
        foreach (var span in spans.Skip(1))
        {
            if (span.Left - right > minGap)
            {
                // Граница ставится по середине просвета: так слово с любой
                // стороны попадает в свою колонку даже при неровных отступах.
                edges.Add((right + span.Left) / 2.0);
                right = span.Right;
                continue;
            }
            right = Math.Max(right, span.Right);
        }
        edges.Add(Math.Max(right, spans.Max(s => s.Right)));
        return edges;
    }

    private static ExtractedTable? FromBlock(IReadOnlyList<TextLine> block)
    {
        if (block.Count < MinRows) return null;

        var edges = FindColumnEdges(block);
        var columnCount = edges.Count - 1;
        if (columnCount < 2) return null;

        var cells = new List<TableCell>();
        var filled = 0;
        var rowsWithPair = 0;

        for (var r = 0; r < block.Count; r++)
        {
            var line = block[r];
            var inRow = 0;
            for (var c = 0; c < columnCount; c++)
            {
                var from = edges[c];
                var to = edges[c + 1];
                var words = line.Words
                    .Where(w =>
                    {
                        var center = (w.RectPt.Left + w.RectPt.Right) / 2.0;
                        return center >= from && (center < to || c == columnCount - 1);
                    })
                    .ToList();
                var text = string.Join(" ", words.Select(w => w.Text));
                if (text.Length > 0) { filled++; inRow++; }
                cells.Add(new TableCell(
                    r, c, 1, 1, text,
                    new PdfTextRect(from, line.Top, to, line.Bottom),
                    words.Count > 0 && words.All(w => w.IsBold)));
            }
            if (inRow >= 2) rowsWithPair++;
        }

        var fill = (double)filled / (block.Count * columnCount);
        if (fill < MinFill) return null;
        // Настоящая таблица заполнена в несколько колонок в большинстве строк.
        if (rowsWithPair < block.Count * 0.6) return null;

        var bounds = new PdfTextRect(
            block.Min(l => l.Left), block.Max(l => l.Top),
            block.Max(l => l.Right), block.Min(l => l.Bottom));

        // Уверенность растёт с числом строк и заполненностью, но никогда не
        // дотягивает до линий: у догадки не бывает полной уверенности.
        var confidence = Math.Min(0.9, 0.35 + fill * 0.4 + Math.Min(block.Count, 12) / 12.0 * 0.15);

        return new ExtractedTable(
            block.Count, columnCount, cells, edges, TableSource.Whitespace, bounds, confidence);
    }
}
