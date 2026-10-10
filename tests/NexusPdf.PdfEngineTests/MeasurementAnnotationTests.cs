using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Измерения в файле и геометрия страницы для привязки. Проверяется то, что
/// нельзя увидеть по коду: аннотация реально нарисована, подпись лежит рядом с
/// линией, отрезки читаются в тех координатах, в которых их видит человек.
/// </summary>
public sealed class MeasurementAnnotationTests : IAsyncLifetime
{
    private const int SubtypeStamp = 13;

    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsInked(RenderedPageImage image, int x, int y, int threshold)
    {
        var o = y * image.Stride + x * 4;
        return image.Bgra[o] < threshold || image.Bgra[o + 1] < threshold || image.Bgra[o + 2] < threshold;
    }

    /// <summary>Доля не белых пикселей в прямоугольнике растра (порог — насколько пиксель должен отличаться от бумаги).</summary>
    private static double InkFraction(RenderedPageImage image, int x0, int y0, int x1, int y1, int threshold = 200)
    {
        var ink = 0;
        var total = 0;
        for (var y = Math.Max(0, y0); y < Math.Min(image.PixelHeight, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(image.PixelWidth, x1); x++)
            {
                total++;
                if (IsInked(image, x, y, threshold))
                    ink++;
            }
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    /// <summary>
    /// Доля столбцов полосы, в которых есть хоть один тёмный пиксель: так
    /// проверяется, что линия тянется через всю полосу, независимо от её
    /// толщины в пикселях.
    /// </summary>
    private static double ColumnCoverage(RenderedPageImage image, int x0, int y0, int x1, int y1)
    {
        var covered = 0;
        var columns = 0;
        for (var x = Math.Max(0, x0); x < Math.Min(image.PixelWidth, x1); x++)
        {
            columns++;
            for (var y = Math.Max(0, y0); y < Math.Min(image.PixelHeight, y1); y++)
            {
                if (IsInked(image, x, y, 200))
                {
                    covered++;
                    break;
                }
            }
        }
        return columns == 0 ? 0 : (double)covered / columns;
    }

    private static MeasurementDraft Distance(double x1, double y1, double x2, double y2, string label) => new(
        MeasurementKind.Distance, new[] { new InkPoint(x1, y1), new InkPoint(x2, y2) },
        200, MeasureUnit.Meter, 2, label, 0xFFDC2626, "Тест");

    [Fact]
    public async Task Distance_Is_Saved_As_A_Visible_Stamp_With_Its_Label()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "KEEPTHISTEXT")));
        var target = Path.Combine(dir, "measure.pdf");

        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(handle, 0, 0, new PageOverlay[]
                    {
                        // Горизонтальный размер на 300 пт от верха, от x=100 до x=500.
                        Distance(100, 300, 500, 300, "33,12 м"),
                    }),
                },
                target, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);

        // 1. Это аннотация-штамп с подписью и пометкой измерения; содержимое страницы цело.
        var annotations = await result.GetAnnotationsAsync(0, CancellationToken.None);
        var stamp = Assert.Single(annotations, a => a.Subtype == SubtypeStamp);
        Assert.Equal("33,12 м", stamp.Contents);
        Assert.Equal("Measurement", stamp.Subject);
        Assert.Equal("Тест", stamp.Author);
        Assert.Contains("KEEPTHISTEXT", await result.GetPageTextAsync(0, CancellationToken.None));

        // 2. Линия нарисована там, где её поставили, и чертёж вокруг чист.
        var image = await result.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
        var onLine = ColumnCoverage(image, 110, 297, 490, 304);
        var belowLine = InkFraction(image, 110, 340, 490, 400);
        Assert.True(onLine > 0.95, $"размерная линия должна тянуться через всю полосу, а покрыто {onLine:P1}");
        Assert.True(belowLine < 0.01, $"ниже линии страница должна быть чистой, а чернил {belowLine:P1}");

        // 3. Подпись лежит над серединой линии: в полосе над ней есть чернила.
        var label = InkFraction(image, 240, 280, 360, 297);
        Assert.True(label > 0.03, $"над серединой линии ожидалась подпись, а чернил {label:P1}");

        // 4. Засечки на концах выходят за линию по вертикали.
        var tick = InkFraction(image, 98, 294, 103, 306);
        Assert.True(tick > 0.2, $"у конца линии ожидалась засечка, а чернил {tick:P1}");
    }

    [Fact]
    public async Task Area_Is_Filled_Translucently_Inside_Its_Outline()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));
        var target = Path.Combine(dir, "area.pdf");

        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(handle, 0, 0, new PageOverlay[]
                    {
                        new MeasurementDraft(MeasurementKind.Area,
                            new[] { new InkPoint(100, 100), new InkPoint(400, 100), new InkPoint(400, 400), new InkPoint(100, 400) },
                            100, MeasureUnit.Meter, 1, "112,3 м²", 0xFF2563EB, "Тест"),
                    }),
                },
                target, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var image = await result.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);

        // Внутри контура — подкрашено (полупрозрачная заливка лишь слегка
        // темнее бумаги), снаружи — бумага.
        var inside = InkFraction(image, 150, 150, 350, 230, threshold: 245);
        var outside = InkFraction(image, 450, 450, 600, 600, threshold: 245);
        Assert.True(inside > 0.9, $"внутри контура ожидалась заливка, а подкрашено {inside:P1}");
        Assert.True(outside < 0.01, $"снаружи контура должно быть чисто, а подкрашено {outside:P1}");
        Assert.Contains(await result.GetAnnotationsAsync(0, CancellationToken.None),
            a => a.Subtype == SubtypeStamp && a.Contents == "112,3 м²");
    }

    [Fact]
    public async Task Too_Few_Points_Produce_No_Annotation_And_No_Crash()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(300, 300)));
        var target = Path.Combine(dir, "short.pdf");

        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(handle, 0, 0, new PageOverlay[]
                    {
                        new MeasurementDraft(MeasurementKind.Area,
                            new[] { new InkPoint(10, 10), new InkPoint(50, 10) },
                            100, MeasureUnit.Meter, 1, "0", 0xFF000000, ""),
                    }),
                },
                target, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.DoesNotContain(
            await result.GetAnnotationsAsync(0, CancellationToken.None), a => a.Subtype == SubtypeStamp);
    }

    /// <summary>
    /// Живой показ страницы, повёрнутой пользователем, обязан класть правку туда
    /// же, куда её положит сохранение. Раньше запекание для показа читало
    /// поворот из ещё не повёрнутой страницы, и линия на повёрнутом листе
    /// уезжала в чужую рамку.
    /// </summary>
    [Fact]
    public async Task Live_Render_Of_A_Rotated_Page_Matches_The_Saved_File()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));
        var target = Path.Combine(dir, "rotated.pdf");

        // Страница показана с поворотом на четверть (792×612); линия поставлена
        // в ЭТОЙ рамке горизонтально на 150 пт от верха.
        var overlay = new PageOverlay[]
        {
            new InkAnnotationDraft(
                new[] { new[] { new InkPoint(100, 150), new InkPoint(700, 150) } }, 0xFF000000, 4, "", "Тест")
            { PlacedRotation = 1 },
        };

        RenderedPageImage live;
        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            live = await _pdfium.RenderPageWithOverlaysAsync(handle, 0, 1, overlay, 792, 612, CancellationToken.None);
            await _pdfium.ComposeAsync(new[] { new ComposedPage(handle, 0, 1, overlay) }, target, CancellationToken.None);
        }

        await using var saved = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var file = await saved.RenderPageAsync(0, 792, 612, 0, CancellationToken.None);

        foreach (var (name, image) in new[] { ("живой показ", live), ("файл", file) })
        {
            var onLine = ColumnCoverage(image, 120, 146, 680, 154);
            var elsewhere = InkFraction(image, 120, 300, 680, 500);
            Assert.True(onLine > 0.95, $"{name}: линия ожидалась на 150 пт от верха, покрыто {onLine:P1}");
            Assert.True(elsewhere < 0.01, $"{name}: вне линии должно быть чисто, чернил {elsewhere:P1}");
        }
    }

    // ----- Геометрия страницы для привязки -----

    private const string CrossContent =
        "1 w 0 0 0 RG 100 100 m 300 100 l S 200 50 m 200 150 l S " +
        "0 0 1 RG 400 400 m 500 450 l 500 500 l S";

    [Fact]
    public async Task Page_Segments_Come_Out_In_Displayed_Coordinates()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "lines.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, RawContent: CrossContent)));

        await using var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        var segments = await handle.GetPageSegmentsAsync(0, 0, CancellationToken.None);

        // Четыре отрезка; Y отсчитывается от верха (792 − y PDF).
        Assert.Equal(4, segments.Count);
        Assert.Contains(segments, s => Near(s, 100, 692, 300, 692));
        Assert.Contains(segments, s => Near(s, 200, 742, 200, 642));
        Assert.Contains(segments, s => Near(s, 400, 392, 500, 342));
        Assert.Contains(segments, s => Near(s, 500, 342, 500, 292));

        // Индекс по ним находит пересечение креста.
        var index = new VectorSnapIndex(segments, 612, 792);
        var hit = index.Find(202, 690, 6);
        Assert.NotNull(hit);
        Assert.Equal(SnapKind.Intersection, hit!.Value.Kind);
        Assert.Equal(200, hit.Value.X, 3);
        Assert.Equal(692, hit.Value.Y, 3);
    }

    [Fact]
    public async Task Page_Segments_Follow_The_Page_Rotation()
    {
        var dir = TempDir();
        var source = Path.Combine(dir, "lines-rotated.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Rotate: 90, RawContent: CrossContent)));

        await using var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        // Страница 612×792 с /Rotate 90 показывается как 792×612.
        Assert.Equal(792, handle.Info.Pages[0].WidthPoints, 1);
        var segments = await handle.GetPageSegmentsAsync(0, 0, CancellationToken.None);

        // Поворот по часовой: точка (x, y) содержимого → (y, x) показа.
        Assert.Contains(segments, s => Near(s, 100, 100, 100, 300));
        Assert.Contains(segments, s => Near(s, 50, 200, 150, 200));

        // Добавочный поворот пользователя доворачивает ещё на четверть: снова 612×792.
        var turned = await handle.GetPageSegmentsAsync(0, 1, CancellationToken.None);
        Assert.Contains(turned, s => Near(s, 512, 100, 312, 100));
    }

    [Fact]
    public async Task Text_Outlines_And_Invisible_Paths_Are_Not_Snap_Targets()
    {
        // Маленький залитый контур (как буква, переведённая в кривые) и путь
        // без заливки и обводки (вспомогательная геометрия) не попадают в
        // привязку; большой залитый прямоугольник — попадает.
        const string content =
            "0 g 10 10 m 14 10 l 14 16 l 10 16 l h f " +      // «буква» 4×6 пт
            "100 100 m 200 100 l 200 200 l n " +              // невидимый путь
            "0.5 g 300 300 m 400 300 l 400 380 l 300 380 l h f"; // большая заливка
        var dir = TempDir();
        var source = Path.Combine(dir, "filters.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, RawContent: content)));

        await using var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        var segments = await handle.GetPageSegmentsAsync(0, 0, CancellationToken.None);

        Assert.DoesNotContain(segments, s => s.X1 < 20 && s.X2 < 20);
        Assert.DoesNotContain(segments, s => Near(s, 100, 692, 200, 692));
        Assert.Equal(4, segments.Count(s => s.X1 >= 300 && s.X2 >= 300));
    }

    private static bool Near(PdfSegment s, double x1, double y1, double x2, double y2)
    {
        const double eps = 0.05;
        bool Same(double ax, double ay, double bx, double by) => Math.Abs(ax - bx) < eps && Math.Abs(ay - by) < eps;
        return (Same(s.X1, s.Y1, x1, y1) && Same(s.X2, s.Y2, x2, y2)) ||
               (Same(s.X1, s.Y1, x2, y2) && Same(s.X2, s.Y2, x1, y1));
    }
}
