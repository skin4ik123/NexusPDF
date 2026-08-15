using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Рисование от руки: штрихи попадают в PDF Ink-аннотацией и реально видны.</summary>
public sealed class InkAnnotationTests : IAsyncLifetime
{
    private const int SubtypeInk = 15;

    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static IReadOnlyList<IReadOnlyList<InkPoint>> HorizontalStroke(
        double y, double fromX, double toX)
    {
        var points = new List<InkPoint>();
        for (var x = fromX; x <= toX; x += 5)
            points.Add(new InkPoint(x, y));
        return new[] { points };
    }

    /// <summary>Доля тёмных пикселей в полосе растра — так видно, нарисовалась ли линия.</summary>
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

    /// <summary>Рамка всех тёмных пикселей растра или null, если тёмных нет.</summary>
    private static (int Left, int Top, int Right, int Bottom)? DarkBounds(RenderedPageImage image)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < image.PixelHeight; y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var o = y * image.Stride + x * 4;
                if (image.Bgra[o] >= 128 || image.Bgra[o + 1] >= 128 || image.Bgra[o + 2] >= 128)
                    continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        }
        return right < 0 ? null : (left, top, right, bottom);
    }

    [Fact]
    public async Task Ink_Stroke_Is_Saved_As_Annotation_And_Is_Visible()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "KEEPTHISTEXT")));
        var target = Path.Combine(dir, "ink.pdf");

        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(handle, 0, 0, new PageOverlay[]
                    {
                        // Толстая чёрная линия поперёк страницы на 300 пт сверху.
                        new InkAnnotationDraft(
                            HorizontalStroke(300, 100, 500), 0xFF000000, 4, "", "Тест"),
                    }),
                },
                target, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);

        // 1. Это именно аннотация — содержимое страницы не тронуто.
        var annotations = await result.GetAnnotationsAsync(0, CancellationToken.None);
        Assert.Contains(annotations, a => a.Subtype == SubtypeInk);
        Assert.Contains("KEEPTHISTEXT", await result.GetPageTextAsync(0, CancellationToken.None));

        // 2. Линия действительно нарисована: у PDFium появляется внешний вид.
        var image = await result.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
        var onLine = DarkFraction(image, 296, 304);
        var elsewhere = DarkFraction(image, 380, 420);
        Assert.True(onLine > 0.3, $"на линии должно быть много тёмных пикселей, а их {onLine:P1}");
        Assert.True(elsewhere < 0.01, $"вне линии страница должна быть чистой, а тёмных {elsewhere:P1}");

        // 3. Размер страницы не изменился.
        Assert.Equal(612, result.Info.Pages[0].WidthPoints, 1);
        Assert.Equal(792, result.Info.Pages[0].HeightPoints, 1);
    }

    [Fact]
    public async Task Ink_Follows_Page_Rotation_Applied_After_Drawing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "src.pdf");
        // Страница намеренно пустая: рамка тёмных пикселей должна описывать
        // ТОЛЬКО линию, иначе в неё попадёт текст и измерение потеряет смысл.
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));
        var target = Path.Combine(dir, "rotated-ink.pdf");

        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            // Нарисовано БЕЗ поворота (PlacedRotation = 0), а страница
            // сохраняется повёрнутой на четверть: линия обязана поехать вместе
            // со страницей, а не остаться в старом месте.
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(handle, 0, 1, new PageOverlay[]
                    {
                        new InkAnnotationDraft(
                            HorizontalStroke(300, 100, 500), 0xFF000000, 4, "", "Тест")
                        { PlacedRotation = 0 },
                    }),
                },
                target, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        // Повёрнутая страница: 792 × 612.
        Assert.Equal(792, result.Info.Pages[0].WidthPoints, 1);
        var image = await result.RenderPageAsync(0, 792, 612, 0, CancellationToken.None);

        // Горизонтальная линия должна стать ВЕРТИКАЛЬНОЙ: рамка тёмных
        // пикселей высокая и узкая. Если бы оверлей остался в старой рамке,
        // рамка была бы широкой и низкой (или линии не было бы вовсе).
        var box = DarkBounds(image);
        Assert.NotNull(box);
        var (left, top, right, bottom) = box!.Value;
        var width = right - left + 1;
        var height = bottom - top + 1;
        Assert.True(height > 350, $"линия должна тянуться вдоль страницы, а её высота {height} px");
        Assert.True(width < 12, $"линия должна остаться тонкой, а её ширина {width} px");
    }

    [Fact]
    public async Task Empty_And_Single_Point_Strokes_Are_Skipped_Not_Crashing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(300, 300)));
        var target = Path.Combine(dir, "empty-ink.pdf");

        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(handle, 0, 0, new PageOverlay[]
                    {
                        new InkAnnotationDraft(
                            new[] { new[] { new InkPoint(10, 10) } }, 0xFF000000, 2, "", ""),
                    }),
                },
                target, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, result.Info.PageCount);
        Assert.DoesNotContain(
            await result.GetAnnotationsAsync(0, CancellationToken.None),
            a => a.Subtype == SubtypeInk);
    }
}
