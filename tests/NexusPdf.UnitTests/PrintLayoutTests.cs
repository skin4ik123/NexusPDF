using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Геометрия раскладки. Проверяется положение каждой размещённой страницы в
/// пунктах, а не факт «раскладка построилась»: ошибку в координатах на бумаге
/// видно сразу, а в тесте «не упало» — никогда.
/// </summary>
public sealed class PrintLayoutTests
{
    private static readonly SizePt A4 = new(595.28, 841.89);
    private static readonly SizePt A3 = new(841.89, 1190.55);
    private static readonly PaperSizeOption A4Paper = new("A4", A4);

    /// <summary>Принтер без непечатаемых полей — чтобы в тестах геометрии не мешала лишняя величина.</summary>
    private static PrinterCapabilities Caps(MarginsPt? hard = null) => new()
    {
        PrinterName = "Тест",
        SupportsColor = true,
        PaperSizes = new[] { A4Paper, new PaperSizeOption("A3", A3) },
        HardMarginsByPaper = new Dictionary<string, MarginsPt>
        {
            ["A4"] = hard ?? MarginsPt.Zero,
            ["A3"] = hard ?? MarginsPt.Zero,
        },
    };

    private static IReadOnlyList<SourcePage> Pages(int count, SizePt? size = null) =>
        Enumerable.Range(0, count)
            .Select(i => new SourcePage("doc", i, size ?? A4))
            .ToList();

    private readonly PrintLayoutEngine _engine = new();

    [Fact]
    public void Actual_Size_Keeps_Scale_One_And_Centers_The_Page()
    {
        var sheets = _engine.BuildSheets(Pages(1), new LayoutSettings { Size = SizeMode.ActualSize },
            A4Paper, Caps());

        var page = Assert.Single(Assert.Single(sheets).Pages);
        Assert.Equal(1.0, page.Scale, 6);
        Assert.Equal(0, page.TargetRectPt.XPt, 3);
        Assert.Equal(0, page.TargetRectPt.YPt, 3);
        Assert.False(page.IsClipped);
    }

    [Fact]
    public void Actual_Size_On_Smaller_Paper_Reports_Clipping()
    {
        // A3 на A4 в фактическом размере обязан обрезаться, а не «подогнаться».
        var sheets = _engine.BuildSheets(Pages(1, A3), new LayoutSettings { Size = SizeMode.ActualSize },
            A4Paper, Caps());

        var page = Assert.Single(Assert.Single(sheets).Pages);
        Assert.Equal(1.0, page.Scale, 6);
        Assert.True(page.IsClipped);
        Assert.True(Assert.Single(sheets).HasClippedContent);
    }

    [Fact]
    public void Fit_Does_Not_Enlarge_Unless_Asked()
    {
        var small = new SizePt(200, 300);

        var noEnlarge = _engine.BuildSheets(Pages(1, small),
            new LayoutSettings { Size = SizeMode.Fit, AllowEnlarge = false }, A4Paper, Caps());
        Assert.Equal(1.0, Assert.Single(Assert.Single(noEnlarge).Pages).Scale, 6);

        var enlarge = _engine.BuildSheets(Pages(1, small),
            new LayoutSettings { Size = SizeMode.Fit, AllowEnlarge = true, Orientation = OrientationMode.Portrait },
            A4Paper, Caps());
        Assert.True(Assert.Single(Assert.Single(enlarge).Pages).Scale > 2.0);
    }

    [Fact]
    public void Shrink_Oversized_Leaves_Fitting_Pages_At_Full_Size()
    {
        var settings = new LayoutSettings { Size = SizeMode.ShrinkOversized, Orientation = OrientationMode.Portrait };

        var fits = _engine.BuildSheets(Pages(1), settings, A4Paper, Caps());
        Assert.Equal(1.0, Assert.Single(Assert.Single(fits).Pages).Scale, 6);

        var oversized = _engine.BuildSheets(Pages(1, A3), settings, A4Paper, Caps());
        var scaled = Assert.Single(Assert.Single(oversized).Pages);
        Assert.True(scaled.Scale < 1.0);
        Assert.False(scaled.IsClipped); // уменьшили — значит поместилось
    }

