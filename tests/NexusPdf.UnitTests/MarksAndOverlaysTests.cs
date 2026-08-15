using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Типографские метки и печатные наложения. Метка не на своём месте портит
/// весь тираж, поэтому проверяются координаты, а не факт «метка добавилась».
/// </summary>
public sealed class MarksAndOverlaysTests
{
    private static readonly SizePt A4 = new(595.28, 841.89);
    private static readonly PaperSizeOption A4Paper = new("A4", A4);

    private static PrinterCapabilities Caps() => new()
    {
        PrinterName = "Тест",
        PaperSizes = new[] { A4Paper },
        HardMarginsByPaper = new Dictionary<string, MarginsPt> { ["A4"] = MarginsPt.Uniform(20) },
    };

    private static OverlayContext Context(int sheets = 1) =>
        new("doc.pdf", 1, sheets, 1, 1, "15.08.2026", "Принтер", "user");

    private readonly PrintLayoutEngine _engine = new();

    private IReadOnlyList<SheetPlan> Build(LayoutSettings settings, int pageCount = 1)
    {
        var pages = Enumerable.Range(0, pageCount)
            .Select(i => new SourcePage("doc", i, A4)).ToList();
        var sheets = _engine.BuildSheets(pages, settings, A4Paper, Caps());
        return _engine.ApplyMarksAndOverlays(sheets, settings, Context(sheets.Count));
    }

    [Fact]
    public void No_Marks_By_Default()
    {
        var sheet = Assert.Single(Build(new LayoutSettings()));
        Assert.Empty(sheet.Marks);
    }

    [Fact]
    public void Crop_Marks_Are_Outside_The_Content_And_Do_Not_Touch_It()
    {
        var settings = new LayoutSettings
        {
            Size = SizeMode.Fit,
            Orientation = OrientationMode.Portrait,
            Marks = new MarkSettings { Marks = PrinterMarks.CropMarks, LengthPt = 10, OffsetPt = 3 },
        };
        var sheet = Assert.Single(Build(settings));
        var crop = sheet.Marks.Where(m => m.Kind == "crop").ToList();

        // Восемь штрихов: по два на каждый угол.
        Assert.Equal(8, crop.Count);

        var content = sheet.Pages[0].TargetRectPt;
        foreach (var mark in crop)
        {
            var overlapsContent =
                mark.AreaPt.XPt > content.XPt + 0.01 && mark.AreaPt.RightPt < content.RightPt - 0.01 &&
                mark.AreaPt.YPt > content.YPt + 0.01 && mark.AreaPt.BottomPt < content.BottomPt - 0.01;
            Assert.False(overlapsContent, $"метка {mark.AreaPt} легла внутрь содержимого {content}");
        }
    }

    [Fact]
    public void Registration_Marks_Sit_On_The_Centres_Of_The_Sides()
    {
        var settings = new LayoutSettings
        {
            Size = SizeMode.Fit,
            Orientation = OrientationMode.Portrait,
            Marks = new MarkSettings { Marks = PrinterMarks.RegistrationMarks, LengthPt = 12, OffsetPt = 4 },
        };
        var sheet = Assert.Single(Build(settings));
        var marks = sheet.Marks.Where(m => m.Kind == "registration").ToList();
        Assert.Equal(4, marks.Count);

        var content = sheet.Pages[0].TargetRectPt;
        var centreX = content.XPt + content.WidthPt / 2;
        var centreY = content.YPt + content.HeightPt / 2;

        // Верхний и нижний кресты — по горизонтальному центру.
        Assert.Equal(2, marks.Count(m => Math.Abs(m.AreaPt.XPt + m.AreaPt.WidthPt / 2 - centreX) < 0.5));
        // Левый и правый — по вертикальному.
        Assert.Equal(2, marks.Count(m => Math.Abs(m.AreaPt.YPt + m.AreaPt.HeightPt / 2 - centreY) < 0.5));
    }

    [Fact]
    public void Bleed_Marks_Sit_Further_Out_Than_Crop_Marks()
    {
        var settings = new LayoutSettings
        {
            Size = SizeMode.Fit,
            Orientation = OrientationMode.Portrait,
            Marks = new MarkSettings
            {
                Marks = PrinterMarks.CropMarks | PrinterMarks.BleedMarks,
                BleedPt = 8, LengthPt = 10, OffsetPt = 3,
            },
        };
        var sheet = Assert.Single(Build(settings));

        var cropLeft = sheet.Marks.Where(m => m.Kind == "crop").Min(m => m.AreaPt.XPt);
        var bleedLeft = sheet.Marks.Where(m => m.Kind == "bleed").Min(m => m.AreaPt.XPt);
        Assert.True(bleedLeft < cropLeft,
            $"метка вылета {bleedLeft:F1} должна быть левее метки реза {cropLeft:F1}");
    }

