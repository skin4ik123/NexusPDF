using System.Diagnostics;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Куда в бланке пишет щелчок: пустая клетка таблицы, свободная часть клетки
/// с подписью, строка-подчёркивание или пропуск, набранный «____». Всё — по
/// линиям и тексту страницы, в отображаемых пунктах (y вниз).
/// </summary>
public sealed class FormTargetsTests
{
    private const double Size = 10;

    /// <summary>Сетка: левый верхний угол, ширины колонок и высоты строк.</summary>
    private static List<PageRuling> Grid(double x0, double y0, double[] columns, double[] rows)
    {
        var width = columns.Sum();
        var height = rows.Sum();
        var result = new List<PageRuling>();
        var y = y0;
        result.Add(new PageRuling(true, y, x0, x0 + width));
        foreach (var row in rows) result.Add(new PageRuling(true, y += row, x0, x0 + width));
        var x = x0;
        result.Add(new PageRuling(false, x, y0, y0 + height));
        foreach (var column in columns) result.Add(new PageRuling(false, x += column, y0, y0 + height));
        return result;
    }

    /// <summary>Текст «моноширинным» шрифтом: буква — половина кегля.</summary>
    private static PageTextObject Text(string text, double x, double baseline, double size = Size) =>
        new([0], text, new OverlayBox(x, baseline - 0.8 * size, text.Length * size / 2, size), x, baseline, 0, size,
            "Helvetica", false, false, 0xFF000000, false, false, false,
            Enumerable.Repeat(size / 2, text.Length).ToList(), "");

    private static FormTargetIndex Index(IEnumerable<PageRuling> rulings, params PageTextObject[] objects) =>
        FormTargetIndex.Build(new PageTextLayout(objects, [], rulings.ToList()), Size);

    private static void AssertBox(OverlayBox expected, OverlayBox actual, double tolerance = 0.05)
    {
        Assert.InRange(actual.XPt, expected.XPt - tolerance, expected.XPt + tolerance);
        Assert.InRange(actual.YPt, expected.YPt - tolerance, expected.YPt + tolerance);
        Assert.InRange(actual.WidthPt, expected.WidthPt - tolerance, expected.WidthPt + tolerance);
        Assert.InRange(actual.HeightPt, expected.HeightPt - tolerance, expected.HeightPt + tolerance);
    }

    [Fact]
    public void Empty_Cell_In_A_Grid()
    {
        var index = Index(Grid(100, 100, [80, 80, 80], [20, 20, 20]));

        var first = index.At(150, 110);
        var middle = index.At(230, 130);

        Assert.NotNull(first);
        Assert.Equal(FormTargetKind.Cell, first!.Kind);
        AssertBox(new OverlayBox(100, 100, 80, 20), first.CellPt);
        AssertBox(first.CellPt, first.AreaPt);
        Assert.Equal((0, 0), (first.Row, first.Column));
        Assert.NotNull(middle);
        AssertBox(new OverlayBox(180, 120, 80, 20), middle!.CellPt);
        Assert.Equal((1, 1), (middle.Row, middle.Column));
        Assert.Equal(first.TableId, middle.TableId);
    }

    [Fact]
    public void Merged_Cell_Is_One_Cell()
    {
        // Между первыми двумя колонками в верхней строке линии нет.
        var rulings = Grid(100, 100, [80, 80], [20, 20])
            .Where(r => !(r is { Horizontal: false } && Math.Abs(r.PositionPt - 180) < 0.1)).ToList();
        rulings.Add(new PageRuling(false, 180, 120, 140));

        var merged = Index(rulings).At(150, 110);
        var below = Index(rulings).At(150, 130);

        AssertBox(new OverlayBox(100, 100, 160, 20), merged!.CellPt);
        AssertBox(new OverlayBox(100, 120, 80, 20), below!.CellPt);
    }

    [Fact]
    public void Slightly_Short_And_Split_Lines_Still_Close_A_Cell()
    {
        var rulings = new List<PageRuling>
        {
            new(true, 100, 100.5, 150), new(true, 100.8, 151, 199.5),   // верх из двух кусков, чуть вкривь
            new(true, 140, 100, 200),
            new(false, 100, 100, 139.2), new(false, 200, 100.6, 140),
        };
        AssertBox(new OverlayBox(100, 100, 100, 40), Index(rulings).At(150, 120)!.CellPt, tolerance: 0.6);
    }

