using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using NexusPdf.Export;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Весь путь целиком: настоящий PDF → PDFium → разбор → книга Excel.
///
/// Отдельные части проверяются модульными тестами, но только здесь видно, что
/// координаты из движка и алгоритм разбора говорят на одном языке.
/// </summary>
public sealed class PdfToExcelTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _engine = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _engine.DisposeAsync();

    [Fact]
    public async Task Words_Lines_And_Field_Values_Come_Out_Of_A_Real_Pdf()
    {
        var path = PdfFixture.WriteTableToTemp("таблица.pdf");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var words = await doc.GetTextWordsAsync(0, CancellationToken.None);
        var rulings = await doc.GetRulingLinesAsync(0, CancellationToken.None);
        var fields = await doc.GetFormFieldValuesAsync(0, CancellationToken.None);

        // Слова собраны из символов и стоят там, где нарисованы.
        var price = Assert.Single(words, w => w.Text == "25,50");
        Assert.InRange(price.RectPt.Left, 328, 340);
        Assert.InRange(price.RectPt.Bottom, 660, 670);

        // Тонкие залитые прямоугольники распознаны как границы.
        Assert.Equal(4, rulings.Count(r => r.IsHorizontal));
        Assert.Equal(4, rulings.Count(r => !r.IsHorizontal));

        // Повёрнутая подпись собрана в одно слово вдоль своей оси, а не
        // рассыпана на буквы, и направление чтения известно.
        var label = Assert.Single(words, w => w.Text == "LABEL");
        Assert.Equal(1, label.RotationQuarters);        // читается снизу вверх
        Assert.True(label.Height > label.Width);
        Assert.Contains(words, w => w.Text == "SIDE" && w.RotationQuarters == 1);

        // Значение поля формы в текст страницы не входит — его берут отдельно.
        var field = Assert.Single(fields);
        Assert.Equal("Acme Ltd", field.Value);
        Assert.Equal("Customer", field.Name);
        Assert.DoesNotContain("Acme", await doc.GetPageTextAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task A_Real_Pdf_Table_Becomes_A_Real_Workbook()
    {
        var path = PdfFixture.WriteTableToTemp("смета.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "смета.xlsx");

        await using (var doc = await _engine.OpenAsync(path, null, CancellationToken.None))
        {
            var layout = PageAnalyzer.Analyze(
                0, doc.Info.Pages[0].WidthPoints, doc.Info.Pages[0].HeightPoints,
                await doc.GetTextWordsAsync(0, CancellationToken.None),
                await doc.GetRulingLinesAsync(0, CancellationToken.None),
                await doc.GetFormFieldValuesAsync(0, CancellationToken.None));

            var table = Assert.Single(layout.Tables);
            Assert.Equal(TableSource.Ruling, table.Source);
            Assert.Equal(3, table.RowCount);
            Assert.Equal(3, table.ColumnCount);
            Assert.Equal("Item", table.At(0, 0)!.Text);
            Assert.Equal("Bolt", table.At(1, 0)!.Text);
            Assert.Equal("25,50", table.At(1, 2)!.Text);
            Assert.Equal("7,00", table.At(2, 2)!.Text);

            // Текст вне таблицы не потерялся и таблицей не притворился.
            Assert.Contains(layout.Lines, l => l.Text.Contains("Total order"));

            var summary = XlsxExporter.Write(target,
                new[] { new ExportPage(layout, await doc.GetPageLinksAsync(0, CancellationToken.None)) });
            Assert.Equal(1, summary.RulingTables);
            Assert.Equal(1, summary.Links);
        }

        using var workbook = SpreadsheetDocument.Open(target, false);
        var workbookPart = workbook.WorkbookPart!;
        var sheet = workbookPart.Workbook.Descendants<Sheet>().Single();
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        var cells = worksheetPart.Worksheet.Descendants<Cell>().ToList();

        // «25,50» из PDF стало числом 25.5 — по такому столбцу считается сумма.
        var price = cells.Single(c => c.CellReference == "C2");
        Assert.Equal(CellValues.Number, price.DataType!.Value);
        Assert.Equal(25.5, double.Parse(price.CellValue!.Text,
            System.Globalization.CultureInfo.InvariantCulture), 6);

        // Ссылка из PDF стала ссылкой книги.
        var hyperlink = worksheetPart.Worksheet.Descendants<Hyperlink>().Single();
        var relationship = worksheetPart.HyperlinkRelationships.Single(r => r.Id == hyperlink.Id!.Value);
        Assert.Equal("https://example.org/bolt", relationship.Uri.ToString());

        // Значение поля формы доехало до книги.
        Assert.Contains(cells, c => c.InlineString?.Text?.Text == "Acme Ltd");
    }
}