    [Fact]
    public void Fill_Sheet_Covers_The_Area_And_Clips()
    {
        var settings = new LayoutSettings { Size = SizeMode.FillSheet, Orientation = OrientationMode.Portrait };
        var sheets = _engine.BuildSheets(Pages(1, new SizePt(400, 400)), settings, A4Paper, Caps());

        var page = Assert.Single(Assert.Single(sheets).Pages);
        // Квадрат, растянутый до высоты A4, по ширине выйдет за лист.
        Assert.True(page.TargetRectPt.WidthPt >= A4.WidthPt - 0.01);
        Assert.True(page.IsClipped);
    }

    [Fact]
    public void Hard_Margins_Shrink_The_Usable_Area()
    {
        var margins = MarginsPt.Uniform(20);
        var sheets = _engine.BuildSheets(Pages(1, A4),
            new LayoutSettings { Size = SizeMode.Fit, Orientation = OrientationMode.Portrait },
            A4Paper, Caps(margins));

        var sheet = Assert.Single(sheets);
        Assert.Equal(20, sheet.PrintableAreaPt.XPt, 3);
        Assert.Equal(A4.WidthPt - 40, sheet.PrintableAreaPt.WidthPt, 3);

        var page = Assert.Single(sheet.Pages);
        // Страница обязана оказаться внутри печатаемой области, а не под роликами.
        Assert.True(page.TargetRectPt.IsInside(sheet.PrintableAreaPt),
            $"страница {page.TargetRectPt} вне области {sheet.PrintableAreaPt}");
    }

    [Fact]
    public void Automatic_Orientation_Picks_The_Larger_Scale()
    {
        // Широкая страница должна лечь на альбомный лист.
        var wide = new SizePt(800, 400);
        var sheets = _engine.BuildSheets(Pages(1, wide),
            new LayoutSettings { Size = SizeMode.Fit, Orientation = OrientationMode.Automatic },
            A4Paper, Caps());

        Assert.True(Assert.Single(sheets).PaperSizePt.IsLandscape);
    }

    [Fact]
    public void Position_Anchors_Put_The_Page_Where_Asked()
    {
        var small = new SizePt(200, 200);
        var caps = Caps();

        var topLeft = _engine.BuildSheets(Pages(1, small),
            new LayoutSettings { Size = SizeMode.ActualSize, Position = PagePosition.TopLeft, Orientation = OrientationMode.Portrait },
            A4Paper, caps);
        var tl = Assert.Single(Assert.Single(topLeft).Pages).TargetRectPt;
        Assert.Equal(0, tl.XPt, 3);
        Assert.Equal(0, tl.YPt, 3);

        var bottomRight = _engine.BuildSheets(Pages(1, small),
            new LayoutSettings { Size = SizeMode.ActualSize, Position = PagePosition.BottomRight, Orientation = OrientationMode.Portrait },
            A4Paper, caps);
        var br = Assert.Single(Assert.Single(bottomRight).Pages).TargetRectPt;
        Assert.Equal(A4.WidthPt - 200, br.XPt, 3);
        Assert.Equal(A4.HeightPt - 200, br.YPt, 3);
    }

    // ----- Несколько страниц на листе -----

    [Fact]
    public void NUp_Puts_The_Right_Number_Of_Pages_On_Each_Sheet()
    {
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.NUp,
            NUp = new NUpSettings { Rows = 2, Columns = 2 },
        };
        var sheets = _engine.BuildSheets(Pages(7), settings, A4Paper, Caps());