    [Fact]
    public void Open_Rectangle_Is_No_Cell()
    {
        var rulings = new List<PageRuling>
        {
            new(true, 100, 100, 200), new(true, 160, 100, 200), new(false, 100, 100, 160),
        };
        Assert.Null(Index(rulings).At(150, 130));
    }

    [Fact]
    public void Page_Border_Is_No_Cell()
    {
        var rulings = Grid(20, 20, [570], [750]);
        Assert.Null(Index(rulings).At(300, 400));
    }

    [Fact]
    public void Caption_Cell_Targets_The_Free_Part_On_The_Side_Of_The_Click()
    {
        var caption = Text("Name of ship", 102, 109, 8);   // рамка 102…150 × 102,6…110,6
        var index = Index(Grid(100, 100, [200], [40]), caption);

        var right = index.At(250, 110);
        var below = index.At(120, 130);

        Assert.Equal(FormTargetKind.CaptionCell, right!.Kind);
        AssertBox(new OverlayBox(100, 100, 200, 40), right.CellPt);
        Assert.InRange(right.AreaPt.XPt, 150, 154);
        Assert.Equal(300, right.AreaPt.XPt + right.AreaPt.WidthPt, 1);
        Assert.Equal(100, right.AreaPt.YPt, 1);
        Assert.Equal(FormTargetKind.CaptionCell, below!.Kind);
        Assert.Equal(100, below.AreaPt.XPt, 1);
        Assert.InRange(below.AreaPt.YPt, 110.6, 114);
        Assert.Equal(140, below.AreaPt.YPt + below.AreaPt.HeightPt, 1);
    }

    [Fact]
    public void Full_Cell_Is_No_Target()
    {
        var index = Index(Grid(100, 100, [80], [20]), Text("XXXXXXXXXXXXXXX", 102, 114));
        Assert.Null(index.At(150, 110));
    }

    [Fact]
    public void Underline_Line_Targets_From_The_Click_To_The_Line_End()
    {
        var index = Index([new PageRuling(true, 300, 100, 300)], Text("Name", 70, 299));

        var target = index.At(150, 295);

        Assert.Equal(FormTargetKind.Line, target!.Kind);
        Assert.Equal(150, target.AreaPt.XPt, 1);
        Assert.Equal(300, target.AreaPt.XPt + target.AreaPt.WidthPt, 1);
        Assert.Equal(299, target.AreaPt.YPt + target.AreaPt.HeightPt, 1);
        Assert.Equal(100, target.CellPt.XPt, 1);
        Assert.Equal(100, index.At(99, 295)!.AreaPt.XPt, 1);   // чуть левее начала — от начала линии
        Assert.Null(index.At(50, 295));
        Assert.Null(index.At(150, 250));                          // высоко над линией
    }

    [Fact]
    public void Line_With_Text_Above_Or_Too_Short_Is_No_Target()
    {
        var written = Index([new PageRuling(true, 300, 100, 300)], Text("filled in", 120, 297));
        Assert.Null(written.At(200, 295));

        var shortLine = Index([new PageRuling(true, 300, 100, 115)]);
        Assert.Null(shortLine.At(105, 295));
    }

    [Fact]
    public void Text_Blanks_Made_Of_Underscores_Or_Dots()
    {
        var index = Index([], Text("ETA ____", 100, 400), Text("..........", 100, 450), Text("and so on...", 100, 500));

        var underscores = index.At(125, 397);
        var dots = index.At(110, 447);

        Assert.Equal(FormTargetKind.TextBlank, underscores!.Kind);
        Assert.Equal(120, underscores.AreaPt.XPt, 1);
        Assert.Equal(20, underscores.AreaPt.WidthPt, 1);
        Assert.Equal(FormTargetKind.TextBlank, dots!.Kind);
        Assert.Equal(100, dots.AreaPt.XPt, 1);
        Assert.Equal(50, dots.AreaPt.WidthPt, 1);
        // Многоточие в обычной фразе — не пропуск бланка.
        Assert.Null(index.At(152, 497));
    }

