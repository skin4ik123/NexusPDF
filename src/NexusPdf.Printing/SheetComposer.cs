namespace NexusPdf.Printing;

/// <summary>Прямоугольник в пикселях растра листа.</summary>
public readonly record struct RectPx(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"[{X};{Y} {Width}×{Height} px]";
}

/// <summary>Готовое к отрисовке размещение одной страницы на растре листа.</summary>
public sealed record ComposedPagePx(
    PlacedPage Source,
    RectPx TargetPx,
    RectPx ClipPx,
    int RenderWidthPx,
    int RenderHeightPx);

/// <summary>Растр листа целиком: размер и что на нём где лежит.</summary>
public sealed record ComposedSheet(
    SheetPlan Sheet,
    int WidthPx,
    int HeightPx,
    double Dpi,
    RectPx PrintableAreaPx,
    IReadOnlyList<ComposedPagePx> Pages);

/// <summary>
/// Переводит лист плана из пунктов в пиксели. Это единственное место, где
/// происходит переход к растру: и предпросмотр на экране, и вывод на бумагу
/// или в файл вызывают его с разным DPI и получают геометрически одинаковый
/// результат. Отдельной «упрощённой» математики для предпросмотра нет.
/// </summary>
public static class SheetComposer
{
    /// <summary>
    /// Максимальная сторона растра. Гигантские листы (A0 при 600 dpi — это
    /// 20000 px) иначе съедают память целиком; ограничение честно понижает
    /// фактический DPI и сообщает об этом через <see cref="ComposedSheet.Dpi"/>.
    /// </summary>
    public const int MaxRenderSidePx = 10000;

    public static ComposedSheet Compose(SheetPlan sheet, double dpi)
    {
        var requested = Math.Max(1, dpi);
        var scale = requested / Units.PointsPerInch;

        var widthPx = (int)Math.Round(sheet.PaperSizePt.WidthPt * scale);
        var heightPx = (int)Math.Round(sheet.PaperSizePt.HeightPt * scale);

        // Понижаем DPI, а не обрезаем лист: обрезанный растр напечатал бы
        // половину страницы, и заметить это можно было бы только на бумаге.
        var longest = Math.Max(widthPx, heightPx);
        if (longest > MaxRenderSidePx)
        {
            var reduction = (double)MaxRenderSidePx / longest;
            requested *= reduction;
            scale = requested / Units.PointsPerInch;
            widthPx = (int)Math.Round(sheet.PaperSizePt.WidthPt * scale);
            heightPx = (int)Math.Round(sheet.PaperSizePt.HeightPt * scale);
        }

        widthPx = Math.Max(1, widthPx);
        heightPx = Math.Max(1, heightPx);

        var pages = new List<ComposedPagePx>(sheet.Pages.Count);
        foreach (var page in sheet.Pages)
        {
            var target = ToPixels(page.TargetRectPt, scale);
            var clip = ToPixels(page.ClipRectPt, scale);

            // Растр исходной страницы делается ровно под её место на листе:
            // рендерить больше — впустую тратить память, меньше — размыть.
            var renderWidth = Math.Max(1, target.Width);
            var renderHeight = Math.Max(1, target.Height);

            pages.Add(new ComposedPagePx(page, target, clip, renderWidth, renderHeight));
        }

        return new ComposedSheet(
            sheet, widthPx, heightPx, requested,
            ToPixels(sheet.PrintableAreaPt, scale),
            pages);
    }

    private static RectPx ToPixels(RectPt rect, double scale) => new(
        (int)Math.Round(rect.XPt * scale),
        (int)Math.Round(rect.YPt * scale),
        (int)Math.Round(rect.WidthPt * scale),
        (int)Math.Round(rect.HeightPt * scale));

    /// <summary>
    /// DPI, при котором лист поместится в отведённое на экране место.
    /// Предпросмотр не должен рендерить A3 в 300 dpi ради картинки шириной
    /// 400 пикселей.
    /// </summary>
    public static double DpiForPreview(SizePt paperPt, double availableWidthPx, double availableHeightPx)
    {
        if (paperPt.WidthPt <= 0 || paperPt.HeightPt <= 0) return 96;
        var byWidth = availableWidthPx / paperPt.WidthPt * Units.PointsPerInch;
        var byHeight = availableHeightPx / paperPt.HeightPt * Units.PointsPerInch;
        return Math.Clamp(Math.Min(byWidth, byHeight), 24, 300);
    }
}
