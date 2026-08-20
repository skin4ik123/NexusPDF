using NexusPdf.Export;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Восстановление таблиц из координат.
///
/// Проверяется именно то, ради чего это всё написано: объединённые ячейки,
/// разорванные на куски границы, две независимые таблицы на одной странице и
/// отказ выдавать обычный абзац за таблицу.
/// </summary>
public sealed class TableDetectionTests
{
    private static PdfTextWord Word(string text, double left, double bottom, double width = 40, double height = 10) =>
        new(text, new PdfTextRect(left, bottom + height, left + width, bottom), height * 0.8, 400, 0xFF000000);

    private static PdfRulingLine H(double y, double from, double to) => new(true, y, from, to, 0.8);
    private static PdfRulingLine V(double x, double from, double to) => new(false, x, from, to, 0.8);

    /// <summary>
    /// Одна граница, нарисованная кусочками, остаётся ОДНОЙ колонкой.
    ///
    /// Генераторы часто рисуют вертикальную линию таблицы не целиком, а
    /// отдельным отрезком на каждую строку. Сшивание оставляет их разными
    /// линиями с одинаковой позицией, и сетка получала лишние колонки нулевой
    /// ширины: у морского чек-листа RLM из четырёх колонок выходило десять, а
    /// графы для отметок схлопывались в нитку.
    /// </summary>
    [Fact]
    public void Segments_Of_One_Border_Do_Not_Add_Columns()
    {
        var rulings = new[]
        {
            H(700, 40, 300), H(680, 40, 300), H(660, 40, 300),
            // Левая, средняя и правая границы — каждая двумя отрезками,
            // по одному на строку.
            V(40, 680, 700), V(40, 660, 680),
            V(170, 680, 700), V(170, 660, 680),
            V(300, 680, 700), V(300, 660, 680),
        };
        var words = new[]
        {
            Word("слева", 50, 685), Word("справа", 180, 685),
            Word("низ", 50, 665), Word("тоже", 180, 665),
        };

        var tables = RulingTableDetector.Detect(rulings, words);

        var table = Assert.Single(tables);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(2, table.RowCount);
        // Границы — ровно три, без повторов: 40, 170 и 300.
        Assert.Equal(new[] { 40.0, 170.0, 300.0 }, table.ColumnEdges);
    }

