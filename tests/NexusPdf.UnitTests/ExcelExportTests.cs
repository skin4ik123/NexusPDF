using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NexusPdf.Export;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Разбор значений и запись книги Excel.
///
/// Экспорт имеет смысл, только если числа стали числами: иначе по нему нельзя
/// посчитать сумму. И он же обязан НЕ трогать то, что числом лишь выглядит —
/// номер счёта, артикул с нулём впереди, телефон.
/// </summary>
public sealed class ExcelExportTests
{
    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("1 234,50", 1234.5)]        // обычный пробел как разделитель тысяч
    [InlineData("1 234,50", 1234.5)]        // неразрывный пробел — так делает Word
    [InlineData("1 234,50", 1234.5)]        // узкий неразрывный — так делает LaTeX
    [InlineData("1.234,56", 1234.56)]       // европейская запись
    [InlineData("1,234.56", 1234.56)]       // английская запись
    [InlineData("-42,5", -42.5)]
    [InlineData("(1 200,00)", -1200)]       // бухгалтерский минус скобками
    [InlineData("0,75", 0.75)]
    public void Numbers_Become_Numbers(string text, double expected)
    {
        var value = CellValueParser.Parse(text, decimalIsComma: true);
        Assert.Equal(CellKind.Number, value.Kind);
        Assert.Equal(expected, value.Number, 6);
    }

    /// <summary>«1,234» — единственный по-настоящему спорный случай, и решает его язык.</summary>
    [Fact]
    public void The_Ambiguous_Thousand_Separator_Follows_The_Language()
    {
        Assert.Equal(1.234, CellValueParser.Parse("1,234", decimalIsComma: true).Number, 6);
        Assert.Equal(1234, CellValueParser.Parse("1,234", decimalIsComma: false).Number, 6);
        Assert.Equal(1234, CellValueParser.Parse("1.234", decimalIsComma: true).Number, 6);
    }

    [Theory]
    [InlineData("40817810099910004312")]    // номер счёта: 20 цифр, Excel их округлит
    [InlineData("007")]                     // артикул с ведущим нулём
    [InlineData("+7 999 123-45-67")]        // телефон
    [InlineData("2026 год")]
    [InlineData("№12")]
    public void What_Only_Looks_Like_A_Number_Stays_Text(string text)
    {
        Assert.Equal(CellKind.Text, CellValueParser.Parse(text, decimalIsComma: true).Kind);
    }

    [Fact]
    public void Percents_Dates_And_Money_Keep_Their_Meaning()
    {
        var percent = CellValueParser.Parse("12,5%", decimalIsComma: true);
        Assert.Equal(CellKind.Percent, percent.Kind);
        Assert.Equal(0.125, percent.Number, 6);

        var date = CellValueParser.Parse("14.08.2026", decimalIsComma: true);
        Assert.Equal(CellKind.Date, date.Kind);
        Assert.Equal(new DateTime(2026, 8, 14), date.Date);

        var money = CellValueParser.Parse("1 500,00 ₽", decimalIsComma: true);
        Assert.Equal(CellKind.Currency, money.Kind);
        Assert.Equal(1500, money.Number, 6);
        Assert.Equal("₽", money.Currency);
    }

    /// <summary>
    /// Управляющий символ из PDF не должен рушить книгу Excel.
    ///
    /// В XML 1.0 таких символов не существует, и один такой валил запись
    /// целиком: «hexadecimal value 0x02 is an invalid character». Word я
    /// защитил раньше, а Excel остался — и портовая форма уведомления
    /// по-прежнему не выгружалась в таблицу.
    /// </summary>
    [Fact]
    public void A_Control_Character_Does_Not_Break_The_Workbook()
    {
        var stx = ((char)0x02).ToString();
        var words = new[]
        {
            new PdfTextWord("до" + stx + "после", new PdfTextRect(50, 700, 120, 690), 8, 400, 0xFF000000),
            new PdfTextWord("вторая", new PdfTextRect(200, 700, 260, 690), 8, 400, 0xFF000000),
            new PdfTextWord("строка", new PdfTextRect(50, 680, 120, 670), 8, 400, 0xFF000000),
            new PdfTextWord("данных", new PdfTextRect(200, 680, 260, 670), 8, 400, 0xFF000000),
        };
        var rulings = new PdfRulingLine[]
        {
            new(true, 705, 40, 300, 0.8), new(true, 685, 40, 300, 0.8), new(true, 665, 40, 300, 0.8),
            new(false, 40, 665, 705, 0.8), new(false, 170, 665, 705, 0.8), new(false, 300, 665, 705, 0.8),
        };
        var layout = PageAnalyzer.Analyze(
            0, 595, 842, words, rulings, Array.Empty<PdfFormFieldValue>());

        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "управляющие.xlsx");

        var summary = XlsxExporter.Write(
            path, new[] { new ExportPage(layout, Array.Empty<PdfPageLink>()) });

        Assert.True(summary.Tables >= 1);
        Assert.True(new FileInfo(path).Length > 500);
    }

    [Fact]
    public void Column_Names_Follow_Excel()
    {
        Assert.Equal("A", XlsxExporter.ColumnName(0));
        Assert.Equal("Z", XlsxExporter.ColumnName(25));
        Assert.Equal("AA", XlsxExporter.ColumnName(26));
        Assert.Equal("AB", XlsxExporter.ColumnName(27));
        Assert.Equal("BA", XlsxExporter.ColumnName(52));
    }

    /// <summary>В книгу переносятся только те схемы ссылок, которые в ней осмысленны.</summary>
    [Fact]
    public void Only_Safe_Link_Schemes_Travel()
    {
        Assert.True(XlsxExporter.IsSafeLink("https://example.org/"));
        Assert.True(XlsxExporter.IsSafeLink("mailto:a@example.org"));
        Assert.False(XlsxExporter.IsSafeLink("javascript:alert(1)"));
        Assert.False(XlsxExporter.IsSafeLink("file:///C:/Windows/System32/cmd.exe"));
        Assert.False(XlsxExporter.IsSafeLink(null));
    }

    // ----- запись настоящего файла -----

    private static PdfTextWord Word(string text, double left, double bottom, double width = 40) =>
        new(text, new PdfTextRect(left, bottom + 10, left + width, bottom), 8, 400, 0xFF000000);

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "экспорт.xlsx");
    }

    private static (PageLayout Layout, IReadOnlyList<PdfPageLink> Links) SamplePage()
    {
        var rulings = new PdfRulingLine[]
        {
            new(true, 700, 40, 440, 0.8), new(true, 680, 40, 440, 0.8), new(true, 660, 40, 440, 0.8),
            new(false, 40, 660, 700, 0.8), new(false, 440, 660, 700, 0.8),
            new(false, 190, 660, 680, 0.8),     // верхняя строка — объединённая
            new(false, 320, 660, 700, 0.8),
        };
        var words = new[]
        {
            Word("Итого", 60, 684, 60), Word("Сумма", 330, 684, 50),
            Word("Болт", 50, 664), Word("10", 200, 664), Word("1 234,50", 330, 664, 60),
        };
        var links = new PdfPageLink[]
        {
            new(new PdfTextRect(45, 676, 120, 662), "https://example.org/bolt", -1),
        };
        var layout = PageAnalyzer.Analyze(0, 612, 792, words, rulings, Array.Empty<PdfFormFieldValue>());
        return (layout, links);
    }

    [Fact]
    public void The_Workbook_Opens_With_Numbers_Merges_And_A_Live_Link()
    {
        var path = TempFile();
        var (layout, links) = SamplePage();

        var summary = XlsxExporter.Write(path, new[] { new ExportPage(layout, links) });

        Assert.Equal(1, summary.Sheets);
        Assert.Equal(1, summary.RulingTables);
        Assert.Equal(1, summary.Links);
        Assert.True(summary.Numbers >= 2);

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook!.Descendants<Sheet>().Single();
        Assert.Equal("Стр. 1", sheet.Name!.Value);

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        var cells = worksheetPart.Worksheet!.Descendants<Cell>().ToList();

        // Число записано числом, а не строкой, которая на него похожа.
        var amount = cells.Single(c => c.CellReference == "C2");
        Assert.Equal(CellValues.Number, amount.DataType!.Value);
        Assert.Equal(1234.5, double.Parse(amount.CellValue!.Text,
            System.Globalization.CultureInfo.InvariantCulture), 6);

        // Объединённая шапка осталась объединённой.
        var merge = worksheetPart.Worksheet!.Descendants<MergeCell>().Single();
        Assert.Equal("A1:B1", merge.Reference!.Value);

        // Ссылка живая: у неё есть внешняя цель, а не просто синий текст.
        var hyperlink = worksheetPart.Worksheet!.Descendants<Hyperlink>().Single();
        Assert.Equal("A2", hyperlink.Reference!.Value);
        var relationship = worksheetPart.HyperlinkRelationships.Single(r => r.Id == hyperlink.Id!.Value);
        Assert.Equal("https://example.org/bolt", relationship.Uri.ToString());
    }

    /// <summary>Ссылка внутрь документа ведёт на лист той самой страницы.</summary>
    [Fact]
    public void An_Internal_Link_Points_At_The_Sheet_Of_Its_Page()
    {
        var path = TempFile();
        var (layout, _) = SamplePage();
        var second = PageAnalyzer.Analyze(1, 612, 792,
            new[] { Word("вторая страница", 40, 700, 100) },
            Array.Empty<PdfRulingLine>(), Array.Empty<PdfFormFieldValue>());
        var links = new PdfPageLink[] { new(new PdfTextRect(45, 676, 120, 662), null, 1) };

        XlsxExporter.Write(path, new[]
        {
            new ExportPage(layout, links),
            new ExportPage(second, Array.Empty<PdfPageLink>()),
        });

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart!;
        var first = workbookPart.Workbook!.Descendants<Sheet>().First();
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(first.Id!.Value!);

        var hyperlink = worksheetPart.Worksheet!.Descendants<Hyperlink>().Single();
        Assert.Equal("'Стр. 2'!A1", hyperlink.Location!.Value);
        Assert.Null(hyperlink.Id);
    }

    /// <summary>Пустой странице тоже полагается лист — иначе номера страниц разъедутся.</summary>
    [Fact]
    public void Sheet_Names_Are_Unique_And_Short()
    {
        var pages = Enumerable.Range(0, 3)
            .Select(i => new ExportPage(
                new PageLayout(i, 612, 792, Array.Empty<ExtractedTable>(), Array.Empty<TextLine>()),
                Array.Empty<PdfPageLink>()))
            .ToList();

        var names = XlsxExporter.SheetNames(pages);

        Assert.Equal(new[] { "Стр. 1", "Стр. 2", "Стр. 3" }, names);
        Assert.All(names, n => Assert.True(n.Length <= 31));
    }
}