        Assert.Equal(2, sheets.Count);
        Assert.Equal(4, sheets[0].Pages.Count);
        Assert.Equal(3, sheets[1].Pages.Count); // последний лист неполный, пустых ячеек не создаём
    }

    [Fact]
    public void NUp_Cells_Do_Not_Overlap_And_Stay_On_The_Sheet()
    {
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.NUp,
            NUp = new NUpSettings { Rows = 2, Columns = 2, HorizontalGapPt = 10, VerticalGapPt = 10 },
        };
        var sheet = _engine.BuildSheets(Pages(4), settings, A4Paper, Caps()).Single();

        foreach (var page in sheet.Pages)
            Assert.True(page.TargetRectPt.IsInside(sheet.PrintableAreaPt),
                $"ячейка {page.TargetRectPt} вышла за лист {sheet.PrintableAreaPt}");

        for (var i = 0; i < sheet.Pages.Count; i++)
        for (var j = i + 1; j < sheet.Pages.Count; j++)
        {
            var overlap = sheet.Pages[i].TargetRectPt.Intersect(sheet.Pages[j].TargetRectPt);
            Assert.True(overlap.IsEmpty,
                $"страницы {i} и {j} перекрываются на {overlap.WidthPt:F1}x{overlap.HeightPt:F1} пт");
        }
    }

    [Fact]
    public void NUp_Order_Right_To_Left_Mirrors_Columns()
    {
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.NUp,
            NUp = new NUpSettings { Rows = 1, Columns = 2, Order = NUpOrder.RowsRightToLeft },
        };
        var sheet = _engine.BuildSheets(Pages(2), settings, A4Paper, Caps()).Single();

        // Первая страница задания должна оказаться СПРАВА.
        Assert.True(sheet.Pages[0].TargetRectPt.XPt > sheet.Pages[1].TargetRectPt.XPt);
    }

    [Fact]
    public void NUp_Uniform_Scale_Makes_All_Pages_Equal()
    {
        var mixed = new List<SourcePage>
        {
            new("doc", 0, A4),
            new("doc", 1, new SizePt(300, 400)),
        };
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.NUp,
            NUp = new NUpSettings { Rows = 1, Columns = 2, UniformScale = true, AutoRotatePages = false },
        };
        var sheet = _engine.BuildSheets(mixed, settings, A4Paper, Caps()).Single();

        Assert.Equal(sheet.Pages[0].Scale, sheet.Pages[1].Scale, 6);
    }

    // ----- Плакат -----

    [Fact]
    public void Poster_Splits_A_Large_Page_Into_Tiles_That_Cover_It()
    {
        var big = new SizePt(A4.WidthPt * 2, A4.HeightPt * 2);
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.Poster,
            Orientation = OrientationMode.Portrait,
            Poster = new PosterSettings { Scale = 1.0, OverlapPt = 0 },
        };
        var sheets = _engine.BuildSheets(Pages(1, big), settings, A4Paper, Caps());

        Assert.Equal(4, sheets.Count);

        // Плитки вместе обязаны покрыть всю исходную страницу без дыр.
        var covered = sheets.Sum(s => s.Pages[0].SourceRectPt.WidthPt * s.Pages[0].SourceRectPt.HeightPt);
        Assert.Equal(big.WidthPt * big.HeightPt, covered, 1);
    }

    [Fact]
    public void Poster_Overlap_Makes_Tiles_Share_A_Strip()
    {
        var big = new SizePt(A4.WidthPt * 2, A4.HeightPt);
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.Poster,
            Orientation = OrientationMode.Portrait,
            Poster = new PosterSettings { Scale = 1.0, OverlapPt = 20 },
        };
        var sheets = _engine.BuildSheets(Pages(1, big), settings, A4Paper, Caps());

        Assert.True(sheets.Count >= 2);
        var first = sheets[0].Pages[0].SourceRectPt;
        var second = sheets[1].Pages[0].SourceRectPt;
        // Начало второй плитки должно быть левее конца первой ровно на overlap.
        Assert.Equal(first.RightPt - 20, second.XPt, 1);
    }

    [Fact]
    public void Poster_Excluded_Tiles_Are_Not_Printed()
    {
        var big = new SizePt(A4.WidthPt * 2, A4.HeightPt * 2);
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.Poster,
            Orientation = OrientationMode.Portrait,
            Poster = new PosterSettings
            {
                Scale = 1.0,
                OverlapPt = 0,
                ExcludedTiles = new HashSet<(int, int)> { (0, 0) },
            },
        };
        var sheets = _engine.BuildSheets(Pages(1, big), settings, A4Paper, Caps());
        Assert.Equal(3, sheets.Count);
    }

    // ----- Буклет -----

    [Fact]
    public void Booklet_Produces_Two_Sides_Per_Sheet_Of_Four_Pages()
    {
        var settings = new LayoutSettings { Imposition = ImpositionMode.Booklet };
        var sheets = _engine.BuildSheets(Pages(8), settings, A4Paper, Caps());

        Assert.Equal(4, sheets.Count); // 8 страниц = 2 листа = 4 стороны
        Assert.True(sheets[0].IsFront);
        Assert.False(sheets[1].IsFront);
        Assert.Equal(1, sheets[0].PairedSheetIndex);
        Assert.Equal(0, sheets[1].PairedSheetIndex);
    }

    [Fact]
    public void Booklet_Places_Two_Pages_Side_By_Side_Without_Overlap()
    {
        var settings = new LayoutSettings { Imposition = ImpositionMode.Booklet };
        var sheet = _engine.BuildSheets(Pages(8), settings, A4Paper, Caps())[0];

        Assert.Equal(2, sheet.Pages.Count);
        var overlap = sheet.Pages[0].TargetRectPt.Intersect(sheet.Pages[1].TargetRectPt);
        Assert.True(overlap.IsEmpty, "половинки буклета не должны налезать друг на друга");
    }

    [Fact]
    public void Booklet_Creep_Shifts_Outer_Sheets_More_Than_Inner()
    {
        var withCreep = new LayoutSettings
        {
            Imposition = ImpositionMode.Booklet,
            Booklet = new BookletSettings { CompensateCreep = true, PaperThicknessPt = 2.0 },
        };
        var sheets = _engine.BuildSheets(Pages(16), withCreep, A4Paper, Caps());

        // Внешний лист сигнатуры (первый) сдвинут сильнее внутреннего (последнего).
        var outerShift = sheets[0].Pages[0].TargetRectPt.XPt;
        var innerShift = sheets[^2].Pages[0].TargetRectPt.XPt;
        Assert.True(outerShift > innerShift,
            $"внешний лист {outerShift:F2} должен быть сдвинут сильнее внутреннего {innerShift:F2}");
    }
}