    [Fact]
    public void Fold_Marks_Appear_Only_On_A_Booklet_Sheet()
    {
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.Booklet,
            Marks = new MarkSettings { Marks = PrinterMarks.FoldMarks },
        };
        var sheets = Build(settings, pageCount: 4);
        var folds = sheets[0].Marks.Where(m => m.Kind == "fold").ToList();
        Assert.Equal(2, folds.Count);

        // Сгиб проходит ровно между половинами листа.
        var betweenHalves = (sheets[0].Pages[0].TargetRectPt.RightPt + sheets[0].Pages[1].TargetRectPt.XPt) / 2;
        Assert.All(folds, f => Assert.Equal(betweenHalves, f.AreaPt.XPt, 1));
    }

    [Fact]
    public void Page_Information_Carries_The_Substituted_Text()
    {
        var settings = new LayoutSettings
        {
            Marks = new MarkSettings
            {
                Marks = PrinterMarks.PageInformation,
                PageInfoTemplate = "{file} лист {sheet} из {sheets}",
            },
        };
        var sheets = Build(settings, pageCount: 3);
        var info = Assert.Single(sheets[1].Marks.Where(m => m.Kind == "page-info"));
        Assert.Equal("doc.pdf лист 2 из 3", info.Text);
    }

    // ----- Печатные наложения -----

    [Fact]
    public void Overlay_Appears_On_Every_Sheet_By_Default()
    {
        var settings = new LayoutSettings
        {
            Overlays = new[] { new PrintOverlay { Template = "ЧЕРНОВИК" } },
        };
        var sheets = Build(settings, pageCount: 3);
        Assert.All(sheets, s => Assert.Single(s.Marks.Where(m => m.Kind == "overlay")));
    }

    [Theory]
    [InlineData(OverlayScope.FirstSheetOnly, new[] { true, false, false, false })]
    [InlineData(OverlayScope.LastSheetOnly, new[] { false, false, false, true })]
    [InlineData(OverlayScope.OddSheets, new[] { true, false, true, false })]
    [InlineData(OverlayScope.EvenSheets, new[] { false, true, false, true })]
    public void Overlay_Scope_Selects_The_Right_Sheets(OverlayScope scope, bool[] expected)
    {
        var overlay = new PrintOverlay { Template = "X", Scope = scope };
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], overlay.AppliesTo(i, expected.Length));
    }

    [Fact]
    public void Overlay_Stays_Inside_The_Printable_Area()
    {
        var settings = new LayoutSettings
        {
            Overlays = new[]
            {
                new PrintOverlay { Template = "низ", Position = OverlayPosition.BottomRight, MarginPt = 10 },
                new PrintOverlay { Template = "верх", Position = OverlayPosition.TopLeft, MarginPt = 10 },
            },
        };
        var sheet = Assert.Single(Build(settings));
        foreach (var mark in sheet.Marks.Where(m => m.Kind == "overlay"))
            Assert.True(mark.AreaPt.IsInside(sheet.PrintableAreaPt),
                $"наложение {mark.AreaPt} вышло за печатаемую область {sheet.PrintableAreaPt}");
    }

    [Fact]
    public void Template_Substitutes_Only_Known_Placeholders()
    {
        var context = new OverlayContext("файл.pdf", 2, 5, 3, 4, "15.08.2026", "HP", "yurch");
        var text = OverlayTemplate.Render(
            "{file} {sheet}/{sheets} копия {copy} из {copies} {date} {printer} {user} {unknown}", context);

        Assert.Equal("файл.pdf 2/5 копия 3 из 4 15.08.2026 HP yurch {unknown}", text);
    }

    [Fact]
    public void Empty_Template_Adds_Nothing()
    {
        var settings = new LayoutSettings
        {
            Overlays = new[] { new PrintOverlay { Template = "" } },
        };
        var sheet = Assert.Single(Build(settings));
        Assert.Empty(sheet.Marks.Where(m => m.Kind == "overlay"));
    }
}
