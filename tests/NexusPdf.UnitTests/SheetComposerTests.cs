using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Переход из пунктов в пиксели. Здесь доказывается главное обещание системы:
/// предпросмотр и вывод — это ОДИН расчёт при разном DPI, поэтому «на экране
/// одно, на бумаге другое» невозможно.
/// </summary>
public sealed class SheetComposerTests
{
    private static readonly SizePt A4 = new(595.28, 841.89);

    private static SheetPlan Sheet(params RectPt[] targets) => new()
    {
        SheetIndex = 0,
        PaperSizePt = A4,
        PrintableAreaPt = RectPt.FromSize(A4).Deflate(MarginsPt.Uniform(20)),
        HardMarginsPt = MarginsPt.Uniform(20),
        Pages = targets.Select((t, i) => new PlacedPage
        {
            DocumentId = "doc",
            SourcePageIndex = i,
            Box = PageBoxKind.CropBox,
            SourceRectPt = RectPt.FromSize(A4),
            TargetRectPt = t,
            ClipRectPt = t,
            Scale = 1.0,
            RotationDegrees = 0,
        }).ToList(),
    };

    [Fact]
    public void Sheet_Size_Follows_Dpi()
    {
        var at72 = SheetComposer.Compose(Sheet(), 72);
        Assert.Equal(595, at72.WidthPx);
        Assert.Equal(842, at72.HeightPx);

        var at300 = SheetComposer.Compose(Sheet(), 300);
        Assert.Equal(2480, at300.WidthPx);
        Assert.Equal(3508, at300.HeightPx);
    }

    [Fact]
    public void Preview_And_Output_Agree_Up_To_Scale()
    {
        // Одна и та же страница, посчитанная для экрана и для печати, обязана
        // занимать ту же ДОЛЮ листа — это и есть «один источник истины».
        var sheet = Sheet(new RectPt(100, 200, 300, 400));

        var preview = SheetComposer.Compose(sheet, 96);
        var output = SheetComposer.Compose(sheet, 600);

        var previewFraction = (double)preview.Pages[0].TargetPx.X / preview.WidthPx;
        var outputFraction = (double)output.Pages[0].TargetPx.X / output.WidthPx;
        Assert.Equal(previewFraction, outputFraction, 3);

        var previewWidthFraction = (double)preview.Pages[0].TargetPx.Width / preview.WidthPx;
        var outputWidthFraction = (double)output.Pages[0].TargetPx.Width / output.WidthPx;
        Assert.Equal(previewWidthFraction, outputWidthFraction, 3);
    }

    [Fact]
    public void Printable_Area_Is_Inset_By_Hard_Margins()
    {
        var composed = SheetComposer.Compose(Sheet(), 72);
        Assert.Equal(20, composed.PrintableAreaPx.X);
        Assert.Equal(20, composed.PrintableAreaPx.Y);
        Assert.InRange(composed.PrintableAreaPx.Width, 595 - 41, 595 - 39);
    }

    [Fact]
    public void Huge_Sheet_Lowers_Dpi_Instead_Of_Cropping()
    {
        // A0 при 1200 dpi — сорок тысяч пикселей по стороне. Обрезать лист
        // нельзя: напечаталась бы половина, и заметить это можно было бы
        // только на бумаге.
        var a0 = new SizePt(2384, 3370);
        var sheet = new SheetPlan
        {
            SheetIndex = 0,
            PaperSizePt = a0,
            PrintableAreaPt = RectPt.FromSize(a0),
            HardMarginsPt = MarginsPt.Zero,
            Pages = Array.Empty<PlacedPage>(),
        };

        var composed = SheetComposer.Compose(sheet, 1200);

        Assert.True(composed.WidthPx <= SheetComposer.MaxRenderSidePx);
        Assert.True(composed.HeightPx <= SheetComposer.MaxRenderSidePx);
        Assert.True(composed.Dpi < 1200, "фактический DPI обязан быть снижен и сообщён");

        // Пропорции листа при этом обязаны сохраниться.
        Assert.Equal(a0.WidthPt / a0.HeightPt, (double)composed.WidthPx / composed.HeightPx, 2);
    }

    [Fact]
    public void Render_Size_Matches_The_Place_On_The_Sheet()
    {
        var sheet = Sheet(new RectPt(0, 0, 297.64, 420.94)); // половина A4
        var composed = SheetComposer.Compose(sheet, 300);

        var page = composed.Pages[0];
        Assert.Equal(page.TargetPx.Width, page.RenderWidthPx);
        Assert.Equal(page.TargetPx.Height, page.RenderHeightPx);
        // Половина листа при 300 dpi — это 1240 px, а не размер исходной страницы.
        Assert.InRange(page.RenderWidthPx, 1238, 1242);
    }

    [Fact]
    public void Preview_Dpi_Fits_The_Sheet_Into_Available_Space()
    {
        var dpi = SheetComposer.DpiForPreview(A4, 400, 560);
        var composed = SheetComposer.Compose(new SheetPlan
        {
            SheetIndex = 0,
            PaperSizePt = A4,
            PrintableAreaPt = RectPt.FromSize(A4),
            HardMarginsPt = MarginsPt.Zero,
            Pages = Array.Empty<PlacedPage>(),
        }, dpi);

        Assert.True(composed.WidthPx <= 401, $"ширина {composed.WidthPx} не влезает в 400");
        Assert.True(composed.HeightPx <= 561, $"высота {composed.HeightPx} не влезает в 560");
    }
}
