using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Export;

/// <param name="DecimalIsComma">Считать ли запятую десятичным разделителем (русская запись).</param>
/// <param name="KeepLinks">Переносить ли ссылки PDF живыми ссылками книги.</param>
/// <param name="ParseValues">Превращать ли числа и даты в настоящие числа и даты Excel.</param>
public sealed record ExcelExportOptions(
    bool DecimalIsComma = true,
    bool KeepLinks = true,
    bool ParseValues = true);

/// <summary>Страница со всем, что нужно для экспорта: разбор, ссылки, номер.</summary>
public sealed record ExportPage(PageLayout Layout, IReadOnlyList<PdfPageLink> Links);

/// <summary>Что получилось — для честного отчёта пользователю.</summary>
/// <param name="ScannedPages">Страниц без текстового слоя — сканов.</param>
/// <param name="RecognizedPages">Из них распознано на месте.</param>
public sealed record ExcelExportSummary(
    int Sheets, int Tables, int RulingTables, int GuessedTables, int Cells, int Links, int Numbers,
    int ScannedPages = 0, int RecognizedPages = 0);

/// <summary>
/// Таблицы PDF → книга Excel.
///
/// Пишется настоящий Open XML (формат самого Excel), а не CSV с расширением
/// .xlsx: только так переживают объединённые ячейки, ссылки, форматы чисел и
/// даты. Числа записываются числами, иначе экспорт бессмысленен — по нему
/// нельзя ни посчитать сумму, ни построить график.
/// </summary>
public static class XlsxExporter
{
    /// <summary>Схемы ссылок, которые разрешено переносить в книгу.</summary>
    private static readonly string[] SafeSchemes = { "http", "https", "mailto", "ftp", "ftps" };

    /// <summary>Какая доля ссылки должна лежать в ячейке, чтобы ссылка была её.</summary>
    private const double MinLinkShare = 0.3;