/// <summary>
/// Порядок страниц буклета — чистая арифметика, которую иначе можно проверить
/// только сложив стопку бумаги пополам.
/// </summary>
public sealed class BookletImpositionTests
{
    [Fact]
    public void Eight_Page_Booklet_Has_The_Classic_Order()
    {
        var order = BookletImposition.SheetOrder(8);

        Assert.Equal(4, order.Count); // 2 листа × 2 стороны
        Assert.Equal(new[] { 7, 0 }, order[0]); // лицо 1: страницы 8 и 1
        Assert.Equal(new[] { 1, 6 }, order[1]); // оборот 1: страницы 2 и 7
        Assert.Equal(new[] { 5, 2 }, order[2]); // лицо 2: страницы 6 и 3
        Assert.Equal(new[] { 3, 4 }, order[3]); // оборот 2: страницы 4 и 5
    }

    [Fact]
    public void Every_Page_Appears_Exactly_Once()
    {
        foreach (var count in new[] { 4, 8, 12, 16, 32 })
        {
            var used = BookletImposition.SheetOrder(count).SelectMany(s => s).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(0, count), used);
        }
    }

    [Fact]
    public void Page_Count_Is_Rounded_Up_To_A_Multiple_Of_Four()
    {
        Assert.Equal(4, BookletImposition.RoundUpToFour(1));
        Assert.Equal(4, BookletImposition.RoundUpToFour(4));
        Assert.Equal(8, BookletImposition.RoundUpToFour(5));
        Assert.Equal(12, BookletImposition.RoundUpToFour(9));
    }

    [Fact]
    public void Signatures_Cover_The_Whole_Document()
    {
        var signatures = BookletImposition.SplitSignatures(20, 8);
        Assert.Equal(3, signatures.Count);
        Assert.Equal(0, signatures[0].FirstPage);
        Assert.Equal(8, signatures[1].FirstPage);
        Assert.Equal(16, signatures[2].FirstPage);
    }

    [Fact]
    public void Whole_Document_As_One_Signature_When_Size_Is_Zero()
    {
        var signatures = BookletImposition.SplitSignatures(10, 0);
        var only = Assert.Single(signatures);
        Assert.Equal(0, only.FirstPage);
        Assert.Equal(12, only.Count); // 10 округляется до 12
    }

    [Fact]
    public void Manual_Duplex_Reverses_The_Second_Pass()
    {
        // Стопка выходит из принтера в обратном порядке — второй проход обязан
        // это учитывать, иначе обороты лягут не на свои листы.
        var (first, second) = BookletImposition.ManualDuplexOrder(6);
        Assert.Equal(new[] { 0, 2, 4 }, first);
        Assert.Equal(new[] { 5, 3, 1 }, second);
    }
}
