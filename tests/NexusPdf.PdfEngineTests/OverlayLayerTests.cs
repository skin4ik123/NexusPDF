using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Пометки поверх страницы рисуются отдельным прозрачным слоем над обычным
/// растром. Проверяется главное: картинка совпадает с прежней полной
/// перерисовкой страницы (то есть с тем, что окажется в файле), и вымарка до
/// сохранения наконец видна.
/// </summary>
public sealed class OverlayLayerTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string TempPdf(params PdfFixture.PageSpec[] pages)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(pages));
        return path;
    }

    private static IReadOnlyList<PageOverlay> Marks(int placedRotation) => new PageOverlay[]
    {
        new MeasurementDraft(MeasurementKind.Distance,
            new[] { new InkPoint(80, 120), new InkPoint(300, 120) },
            200, MeasureUnit.Meter, 2, "12,34 м", 0xFFDC2626, "Тест") { PlacedRotation = placedRotation },
        new ShapeAnnotationDraft(60, 200, 180, 90, 0xFF2563EB, 0x332563EB, 2, false, "", "Тест")
            { PlacedRotation = placedRotation },
        new InkAnnotationDraft(new[] { (IReadOnlyList<InkPoint>)new[] { new InkPoint(50, 400), new InkPoint(250, 450) } },
            0xFF16A34A, 3, "", "Тест") { PlacedRotation = placedRotation },
    };

    /// <summary>Доля пикселей, отличающихся больше чем на порог хотя бы в одном канале.</summary>
    private static double DifferentFraction(RenderedPageImage a, RenderedPageImage b, int threshold = 48)
    {
        Assert.Equal(a.PixelWidth, b.PixelWidth);
        Assert.Equal(a.PixelHeight, b.PixelHeight);
        var different = 0;
        for (var y = 0; y < a.PixelHeight; y++)
        {
            for (var x = 0; x < a.PixelWidth; x++)
            {
                var oa = y * a.Stride + x * 4;
                var ob = y * b.Stride + x * 4;
                if (Math.Abs(a.Bgra[oa] - b.Bgra[ob]) > threshold ||
                    Math.Abs(a.Bgra[oa + 1] - b.Bgra[ob + 1]) > threshold ||
                    Math.Abs(a.Bgra[oa + 2] - b.Bgra[ob + 2]) > threshold)
                    different++;
            }
        }
        return (double)different / (a.PixelWidth * a.PixelHeight);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 0)]
    [InlineData(0, 1)]
    [InlineData(90, 3)]
    public async Task Layer_Over_The_Page_Looks_Like_The_Full_Redraw(int pageRotate, int extraTurns)
    {
        var path = TempPdf(new PdfFixture.PageSpec(612, 792, Rotate: pageRotate, Text: "KEEP THIS TEXT"));
        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var size = handle.Info.Pages[0];
        var displayedWidth = extraTurns % 2 == 0 ? size.WidthPoints : size.HeightPoints;
        var displayedHeight = extraTurns % 2 == 0 ? size.HeightPoints : size.WidthPoints;
        var width = (int)Math.Round(displayedWidth);
        var height = (int)Math.Round(displayedHeight);
        var marks = Marks(extraTurns);

        var full = await _pdfium.RenderPageWithOverlaysAsync(handle, 0, extraTurns, marks, width, height, CancellationToken.None);
        var content = await handle.RenderPageAsync(0, width, height, extraTurns, CancellationToken.None);
        var layer = await _pdfium.RenderOverlayLayerAsync(handle, 0, extraTurns, marks, width, height, CancellationToken.None);
        var composed = OverlayCompositor.BlendOver(content, layer);

        var differs = DifferentFraction(full, composed);
        Assert.True(differs < 0.002, $"слой поверх страницы отличается от полной перерисовки на {differs:P2} пикселей");

        // И слой действительно что-то нарисовал (иначе сравнение ничего не стоит).
        var layerPixels = 0;
        for (var i = 3; i < layer.Bgra.Length; i += 4)
            if (layer.Bgra[i] > 0) layerPixels++;
        Assert.True(layerPixels > 500, $"в слое правок почти пусто: {layerPixels} пикселей");
    }

    [Fact]
    public async Task Pending_Redaction_Is_Shown_As_A_Black_Box()
    {
        // До сохранения вымарка не была видна вовсе — человек не видел, что
        // именно будет уничтожено.
        var path = TempPdf(new PdfFixture.PageSpec(612, 792, Text: "SECRET"));
        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var layer = await _pdfium.RenderOverlayLayerAsync(handle, 0, 0,
            new PageOverlay[] { new RedactionDraft(100, 100, 200, 60) }, 612, 792, CancellationToken.None);

        var o = 130 * layer.Stride + 200 * 4;
        Assert.Equal(255, layer.Bgra[o + 3]);
        Assert.True(layer.Bgra[o] < 40 && layer.Bgra[o + 1] < 40 && layer.Bgra[o + 2] < 40, "середина вымарки должна быть чёрной");

        var outside = 400 * layer.Stride + 400 * 4;
        Assert.Equal(0, layer.Bgra[outside + 3]);
    }

    [Fact]
    public async Task Marks_Without_Text_Do_Not_Bake_A_Copy_Of_The_Page_For_Text()
    {
        // Испечённая копия страницы для поиска и выделения раньше собиралась
        // после любой пометки — на тяжёлом чертеже это секунды на щелчок.
        var path = TempPdf(new PdfFixture.PageSpec(612, 792, Text: "SEARCHABLE"));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0, Marks(0)[0]));
            var (handle, index) = await document.ResolveTextPageAsync(0, CancellationToken.None);
            Assert.Same(document.PrimaryHandle, handle);
            Assert.Equal(0, index);

            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("ADDED", 100, 600, 14, 0xFF000000, 0)));
            var (baked, _) = await document.ResolveTextPageAsync(0, CancellationToken.None);
            Assert.NotSame(document.PrimaryHandle, baked);
            Assert.Contains("ADDED", await baked.GetPageTextAsync(0, CancellationToken.None));
        }
    }

    [Fact]
    public void Content_Edits_And_Highlights_Still_Take_The_Full_Redraw()
    {
        Assert.True(OverlayLayers.CanDrawAsLayer(Marks(0)));
        Assert.False(OverlayLayers.CanDrawAsLayer(new PageOverlay[]
        {
            new ImageObjectReplacement(0, new byte[4], 1, 1),
        }));
        Assert.False(OverlayLayers.CanDrawAsLayer(new PageOverlay[]
        {
            new TextMarkupDraft(TextMarkupKind.Highlight, new[] { new TextMarkupRect(0, 0, 10, 10) }, 0xFFFFFF00, "", "Тест"),
        }));
        Assert.False(OverlayLayers.CanDrawAsLayer(Array.Empty<PageOverlay>()));
    }
}
