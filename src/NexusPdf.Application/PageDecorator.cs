using System.Globalization;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

public enum DecorPosition
{
    Top,
    Bottom,
}

public enum DecorAlignment
{
    Left,
    Center,
    Right,
}

public sealed record HeaderFooterOptions(
    string Template,
    DecorPosition Position,
    DecorAlignment Alignment,
    double FontSizePt,
    IReadOnlyList<int> PageIndices,
    bool SkipFirstPage);

public sealed record WatermarkOptions(
    string Text,
    double FontSizePt,
    double Opacity,          // 0..1
    bool Diagonal,
    IReadOnlyList<int> PageIndices);

/// <summary>
/// Построение пакетов текстовых оверлеев: колонтитулы с подстановками
/// ({n} — номер, {N} — всего, {file} — имя файла, {date} — дата) и водяные знаки.
/// Ширина строки оценивается приближённо (метрики Segoe UI); точность
/// достаточна для выравнивания по краям и центру.
/// </summary>
public static class PageDecorator
{
    private const double EdgeMarginPt = 36;
    private const double VerticalMarginPt = 18;
    private const double ApproxGlyphWidthFactor = 0.52;

    public static string ExpandTemplate(string template, int pageNumber, int pageCount, string? fileName, DateTime date) =>
        template
            .Replace("{n}", pageNumber.ToString(CultureInfo.InvariantCulture))
            .Replace("{N}", pageCount.ToString(CultureInfo.InvariantCulture))
            .Replace("{file}", fileName ?? "")
            .Replace("{date}", date.ToString("d", CultureInfo.CurrentCulture));

    public static double ApproximateWidthPt(string text, double fontSizePt) =>
        text.Length * fontSizePt * ApproxGlyphWidthFactor;

    public static AddOverlaysOperation BuildHeaderFooter(OpenedDocument document, HeaderFooterOptions options)
    {
        var pageCount = document.Session.Model.Pages.Count;
        var fileName = document.Session.FilePath is { } p ? Path.GetFileName(p) : null;
        var now = DateTime.Now;

        var items = new List<(int, PageOverlay)>();
        foreach (var index in options.PageIndices)
        {
            if (options.SkipFirstPage && index == 0)
                continue;

            var size = document.GetLogicalPageSize(index);
            var text = ExpandTemplate(options.Template, index + 1, pageCount, fileName, now);
            if (text.Length == 0)
                continue;

            var width = ApproximateWidthPt(text, options.FontSizePt);
            var x = options.Alignment switch
            {
                DecorAlignment.Left => EdgeMarginPt,
                DecorAlignment.Right => Math.Max(EdgeMarginPt, size.WidthPoints - EdgeMarginPt - width),
                _ => Math.Max(EdgeMarginPt, (size.WidthPoints - width) / 2),
            };
            var y = options.Position == DecorPosition.Top
                ? VerticalMarginPt
                : size.HeightPoints - VerticalMarginPt - options.FontSizePt;

            items.Add((index, new TextOverlay(text, x, y, options.FontSizePt, 0xFF3A3A3A, 0)));
        }
        return new AddOverlaysOperation(items, "Колонтитулы");
    }

    public static AddOverlaysOperation BuildWatermark(OpenedDocument document, WatermarkOptions options)
    {
        var alpha = (byte)Math.Clamp((int)Math.Round(options.Opacity * 255), 10, 255);
        var color = (uint)(alpha << 24 | 0x808080);

        var items = new List<(int, PageOverlay)>();
        foreach (var index in options.PageIndices)
        {
            var size = document.GetLogicalPageSize(index);
            var angleDeg = options.Diagonal ? 45.0 : 0.0;
            var angle = angleDeg * Math.PI / 180.0;
            var width = ApproximateWidthPt(options.Text, options.FontSizePt);

            // Начало базовой линии смещается от центра страницы на полширины
            // строки вдоль направления текста (в отображаемых координатах y — вниз).
            var x = size.WidthPoints / 2 - Math.Cos(angle) * width / 2;
            var y = size.HeightPoints / 2 + Math.Sin(angle) * width / 2 - options.FontSizePt / 2;

            items.Add((index, new TextOverlay(options.Text, x, y, options.FontSizePt, color, angleDeg)));
        }
        return new AddOverlaysOperation(items, "Водяной знак");
    }
}