    public static ExcelExportSummary Write(
        string path, IReadOnlyList<ExportPage> pages, ExcelExportOptions? options = null)
    {
        options ??= new ExcelExportOptions();
        if (pages.Count == 0) throw new ArgumentException("Нечего экспортировать.", nameof(pages));

        var names = SheetNames(pages);
        var styles = new SpreadsheetStyles();
        var grids = pages
            .Select(p => SheetGrid.Build(p, options, styles, names))
            .ToList();

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();

        var links = 0;
        for (var i = 0; i < grids.Count; i++)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            links += grids[i].WriteTo(worksheetPart);
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = (uint)(i + 1),
                Name = names[i],
            });
        }

        // Стили пишутся последними: до этого момента ещё не известно, какие
        // сочетания форматов встретились в документе.
        stylesPart.Stylesheet = styles.Build();
        stylesPart.Stylesheet.Save();
        workbookPart.Workbook.Save();

        return new ExcelExportSummary(
            grids.Count,
            grids.Sum(g => g.Tables),
            grids.Sum(g => g.RulingTables),
            grids.Sum(g => g.GuessedTables),
            grids.Sum(g => g.CellCount),
            links,
            grids.Sum(g => g.Numbers));
    }

    /// <summary>Имена листов: уникальные, не длиннее 31 знака и без запрещённых символов.</summary>
    public static IReadOnlyList<string> SheetNames(IReadOnlyList<ExportPage> pages)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(pages.Count);
        foreach (var page in pages)
        {
            var baseName = $"Стр. {page.Layout.PageIndex + 1}";
            var name = baseName;
            var suffix = 2;
            while (!used.Add(name))
                name = Trim($"{baseName} ({suffix++})");
            names.Add(name);
        }
        return names;

        static string Trim(string value)
        {
            var cleaned = new string(value.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
            return cleaned.Length <= 31 ? cleaned : cleaned[..31];
        }
    }

    public static bool IsSafeLink(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        SafeSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase);

    /// <summary>Номер колонки → имя: 0 → A, 26 → AA.</summary>
    public static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var i = index; i >= 0; i = i / 26 - 1)
            name = (char)('A' + i % 26) + name;
        return name;
    }

    public static string Reference(int row, int column) => ColumnName(column) + (row + 1);

    // ----- построение листа -----

    private sealed record CellDraft(
        string Text, ParsedValue Value, bool Bold, bool Border, bool Header,
        int RowSpan, int ColumnSpan, string? Uri, string? SheetTarget);

    private sealed class SheetGrid
    {
        private readonly Dictionary<(int Row, int Column), CellDraft> _cells = new();
        private readonly SpreadsheetStyles _styles;
        private int _rows;
        private int _columns;

        private SheetGrid(SpreadsheetStyles styles) => _styles = styles;

        public int Tables { get; private set; }
        public int RulingTables { get; private set; }
        public int GuessedTables { get; private set; }
        public int Numbers { get; private set; }
        public int CellCount => _cells.Count;

        public static SheetGrid Build(
            ExportPage page, ExcelExportOptions options, SpreadsheetStyles styles,
            IReadOnlyList<string> sheetNames)
        {
            var grid = new SheetGrid(styles);
            var row = 0;

            // Всё содержимое страницы идёт сверху вниз в том же порядке, в
            // каком стоит на бумаге: таблицы вперемешку с обычным текстом.
            var blocks = page.Layout.Tables
                .Select(t => (Top: t.Bounds.Top, Table: (ExtractedTable?)t, Line: (TextLine?)null))
                .Concat(page.Layout.Lines.Select(l => (Top: l.Top, Table: (ExtractedTable?)null, Line: (TextLine?)l)))
                .OrderByDescending(b => b.Top)
                .ToList();

            foreach (var block in blocks)
            {
                if (block.Table is { } table)
                {
                    grid.PlaceTable(table, row, page, options, sheetNames);
                    row += table.RowCount + 1;
                    grid.Tables++;
                    if (table.Source == TableSource.Ruling) grid.RulingTables++;
                    else grid.GuessedTables++;
                }
                else if (block.Line is { } line)
                {
                    grid.Place(row, 0, new CellDraft(
                        line.Text, ParsedValue.AsText(line.Text), line.IsBold, false, false, 1, 1,
                        grid.LinkFor(page, options, sheetNames, line.Left, line.Right, line.Top, line.Bottom, out var sheet),
                        sheet));
                    row++;
                }
            }

            return grid;
        }

        private void PlaceTable(
            ExtractedTable table, int startRow, ExportPage page,
            ExcelExportOptions options, IReadOnlyList<string> sheetNames)
        {
            // Первая строка таблицы считается заголовком, только если она
            // выделена жирным: красить произвольную строку было бы враньём.
            var headerRow = table.Cells
                .Where(c => c.Row == 0 && c.Text.Length > 0)
                .ToList();
            var hasHeader = headerRow.Count > 1 && headerRow.All(c => c.IsBold);

            foreach (var cell in table.Cells)
            {
                var uri = LinkFor(page, options, sheetNames,
                    cell.Bounds.Left, cell.Bounds.Right, cell.Bounds.Top, cell.Bounds.Bottom, out var sheet);
                var value = options.ParseValues
                    ? CellValueParser.Parse(cell.Text, options.DecimalIsComma)
                    : ParsedValue.AsText(cell.Text);
                if (value.Kind != CellKind.Text) Numbers++;

                Place(startRow + cell.Row, cell.Column, new CellDraft(
                    cell.Text, value,
                    cell.IsBold, true, hasHeader && cell.Row == 0,
                    cell.RowSpan, cell.ColumnSpan, uri, sheet));
            }
        }

        /// <summary>Ссылка, попадающая в этот прямоугольник страницы, если она есть.</summary>
        private string? LinkFor(
            ExportPage page, ExcelExportOptions options, IReadOnlyList<string> sheetNames,
            double left, double right, double top, double bottom, out string? sheetTarget)
        {
            sheetTarget = null;
            if (!options.KeepLinks) return null;

            // Ссылка достаётся той ячейке, в которой она ДЕЙСТВИТЕЛЬНО лежит.
            // Рамка ссылки почти всегда чуть-чуть заходит на соседнюю строку —
            // без порога и выбора лучшей ячейки одна ссылка размножилась бы по
            // всем задетым краешком клеткам.
            PdfPageLink? best = null;
            var bestShare = 0.0;
            foreach (var link in page.Links)
            {
                var overlapX = Math.Min(right, link.RectPt.Right) - Math.Max(left, link.RectPt.Left);
                var overlapY = Math.Min(top, link.RectPt.Top) - Math.Max(bottom, link.RectPt.Bottom);
                if (overlapX <= 0 || overlapY <= 0) continue;

                var area = Math.Max(1e-6,
                    (link.RectPt.Right - link.RectPt.Left) * (link.RectPt.Top - link.RectPt.Bottom));
                var share = overlapX * overlapY / area;
                if (share < MinLinkShare || share <= bestShare) continue;
                bestShare = share;
                best = link;
            }

            if (best == null) return null;
            if (IsSafeLink(best.Uri)) return best.Uri;

            // Ссылка внутрь документа осмысленна и в книге: она ведёт на лист
            // той самой страницы.
            if (best.Uri == null && best.TargetPageIndex >= 0 && best.TargetPageIndex < sheetNames.Count)
                sheetTarget = $"'{sheetNames[best.TargetPageIndex].Replace("'", "''")}'!A1";
            return null;
        }

        private void Place(int row, int column, CellDraft cell)
        {
            if (cell.Text.Length == 0 && cell.RowSpan == 1 && cell.ColumnSpan == 1) return;
            _cells[(row, column)] = cell;
            _rows = Math.Max(_rows, row + cell.RowSpan);
            _columns = Math.Max(_columns, column + cell.ColumnSpan);
        }

        /// <summary>Записывает лист и возвращает число перенесённых ссылок.</summary>
        public int WriteTo(WorksheetPart part)
        {
            var sheetData = new SheetData();
            var merges = new MergeCells();
            var hyperlinks = new Hyperlinks();
            var linkCount = 0;
            var widths = new Dictionary<int, double>();

            for (var r = 0; r < _rows; r++)
            {
                var rowElement = new Row { RowIndex = (uint)(r + 1) };
                var any = false;
                for (var c = 0; c < _columns; c++)
                {
                    if (!_cells.TryGetValue((r, c), out var draft)) continue;
                    any = true;
                    rowElement.Append(BuildCell(r, c, draft));

                    if (draft.ColumnSpan > 1 || draft.RowSpan > 1)
                    {
                        merges.Append(new MergeCell
                        {
                            Reference = $"{Reference(r, c)}:{Reference(r + draft.RowSpan - 1, c + draft.ColumnSpan - 1)}",
                        });
                    }

                    if (draft.Uri != null || draft.SheetTarget != null)
                    {
                        var hyperlink = new Hyperlink { Reference = Reference(r, c) };
                        if (draft.Uri != null)
                            hyperlink.Id = part.AddHyperlinkRelationship(new Uri(draft.Uri, UriKind.Absolute), true).Id;
                        else
                            hyperlink.Location = draft.SheetTarget;
                        hyperlinks.Append(hyperlink);
                        linkCount++;
                    }

                    if (draft.ColumnSpan == 1)
                    {
                        var estimate = Math.Min(60, draft.Text.Split('\n').Max(s => s.Length) * 1.05 + 2);
                        widths[c] = Math.Max(widths.GetValueOrDefault(c, 8), estimate);
                    }
                }
                if (any) sheetData.Append(rowElement);
            }

            var worksheet = new Worksheet();
            if (widths.Count > 0)
            {
                var columns = new Columns();
                foreach (var (index, width) in widths.OrderBy(w => w.Key))
                {
                    columns.Append(new Column
                    {
                        Min = (uint)(index + 1),
                        Max = (uint)(index + 1),
                        Width = width,
                        CustomWidth = true,
                    });
                }
                worksheet.Append(columns);
            }

            // Порядок элементов листа задан схемой Open XML: данные, объединения,
            // ссылки. Нарушишь — Excel объявит книгу повреждённой.
            worksheet.Append(sheetData);
            if (merges.ChildElements.Count > 0)
            {
                merges.Count = (uint)merges.ChildElements.Count;
                worksheet.Append(merges);
            }
            if (hyperlinks.ChildElements.Count > 0) worksheet.Append(hyperlinks);

            part.Worksheet = worksheet;
            part.Worksheet.Save();
            return linkCount;
        }

        private Cell BuildCell(int row, int column, CellDraft draft)
        {
            var isLink = draft.Uri != null || draft.SheetTarget != null;
            var format = draft.Value.Kind switch
            {
                CellKind.Date => SpreadsheetStyles.DateFormat,
                CellKind.Percent => SpreadsheetStyles.PercentFormat,
                CellKind.Currency => SpreadsheetStyles.CurrencyFormat(draft.Value.Currency),
                _ => string.Empty,
            };

            var cell = new Cell
            {
                CellReference = Reference(row, column),
                StyleIndex = _styles.Get(draft.Bold, draft.Border, draft.Header, isLink, format),
            };

            if (draft.Value.Kind == CellKind.Text || draft.Text.Length == 0)
            {
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(draft.Text) { Space = SpaceProcessingModeValues.Preserve });
            }
            else
            {
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue(draft.Value.Number.ToString("R", CultureInfo.InvariantCulture));
            }
            return cell;
        }
    }
}