    [Fact]
    public void Next_Goes_Row_Major_Skipping_Full_Cells()
    {
        var index = Index(Grid(100, 100, [80, 80, 80], [20, 20]), Text("XXXXXXXXXXXXXXX", 182, 114));
        var first = index.At(150, 110)!;

        var second = index.Next(first, backwards: false);
        var third = index.Next(second!, backwards: false);
        var back = index.Next(third!, backwards: true);

        AssertBox(new OverlayBox(260, 100, 80, 20), second!.CellPt);
        AssertBox(new OverlayBox(100, 120, 80, 20), third!.CellPt);
        AssertBox(second.CellPt, back!.CellPt);
        Assert.Null(index.Next(first, backwards: true));
        Assert.Null(index.Next(index.At(300, 130)!, backwards: false));
    }

    [Fact]
    public void Next_Stays_In_Its_Table()
    {
        var rulings = Grid(100, 100, [80], [20]).Concat(Grid(100, 300, [80], [20])).ToList();
        var index = Index(rulings);

        var top = index.At(150, 110)!;
        var bottom = index.At(150, 310)!;

        Assert.NotEqual(top.TableId, bottom.TableId);
        Assert.Null(index.Next(top, backwards: false));
    }

    [Fact]
    public void Cell_Around_An_Area()
    {
        var index = Index(Grid(100, 100, [80, 80], [20]));
        AssertBox(new OverlayBox(100, 100, 80, 20), index.CellAround(new OverlayBox(110, 104, 40, 10))!.Value);
        Assert.Null(index.CellAround(new OverlayBox(150, 104, 70, 10)));
    }

    [Fact]
    public void Fifty_Thousand_Rulings_Stay_Fast()
    {
        // 65 × 65 таблиц по 5 × 5 клеток — 50 700 линий.
        var rulings = new List<PageRuling>();
        for (var ty = 0; ty < 65; ty++)
        for (var tx = 0; tx < 65; tx++)
            rulings.AddRange(Grid(tx * 70, ty * 70, [10, 10, 10, 10, 10], [10, 10, 10, 10, 10]));
        var random = new Random(7);
        var points = Enumerable.Range(0, 100)
            .Select(_ => (X: random.Next(65) * 70 + 5 + random.Next(5) * 10, Y: random.Next(65) * 70 + 5 + random.Next(5) * 10))
            .ToList();
        FormTargetIndex.Build(new PageTextLayout([], [], Grid(0, 0, [10], [10])), Size).At(5, 5);   // прогрев

        var watch = Stopwatch.StartNew();
        var index = FormTargetIndex.Build(new PageTextLayout([], [], rulings), Size);
        var found = points.Count(p => index.At(p.X, p.Y) is { Kind: FormTargetKind.Cell });
        watch.Stop();

        Assert.Equal(100, found);
        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Huge_Grid_Gives_No_Tab_Order_Instead_Of_Freezing()
    {
        // 150 × 150 клеток — 22 500 промежутков сетки: не таблица бланка, а чертёж.
        var cells = Enumerable.Repeat(10.0, 150).ToArray();
        var index = Index(Grid(0, 0, cells, cells));
        var first = index.At(5, 5)!;

        var watch = Stopwatch.StartNew();
        var next = index.Next(first, backwards: false);
        watch.Stop();

        Assert.Null(next);
        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Cell_Whose_Sides_Do_Not_Touch_The_Top_Line_Has_No_Tab_Order()
    {
        // Боковые линии начинаются на 5 пт ниже верхней: клетка замкнута (92 %),
        // но верхняя линия ни с чем не связана — таблицы из одной линии нет.
        var rulings = new List<PageRuling>
        {
            new(true, 100, 100, 200), new(true, 160, 100, 200),
            new(false, 100, 105, 160), new(false, 200, 105, 160),
        };
        var index = Index(rulings);
        var cell = index.At(150, 130);

        Assert.Equal(FormTargetKind.Cell, cell!.Kind);
        Assert.Null(index.Next(cell, backwards: false));
    }
}
