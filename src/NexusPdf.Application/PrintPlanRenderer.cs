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
    /// <param name="drawGuides">
    /// Нарисовать границу печатаемой области и отметить обрезанное содержимое.
    /// Только для предпросмотра: на бумагу и в файл направляющие не идут.
    /// </param>
    public async Task<RenderedPageImage> RenderSheetAsync(
        ComposedSheet composed, CancellationToken ct, bool drawGuides = false)
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

        // Метки рисуются ПОСЛЕ содержимого: линия реза, попавшая под страницу,
        // бесполезна — по ней нечего резать.
        DrawMarks(composed, buffer, stride);

        // Цветовой режим — предпоследним: он обязан захватить и содержимое, и
        // метки, иначе на сером листе метки остались бы цветными.
        ColorConversion.Apply(buffer, composed.Sheet.Color, composed.WidthPx);

        // Направляющие — последними и в цвете: это разметка предпросмотра, а не
        // содержимое листа, и обесцвечивать её вместе с ним нельзя — иначе на
        // сером листе она сольётся с текстом.
        if (drawGuides)
            DrawGuides(composed, buffer, stride);

        return new RenderedPageImage(composed.WidthPx, composed.HeightPx, stride, buffer);
    }

    /// <summary>
    /// Типографские метки и печатные наложения листа. Текстовые метки
    /// (подпись листа, наложение) рисуются простым растровым шрифтом: тянуть
    /// сюда полноценную типографику ради строки в углу листа незачем.
    /// </summary>
    private static void DrawMarks(ComposedSheet composed, byte[] buffer, int stride)
    {
        if (composed.Sheet.Marks.Count == 0) return;

        var scale = composed.Dpi / 72.0;
        foreach (var mark in composed.Sheet.Marks)
        {
            var x = (int)Math.Round(mark.AreaPt.XPt * scale);
            var y = (int)Math.Round(mark.AreaPt.YPt * scale);
            var w = (int)Math.Round(mark.AreaPt.WidthPt * scale);
            var h = (int)Math.Round(mark.AreaPt.HeightPt * scale);

            switch (mark.Kind)
            {
                case "crop":
                case "trim":
                case "bleed":
                case "fold":
                case "cut":
                    // Штрих: нулевая ширина или высота означает линию.
                    DrawLine(buffer, stride, composed.WidthPx, composed.HeightPx,
                        x, y, x + Math.Max(w, 0), y + Math.Max(h, 0), 0, 0, 0);
                    break;

                case "registration":
                    DrawLine(buffer, stride, composed.WidthPx, composed.HeightPx,
                        x, y + h / 2, x + w, y + h / 2, 0, 0, 0);
                    DrawLine(buffer, stride, composed.WidthPx, composed.HeightPx,
                        x + w / 2, y, x + w / 2, y + h, 0, 0, 0);
                    break;

                case "page-info":
                case "overlay":
                case "tile-label":
                    if (!string.IsNullOrEmpty(mark.Text))
                        TinyFont.Draw(buffer, stride, composed.WidthPx, composed.HeightPx,
                            mark.Text!, x, y, Math.Max(1, (int)Math.Round(h / 7.0)));
                    break;
            }
        }
    }

    private static void DrawLine(
        byte[] buffer, int stride, int width, int height,
        int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var steps = Math.Max(dx, dy);
        if (steps == 0) steps = 1;

        for (var i = 0; i <= steps; i++)
        {
            var x = x0 + (x1 - x0) * i / steps;
            var y = y0 + (y1 - y0) * i / steps;
            if (x < 0 || y < 0 || x >= width || y >= height) continue;
            var offset = y * stride + x * 4;
            buffer[offset] = b;
            buffer[offset + 1] = g;
            buffer[offset + 2] = r;
        }
    }

    /// <summary>
    /// Направляющие предпросмотра: серая рамка печатаемой области и красная —
    /// вокруг обрезанного содержимого. Без них пользователь узнаёт про поля
    /// принтера уже по испорченному листу.
    /// </summary>
    private static void DrawGuides(ComposedSheet composed, byte[] buffer, int stride)
    {
        DrawDashedRect(buffer, stride, composed.WidthPx, composed.HeightPx,
            composed.PrintableAreaPx, b: 190, g: 190, r: 190, dash: 6);

        foreach (var page in composed.Pages)
        {
            if (!page.Source.IsClipped) continue;
            DrawDashedRect(buffer, stride, composed.WidthPx, composed.HeightPx,
                page.ClipPx, b: 38, g: 38, r: 220, dash: 4);
        }
    }

    private static void DrawDashedRect(
        byte[] buffer, int stride, int width, int height,
        RectPx rect, byte b, byte g, byte r, int dash)
    {
        if (rect.IsEmpty) return;

        void Dot(int x, int y, int step)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            if (step % (dash * 2) >= dash) return; // пропуск — из этого и получается пунктир
            var offset = y * stride + x * 4;
            buffer[offset] = b;
            buffer[offset + 1] = g;
            buffer[offset + 2] = r;
        }

        var right = Math.Min(rect.Right, width) - 1;
        var bottom = Math.Min(rect.Bottom, height) - 1;
        for (var x = Math.Max(0, rect.X); x <= right; x++)
        {
            Dot(x, rect.Y, x);
            Dot(x, bottom, x);
        }
        for (var y = Math.Max(0, rect.Y); y <= bottom; y++)
        {
            Dot(rect.X, y, y);
            Dot(right, y, y);
        }
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
        return _document.RenderLogicalPageForPrintAsync(
            logicalIndex, widthPx, heightPx, ContentOptionsOf(placed), ct);
    }

    /// <summary>
    /// Политики плана → состав растра.
    ///
    /// Аннотации и поля решаются РАЗДЕЛЬНО: поле формы — это тоже аннотация, но
    /// «печатать без полей» не должно заодно убирать комментарии. Раньше обе
    /// политики сводились к одному content-only рендеру, и снятая галочка полей
    /// молча уносила с листа всю разметку.
    /// </summary>
    internal static PrintContentOptions ContentOptionsOf(PlacedPage placed)
    {
        if (placed.Annotations == AnnotationPolicy.DocumentOnly)
            return PrintContentOptions.DocumentOnly;

        return new PrintContentOptions(
            IncludeAnnotations: true,
            // «Все видимые» — это осознанный выбор напечатать и то, что автор
            // пометил как экранное; по умолчанию соблюдается флаг Print.
            OnlyPrintableAnnotations: placed.Annotations != AnnotationPolicy.AllVisibleAnnotations,
            IncludeFormFields: placed.Forms == FormPolicy.WithValues);
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
