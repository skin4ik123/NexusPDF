using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Наложение прозрачного слоя правок на готовый растр страницы.
///
/// Слой рисуется движком отдельно (пустая страница той же геометрии с одними
/// правками), поэтому добавленная заметка или измерение не требует заново
/// рисовать содержимое страницы — на тяжёлом чертеже это и была та самая
/// «перерисовка всего» после каждого щелчка.
/// </summary>
public static class OverlayCompositor
{
    /// <summary>
    /// Кладёт слой (BGRA, прозрачность не умножена на цвет) поверх
    /// непрозрачной страницы того же размера. Страница не меняется — результат
    /// новым буфером: исходный растр может лежать в кэше.
    /// </summary>
    public static RenderedPageImage BlendOver(RenderedPageImage page, RenderedPageImage layer)
    {
        if (page.PixelWidth != layer.PixelWidth || page.PixelHeight != layer.PixelHeight)
            throw new ArgumentException("Layer size differs from the page size.", nameof(layer));

        var result = (byte[])page.Bgra.Clone();
        var width = page.PixelWidth;
        for (var y = 0; y < page.PixelHeight; y++)
        {
            var target = y * page.Stride;
            var source = y * layer.Stride;
            for (var x = 0; x < width; x++, target += 4, source += 4)
            {
                int alpha = layer.Bgra[source + 3];
                if (alpha == 0) continue;
                if (alpha == 255)
                {
                    result[target] = layer.Bgra[source];
                    result[target + 1] = layer.Bgra[source + 1];
                    result[target + 2] = layer.Bgra[source + 2];
                    continue;
                }
                var inverse = 255 - alpha;
                result[target] = (byte)((layer.Bgra[source] * alpha + result[target] * inverse + 127) / 255);
                result[target + 1] = (byte)((layer.Bgra[source + 1] * alpha + result[target + 1] * inverse + 127) / 255);
                result[target + 2] = (byte)((layer.Bgra[source + 2] * alpha + result[target + 2] * inverse + 127) / 255);
            }
        }
        return new RenderedPageImage(page.PixelWidth, page.PixelHeight, page.Stride, result);
    }
}
