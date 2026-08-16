using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NexusPdf.Export;

/// <summary>
/// Набор стилей книги, который собирается по мере надобности.
///
/// Excel хранит стили номерами в общей таблице, поэтому каждое сочетание
/// «жирный + рамка + формат числа» регистрируется один раз и дальше
/// переиспользуется: иначе на большом документе таблица стилей распухает и файл
/// перестаёт открываться.
/// </summary>
internal sealed class SpreadsheetStyles
{
    private const uint FirstCustomFormatId = 170;

    private readonly Dictionary<(bool Bold, bool Border, bool Header, bool Link, string Format), uint> _styles = new();
    private readonly Dictionary<string, uint> _numberFormats = new(StringComparer.Ordinal);
    private readonly List<(bool Bold, bool Border, bool Header, bool Link, string Format)> _order = new();

    /// <summary>Формат даты в привычном виде — Excel сам не угадает, что 45000 это дата.</summary>
    public const string DateFormat = "DD.MM.YYYY";

    public const string PercentFormat = "0.00%";

    public static string CurrencyFormat(string symbol) =>
        symbol.Length == 0 ? "#,##0.00" : "#,##0.00\\ \"" + symbol + "\"";

    public uint Get(bool bold = false, bool border = false, bool header = false,
        bool link = false, string format = "")
    {
        var key = (bold, border, header, link, format);
        if (_styles.TryGetValue(key, out var index)) return index;

        if (format.Length > 0 && !_numberFormats.ContainsKey(format))
            _numberFormats[format] = FirstCustomFormatId + (uint)_numberFormats.Count;

        index = (uint)_order.Count;
        _order.Add(key);
        _styles[key] = index;
        return index;
    }

    public Stylesheet Build()
    {
        var fonts = new Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Underline(), new Color { Rgb = "FF0563C1" },
                new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new Underline(), new Color { Rgb = "FF0563C1" },
                new FontSize { Val = 11 }, new FontName { Val = "Calibri" }))
        { Count = 4 };

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FFEDF2F7" },
                new BackgroundColor { Indexed = 64 })
            { PatternType = PatternValues.Solid }))
        { Count = 3 };

        var thin = new Border(
            new LeftBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin },
            new RightBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin },
            new TopBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin },
            new BottomBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin },
            new DiagonalBorder());
        var borders = new Borders(
            new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
            thin)
        { Count = 2 };

        var numberingFormats = new NumberingFormats { Count = (uint)_numberFormats.Count };
        foreach (var (code, id) in _numberFormats)
            numberingFormats.Append(new NumberingFormat { NumberFormatId = id, FormatCode = code });

        var cellFormats = new CellFormats { Count = (uint)_order.Count };
        foreach (var (bold, border, header, link, format) in _order)
        {
            var fontId = (link, bold || header) switch
            {
                (true, true) => 3u,
                (true, false) => 2u,
                (false, true) => 1u,
                _ => 0u,
            };
            var formatId = format.Length > 0 ? _numberFormats[format] : 0u;
            cellFormats.Append(new CellFormat
            {
                FontId = fontId,
                FillId = header ? 2u : 0u,
                BorderId = border ? 1u : 0u,
                NumberFormatId = formatId,
                ApplyFont = true,
                ApplyFill = header,
                ApplyBorder = border,
                ApplyNumberFormat = format.Length > 0,
                ApplyAlignment = true,
                // Текст ячейки может быть многострочным: в PDF в одной клетке
                // спокойно живут две-три строки, и обрезать их нельзя.
                Alignment = new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top },
            });
        }

        return new Stylesheet(numberingFormats, fonts, fills, borders,
            new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
            cellFormats,
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
    }
}
