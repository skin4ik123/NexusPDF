using NexusPdf.Application;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Слои документа целиком: чтение списка из структуры PDF и переключение
/// видимости с проверкой, что выключенный слой ДЕЙСТВИТЕЛЬНО перестаёт
/// рисоваться, а включённый возвращается.
/// </summary>
public sealed class LayerServiceTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;
    private LayerService _layers = null!;
    private QpdfEngine _qpdf = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        _qpdf = new QpdfEngine();
        _layers = new LayerService(_qpdf);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static double DarkFraction(
        NexusPdf.Pdf.Abstractions.RenderedPageImage image, int rowFrom, int rowTo)
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

    private async Task<(double One, double Two)> InkAsync(string path)
    {
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var image = await doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
        return (DarkFraction(image, 66, 94), DarkFraction(image, 166, 194));
    }

    [Fact]
    public async Task Layers_Are_Listed_With_Names_And_Current_Visibility()
    {
        Assert.True(_qpdf.IsAvailable, "тест требует поставляемого qpdf");
        var path = PdfFixture.WriteLayersToTemp("layers.pdf", offLayer: 2);

        var layers = await _layers.GetLayersAsync(path, null, CancellationToken.None);

        Assert.Equal(2, layers.Count);
        Assert.Equal("Layer one", layers[0].Name);
        Assert.True(layers[0].IsVisible);
        Assert.Equal("Layer two", layers[1].Name);
        Assert.False(layers[1].IsVisible); // выключен в /OCProperties /D /OFF
    }

    [Fact]
    public async Task Turning_A_Layer_Off_Actually_Hides_It_And_Back_On_Restores_It()
    {
        Assert.True(_qpdf.IsAvailable, "тест требует поставляемого qpdf");
        var path = PdfFixture.WriteLayersToTemp("layers.pdf");
        var dir = Path.GetDirectoryName(path)!;

        var before = await InkAsync(path);
        Assert.True(before.One > 0.005 && before.Two > 0.005, "сначала видны оба слоя");

        var layers = await _layers.GetLayersAsync(path, null, CancellationToken.None);
        var hidden = Path.Combine(dir, "hidden.pdf");
        await _layers.SetLayerVisibilityAsync(path, null,
            new Dictionary<string, bool> { [layers[1].Reference] = false },
            hidden, CancellationToken.None);

        var afterHide = await InkAsync(hidden);
        Assert.True(afterHide.One > 0.005, "первый слой должен остаться");
        Assert.True(afterHide.Two < 0.0005,
            $"второй слой должен исчезнуть, а тёмных пикселей {afterHide.Two:P2}");

        // Состояние читается обратно из файла.
        var reread = await _layers.GetLayersAsync(hidden, null, CancellationToken.None);
        Assert.True(reread[0].IsVisible);
        Assert.False(reread[1].IsVisible);

        // И слой возвращается: содержимое страницы не удалялось.
        var restored = Path.Combine(dir, "restored.pdf");
        await _layers.SetLayerVisibilityAsync(hidden, null,
            new Dictionary<string, bool> { [reread[1].Reference] = true },
            restored, CancellationToken.None);

        var afterRestore = await InkAsync(restored);
        Assert.True(afterRestore.Two > 0.005,
            $"включённый обратно слой должен снова рисоваться, а тёмных {afterRestore.Two:P2}");
    }

    [Fact]
    public async Task Document_Without_Layers_Reports_None()
    {
        Assert.True(_qpdf.IsAvailable, "тест требует поставляемого qpdf");
        var path = PdfFixture.WriteToTemp("plain.pdf", new PdfFixture.PageSpec(300, 300));

        Assert.Empty(await _layers.GetLayersAsync(path, null, CancellationToken.None));
    }
}
