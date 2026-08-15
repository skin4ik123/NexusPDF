using NexusPdf.Pdf.Abstractions;
using NexusPdf.Printing;

namespace NexusPdf.Application;

/// <summary>
/// Рисует лист плана печати. Один и тот же метод вызывают предпросмотр (низкий
/// DPI) и вывод на бумагу или в файл (высокий): «в окне одно, на бумаге другое»
/// исключено не договорённостью, а тем, что кода для второго варианта нет.
///
/// Растр здесь не выбор, а следствие движка: PDFium не отдаёт векторные команды
/// наружу, поэтому любое размещение страницы на листе — это изображение.
/// Записано честно в PRINT_KNOWN_LIMITATIONS.
/// </summary>
public sealed class PrintPlanRenderer
{
    private readonly OpenedDocument _document;

    public PrintPlanRenderer(OpenedDocument document) => _document = document;

    /// <summary>Отрисованный лист: BGRA-буфер размером с физический лист.</summary>
    public async Task<RenderedPageImage> RenderSheetAsync(
        ComposedSheet composed, CancellationToken ct)
    {
        var stride = composed.WidthPx * 4;
        var buffer = new byte[stride * composed.HeightPx];
        // Бумага белая: без заливки лист был бы прозрачным, и на печати
        // получился бы чёрный фон в тех местах, где нет содержимого.
        Array.Fill(buffer, (byte)0xFF);

        foreach (var page in composed.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (page.ClipPx.IsEmpty) continue;

            var image = await RenderPlacedPageAsync(page, ct).ConfigureAwait(false);
            if (image == null) continue;

            Blit(image, page, buffer, stride, composed.WidthPx, composed.HeightPx);
        }

        return new RenderedPageImage(composed.WidthPx, composed.HeightPx, stride, buffer);
    }

    /// <summary>
    /// Растр одной размещённой страницы. Для плаката берётся кусок страницы,
    /// поэтому рендерится вся страница в увеличенном масштабе, а нужный кусок
    /// вырезается: сдвига «отрендерить сразу область» у движка нет.
    /// </summary>
    private async Task<RenderedPageImage?> RenderPlacedPageAsync(ComposedPagePx page, CancellationToken ct)
    {
        var placed = page.Source;
        var logicalIndex = placed.SourcePageIndex;
        if (logicalIndex < 0 || logicalIndex >= _document.Session.Model.Pages.Count)
            return null;

        var full = _document.GetLogicalPageSize(logicalIndex);
        var src = placed.SourceRectPt;

        var takesWholePage =
            src.XPt <= 0.01 && src.YPt <= 0.01 &&
            src.WidthPt >= full.WidthPoints - 0.01 &&
            src.HeightPt >= full.HeightPoints - 0.01;

        if (takesWholePage)
        {
            return await RenderWholeAsync(logicalIndex, placed,
                page.RenderWidthPx, page.RenderHeightPx, ct).ConfigureAwait(false);
        }

        // Кусок страницы: масштаб тот же, что у целого листа, поэтому полный
        // растр во столько же раз больше, во сколько страница больше куска.
        var scaleX = page.RenderWidthPx / Math.Max(1.0, src.WidthPt);
        var scaleY = page.RenderHeightPx / Math.Max(1.0, src.HeightPt);
        var fullWidthPx = (int)Math.Round(full.WidthPoints * scaleX);
        var fullHeightPx = (int)Math.Round(full.HeightPoints * scaleY);

        // Предохранитель от гигантских промежуточных растров у плаката.
        const int MaxIntermediateSide = 12000;
        if (fullWidthPx > MaxIntermediateSide || fullHeightPx > MaxIntermediateSide)
        {
            var reduce = (double)MaxIntermediateSide / Math.Max(fullWidthPx, fullHeightPx);
            fullWidthPx = (int)Math.Round(fullWidthPx * reduce);
            fullHeightPx = (int)Math.Round(fullHeightPx * reduce);
            scaleX *= reduce;
            scaleY *= reduce;
        }

        var whole = await RenderWholeAsync(logicalIndex, placed,
            Math.Max(1, fullWidthPx), Math.Max(1, fullHeightPx), ct).ConfigureAwait(false);
        if (whole == null) return null;

        return Crop(whole,
            (int)Math.Round(src.XPt * scaleX),
            (int)Math.Round(src.YPt * scaleY),
            page.RenderWidthPx, page.RenderHeightPx);
    }

    private Task<RenderedPageImage> RenderWholeAsync(
        int logicalIndex, PlacedPage placed, int widthPx, int heightPx, CancellationToken ct)
    {
        // Политика аннотаций решает, каким рендером брать страницу: обычный
        // включает аннотации и поля форм, content-only не включает ничего.
        // Флаг Print у отдельных аннотаций соблюдается движком при отрисовке.
        return placed.Annotations == AnnotationPolicy.DocumentOnly ||
               placed.Forms == FormPolicy.WithoutFields
            ? _document.RenderLogicalPageContentOnlyAsync(logicalIndex, widthPx, heightPx, ct)
            : _document.RenderLogicalPageAsync(logicalIndex, widthPx, heightPx, ct);
    }

    private static RenderedPageImage Crop(RenderedPageImage source, int x, int y, int width, int height)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        Array.Fill(buffer, (byte)0xFF);

        for (var row = 0; row < height; row++)
        {
            var sourceRow = y + row;
            if (sourceRow < 0 || sourceRow >= source.PixelHeight) continue;

            var copyX = Math.Max(0, x);
            var copyWidth = Math.Min(width - (copyX - x), source.PixelWidth - copyX);
            if (copyWidth <= 0) continue;

            Buffer.BlockCopy(
                source.Bgra, sourceRow * source.Stride + copyX * 4,
                buffer, row * stride + (copyX - x) * 4,
                copyWidth * 4);
        }
        return new RenderedPageImage(width, height, stride, buffer);
    }

    /// <summary>
    /// Переносит растр страницы на лист с обрезкой по ClipPx. Обрезка делается
    /// здесь, а не рендером меньшего размера: иначе обрезанная страница
    /// печаталась бы сжатой, а не подрезанной, — а это разные вещи.
    /// </summary>
    private static void Blit(
        RenderedPageImage image, ComposedPagePx page,
        byte[] sheet, int sheetStride, int sheetWidth, int sheetHeight)
    {
        var clip = page.ClipPx;
        var target = page.TargetPx;

        var left = Math.Max(0, Math.Max(clip.X, target.X));
        var top = Math.Max(0, Math.Max(clip.Y, target.Y));
        var right = Math.Min(sheetWidth, Math.Min(clip.Right, target.Right));
        var bottom = Math.Min(sheetHeight, Math.Min(clip.Bottom, target.Bottom));
        if (right <= left || bottom <= top) return;

        for (var y = top; y < bottom; y++)
        {
            var sourceY = y - target.Y;
            if (sourceY < 0 || sourceY >= image.PixelHeight) continue;

            var sourceX = left - target.X;
            if (sourceX < 0) continue;
            var width = Math.Min(right - left, image.PixelWidth - sourceX);
            if (width <= 0) continue;

            Buffer.BlockCopy(
                image.Bgra, sourceY * image.Stride + sourceX * 4,
                sheet, y * sheetStride + left * 4,
                width * 4);
        }
    }
}
