using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Слои документа (OCG). У pdfium нет публичного API для слоёв, поэтому
/// сначала измеряется главное: СЧИТАЕТСЯ ли движок с конфигурацией слоёв по
/// умолчанию при отрисовке. От ответа зависит, можно ли вообще дать
/// пользователю переключение слоёв.
/// </summary>
public sealed class LayerProbeTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    /// <summary>Доля тёмных пикселей в горизонтальной полосе растра.</summary>
    private static double DarkFraction(RenderedPageImage image, int rowFrom, int rowTo)
    {
        var dark = 0;
        var total = 0;
        for (var y = Math.Max(0, rowFrom); y < Math.Min(image.PixelHeight, rowTo); y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var o = y * image.Stride + x * 4;
                total++;
                if (image.Bgra[o] < 128 && image.Bgra[o + 1] < 128 && image.Bgra[o + 2] < 128)
                    dark++;
            }
        }
        return total == 0 ? 0 : (double)dark / total;
    }

    // Текст первого слоя стоит на 700 пт снизу => строка растра ~68..92,
    // второго на 600 пт => ~168..192 (страница 792 пт, растр 1:1).
    private static double LayerOneInk(RenderedPageImage image) => DarkFraction(image, 66, 94);
    private static double LayerTwoInk(RenderedPageImage image) => DarkFraction(image, 166, 194);

    [Fact]
    public async Task Both_Layers_Visible_When_Nothing_Is_Turned_Off()
    {
        var path = PdfFixture.WriteLayersToTemp("layers-on.pdf");
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var image = await doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);

        Assert.True(LayerOneInk(image) > 0.005, $"первый слой должен быть виден: {LayerOneInk(image):P2}");
        Assert.True(LayerTwoInk(image) > 0.005, $"второй слой должен быть виден: {LayerTwoInk(image):P2}");
    }

    [Fact]
    public async Task Layer_Turned_Off_In_The_Default_Config_Is_Not_Drawn()
    {
        var path = PdfFixture.WriteLayersToTemp("layers-off2.pdf", offLayer: 2);
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var image = await doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);

        // Первый слой на месте, второй выключен в /OCProperties /D /OFF.
        Assert.True(LayerOneInk(image) > 0.005,
            $"первый слой должен остаться видимым: {LayerOneInk(image):P2}");
        Assert.True(LayerTwoInk(image) < 0.0005,
            $"выключенный слой рисоваться не должен, а тёмных пикселей {LayerTwoInk(image):P2}");
    }
}