    /// <summary>Сетка 2×3 по нарисованным границам: тексты попадают в свои ячейки.</summary>
    [Fact]
    public void A_Drawn_Grid_Becomes_A_Table()
    {
        var rulings = new[]
        {
            H(700, 40, 440), H(680, 40, 440), H(660, 40, 440),
            V(40, 660, 700), V(190, 660, 700), V(320, 660, 700), V(440, 660, 700),
        };
        var words = new[]
        {
            Word("Товар", 50, 684), Word("Кол-во", 200, 684), Word("Цена", 330, 684),
            Word("Болт", 50, 664), Word("10", 200, 664), Word("25,50", 330, 664),
        };

        var table = Assert.Single(RulingTableDetector.Detect(rulings, words));

        Assert.Equal(TableSource.Ruling, table.Source);
        Assert.Equal(2, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal("Товар", table.At(0, 0)!.Text);
        Assert.Equal("Цена", table.At(0, 2)!.Text);
        Assert.Equal("25,50", table.At(1, 2)!.Text);
    }

    /// <summary>
    /// Нет границы между клетками — значит это ОДНА ячейка на две колонки.
    /// Без этого шапка «Итого за год» разъехалась бы по двум колонкам.
    /// </summary>
    [Fact]
    public void A_Missing_Border_Means_A_Merged_Cell()
    {
        var rulings = new[]
        {
            H(700, 40, 440), H(680, 40, 440), H(660, 40, 440),
            V(40, 660, 700), V(440, 660, 700),
            V(190, 660, 680),   // разделитель есть только в нижней строке
            V(320, 660, 700),
        };
        var words = new[]
        {
            Word("Итого за год", 60, 684, width: 120),
            Word("Прочее", 330, 684),
            Word("Болт", 50, 664), Word("10", 200, 664), Word("25", 330, 664),
        };

        var table = Assert.Single(RulingTableDetector.Detect(rulings, words));

        var merged = table.At(0, 0)!;
        Assert.Equal(2, merged.ColumnSpan);
        Assert.Equal("Итого за год", merged.Text);
        Assert.Null(table.At(0, 1));           // клетку накрыла соседка
        Assert.Equal("Прочее", table.At(0, 2)!.Text);
        Assert.Equal("10", table.At(1, 1)!.Text);
    }

    /// <summary>Границу часто рисуют кусочками — по кусочку на ячейку.</summary>
    [Fact]
    public void Border_Drawn_In_Pieces_Is_Stitched_Back()
    {
        var rulings = new[]
        {
            H(700, 40, 190), H(700, 190, 320), H(700, 320, 440),
            H(660, 40, 190), H(660, 190, 440),
            V(40, 660, 700), V(190, 660, 700), V(320, 660, 700), V(440, 660, 700),
        };
        var words = new[] { Word("A", 50, 674), Word("B", 200, 674), Word("C", 330, 674) };

        var table = Assert.Single(RulingTableDetector.Detect(rulings, words));

        Assert.Equal(1, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal("C", table.At(0, 2)!.Text);
    }

    /// <summary>Две таблицы на странице остаются двумя, а не сливаются в одну сетку.</summary>
    [Fact]
    public void Two_Separate_Grids_Stay_Separate()
    {
        var rulings = new[]
        {
            H(700, 40, 200), H(680, 40, 200), V(40, 680, 700), V(200, 680, 700),
            H(500, 40, 200), H(480, 40, 200), V(40, 480, 500), V(200, 480, 500),
        };
        var words = new[] { Word("сверху", 50, 684), Word("снизу", 50, 484) };

        var tables = RulingTableDetector.Detect(rulings, words);

        Assert.Equal(2, tables.Count);
        Assert.Equal("сверху", tables[0].At(0, 0)!.Text);   // порядок сверху вниз
        Assert.Equal("снизу", tables[1].At(0, 0)!.Text);
    }

    /// <summary>Рамка без текста — это рамка вокруг картинки, а не таблица.</summary>
    [Fact]
    public void An_Empty_Frame_Is_Not_A_Table()
    {
        var rulings = new[]
        {
            H(700, 40, 440), H(600, 40, 440), V(40, 600, 700), V(440, 600, 700),
        };

        Assert.Empty(RulingTableDetector.Detect(rulings, Array.Empty<PdfTextWord>()));
    }

    /// <summary>Без линий колонки находятся по сквозным просветам.</summary>
    [Fact]
    public void Columns_Without_Borders_Are_Found_By_Gaps()
    {
        var words = new List<PdfTextWord>();
        var rows = new[]
        {
            new[] { "Товар", "Кол-во", "Цена" },
            new[] { "Болт", "10", "25,50" },
            new[] { "Гайка", "20", "7,00" },
            new[] { "Шайба", "5", "3,10" },
        };
        for (var r = 0; r < rows.Length; r++)
        {
            var y = 700 - r * 14;
            words.Add(Word(rows[r][0], 40, y, width: 45));
            words.Add(Word(rows[r][1], 200, y, width: 35));
            words.Add(Word(rows[r][2], 320, y, width: 40));
        }

        var lines = TextLineBuilder.Build(words);
        Assert.Equal(4, lines.Count);

        var table = Assert.Single(WhitespaceTableDetector.Detect(lines));
        Assert.Equal(TableSource.Whitespace, table.Source);
        Assert.Equal(4, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal("Гайка", table.At(2, 0)!.Text);
        Assert.Equal("7,00", table.At(2, 2)!.Text);

        // Догадка не притворяется фактом.
        Assert.True(table.Confidence < 1.0);
    }

    /// <summary>Обычный абзац таблицей не объявляется: сквозных просветов в нём нет.</summary>
    [Fact]
    public void A_Paragraph_Is_Not_Mistaken_For_A_Table()
    {
        var words = new List<PdfTextWord>();
        var text = new[]
        {
            new[] { "Настоящий", "договор", "составлен", "в", "двух" },
            new[] { "экземплярах,", "имеющих", "равную", "силу", "для" },
            new[] { "обеих", "сторон", "и", "вступает", "в" },
            new[] { "силу", "с", "момента", "подписания", "сторонами" },
        };
        for (var r = 0; r < text.Length; r++)
        {
            var x = 40.0;
            foreach (var word in text[r])
            {
                words.Add(Word(word, x, 700 - r * 14, width: word.Length * 5.0));
                x += word.Length * 5.0 + 4;   // обычные пробелы, а не колонки
            }
        }

        Assert.Empty(WhitespaceTableDetector.Detect(TextLineBuilder.Build(words)));
    }

    /// <summary>Слова одной строки собираются вместе даже при разном кегле.</summary>
    [Fact]
    public void Words_Of_Different_Sizes_Still_Form_One_Line()
    {
        var words = new[]
        {
            Word("Заголовок", 40, 700, width: 90, height: 18),
            Word("сноска", 140, 702, width: 40, height: 7),
            Word("следующая", 40, 660, width: 80),
        };

        var lines = TextLineBuilder.Build(words);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Заголовок сноска", lines[0].Text);
        Assert.Equal("следующая", lines[1].Text);
    }

    /// <summary>Значение заполненного поля формы попадает в таблицу наравне с текстом.</summary>
    [Fact]
    public void A_Filled_Form_Field_Lands_In_Its_Cell()
    {
        var rulings = new[]
        {
            H(700, 40, 440), H(680, 40, 440),
            V(40, 680, 700), V(240, 680, 700), V(440, 680, 700),
        };
        var words = new[] { Word("Заказчик", 50, 684, width: 70) };
        var fields = new[]
        {
            new PdfFormFieldValue("Customer", "ООО «Ромашка»", new PdfTextRect(250, 698, 430, 682)),
        };

        var layout = PageAnalyzer.Analyze(0, 612, 792, words, rulings, fields);

        var table = Assert.Single(layout.Tables);
        Assert.Equal("Заказчик", table.At(0, 0)!.Text);
        Assert.Equal("ООО «Ромашка»", table.At(0, 1)!.Text);
    }
}
