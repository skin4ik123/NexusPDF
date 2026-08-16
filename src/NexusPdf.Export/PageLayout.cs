using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <summary>Строка текста, собранная из слов по их вертикальному положению.</summary>
public sealed class TextLine
{
    public TextLine(IReadOnlyList<PdfTextWord> words)
    {
        if (words.Count == 0) throw new ArgumentException("Строка без слов.", nameof(words));
        Words = words;
        Left = words.Min(w => w.RectPt.Left);
        Right = words.Max(w => w.RectPt.Right);
        Top = words.Max(w => w.RectPt.Top);
        Bottom = words.Min(w => w.RectPt.Bottom);
    }

    /// <summary>Слова слева направо.</summary>
    public IReadOnlyList<PdfTextWord> Words { get; }

    public double Left { get; }
    public double Right { get; }
    public double Top { get; }
    public double Bottom { get; }
    public double CenterY => (Top + Bottom) / 2.0;

    /// <summary>Кегль строки — медиана по словам, чтобы одна крупная буква не сбивала.</summary>
    public double FontSize => Median(Words.Where(w => w.FontSizePt > 0).Select(w => w.FontSizePt).ToList(), Top - Bottom);

    /// <summary>Полужирная ли строка целиком — по этому признаку строится стиль заголовка.</summary>
    public bool IsBold => Words.Count > 0 && Words.All(w => w.IsBold);

    public string Text => string.Join(" ", Words.Select(w => w.Text));

    internal static double Median(IReadOnlyList<double> values, double fallback)
    {
        if (values.Count == 0) return fallback;
        var sorted = values.OrderBy(v => v).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}

/// <summary>Чем распознана таблица — фактом или предположением.</summary>
public enum TableSource
{
    /// <summary>По НАРИСОВАННЫМ линиям: границы ячеек взяты из документа, а не угаданы.</summary>
    Ruling,

    /// <summary>По вертикальным просветам между колонками: линий в документе нет, структура восстановлена.</summary>
    Whitespace,
}

/// <summary>Ячейка таблицы. Объединённые ячейки описываются размахом строк и колонок.</summary>
public sealed record TableCell(
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    string Text,
    PdfTextRect Bounds,
    bool IsBold);

/// <summary>Распознанная таблица страницы.</summary>
public sealed record ExtractedTable(
    int RowCount,
    int ColumnCount,
    IReadOnlyList<TableCell> Cells,
    IReadOnlyList<double> ColumnEdges,
    TableSource Source,
    PdfTextRect Bounds,
    double Confidence)
{
    /// <summary>Ячейка по позиции сетки или null, если её накрыла объединённая соседка.</summary>
    public TableCell? At(int row, int column) =>
        Cells.FirstOrDefault(c => c.Row == row && c.Column == column);
}

/// <summary>Разобранная страница: таблицы и строки текста вне таблиц.</summary>
/// <param name="AllWords">
/// Все слова страницы, включая попавшие в таблицы. Нужны экспорту в Word:
/// текст ячейки хранится строкой, а начертание — только у слов.
/// </param>
public sealed record PageLayout(
    int PageIndex,
    double WidthPt,
    double HeightPt,
    IReadOnlyList<ExtractedTable> Tables,
    IReadOnlyList<TextLine> Lines,
    IReadOnlyList<PdfTextWord>? AllWords = null)
{
    public IReadOnlyList<PdfTextWord> Words => AllWords ?? Array.Empty<PdfTextWord>();

    /// <summary>Есть ли на странице хоть что-нибудь текстовое.</summary>
    public bool IsEmpty => Tables.Count == 0 && Lines.Count == 0;
}
