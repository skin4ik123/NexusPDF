using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Запекание наложенного контента: текст (кириллица), изображения, повороты.</summary>
public sealed class OverlayTests : IAsyncLifetime
{
    private PdfiumRenderEngine _engine = null!;

    public Task InitializeAsync()
    {
        _engine = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    [Fact]
    public async Task Text_Overlay_With_Cyrillic_Is_Baked_And_Searchable()
    {
        var path = PdfFixture.WriteToTemp("overlay-text.pdf", new PdfFixture.PageSpec(612, 792));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var target = Path.Combine(Path.GetDirectoryName(path)!, "overlay-text-out.pdf");
        await _engine.ComposeAsync(new[]
        {
            new ComposedPage(doc, 0, 0, new PageOverlay[]
            {
                new TextOverlay("Согласовано: Артур Юрчук", 60, 60, 18, 0xFF1A1AB4, 0),
            }),
        }, target, CancellationToken.None);

        await using var result = await _engine.OpenAsync(target, null, CancellationToken.None);
        var text = await result.GetPageTextAsync(0, CancellationToken.None);
        Assert.Contains("Согласовано", text);
        Assert.Contains("Юрчук", text);
    }

    [Fact]
    public async Task Text_Overlay_On_Rotated_Page_Is_Baked()
    {
        var path = PdfFixture.WriteToTemp("overlay-rot.pdf", new PdfFixture.PageSpec(612, 792));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        // Страница дополнительно повёрнута на 90°: отображаемая ширина = 792.
        var target = Path.Combine(Path.GetDirectoryName(path)!, "overlay-rot-out.pdf");
        await _engine.ComposeAsync(new[]
        {
            new ComposedPage(doc, 0, 1, new PageOverlay[]
            {
                new TextOverlay("Верхний колонтитул", 40, 20, 14, 0xFF000000, 0),
            }),
        }, target, CancellationToken.None);

        await using var result = await _engine.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(792, result.Info.Pages[0].WidthPoints, 1);
        Assert.Contains("колонтитул", await result.GetPageTextAsync(0, CancellationToken.None));

        // Текст должен визуально оказаться у ВЕРХНЕГО края отображаемой страницы:
        // рендерим и проверяем, что нижняя половина страницы осталась чистой,
        // а в верхней трети есть неබелые пиксели.
        var image = await result.RenderPageAsync(0, 396, 306, 0, CancellationToken.None);
        Assert.True(CountInk(image, 0.0, 0.35) > 0, "нет текста в верхней трети");
        Assert.Equal(0, CountInk(image, 0.55, 1.0));
    }

    [Fact]
    public async Task Overlay_Placed_Before_Rotation_Is_Remapped_To_Final_Frame()
    {
        var path = PdfFixture.WriteToTemp("overlay-remap.pdf",
            new PdfFixture.PageSpec(612, 792, Text: " ")); // без фонового текста фикстуры
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        // Оверлей размещён у ВЕРХА портретной страницы (PlacedRotation=0),
        // затем страницу довернули на 90° по часовой: контент верха портрета
        // должен оказаться у ПРАВОГО края альбомной страницы.
        var target = Path.Combine(Path.GetDirectoryName(path)!, "overlay-remap-out.pdf");
        await _engine.ComposeAsync(new[]
        {
            new ComposedPage(doc, 0, 1, new PageOverlay[]
            {
                new TextOverlay("Шапка", 200, 24, 20, 0xFF000000, 0) { PlacedRotation = 0 },
            }),
        }, target, CancellationToken.None);

        await using var result = await _engine.OpenAsync(target, null, CancellationToken.None);
        Assert.Contains("Шапка", await result.GetPageTextAsync(0, CancellationToken.None));

        var image = await result.RenderPageAsync(0, 396, 306, 0, CancellationToken.None);
        Assert.True(CountInkInXBand(image, 0.75, 1.0) > 0, "нет текста у правого края");
        Assert.Equal(0, CountInkInXBand(image, 0.0, 0.5));
    }

    private static int CountInkInXBand(RenderedPageImage image, double fromXFraction, double toXFraction)
    {
        var count = 0;
        var fromX = (int)(image.PixelWidth * fromXFraction);
        var toX = (int)(image.PixelWidth * toXFraction);
        for (var y = 0; y < image.PixelHeight; y++)
        {
            for (var x = fromX; x < toX; x++)
            {
                var offset = y * image.Stride + x * 4;
                if (image.Bgra[offset] < 0xF0 || image.Bgra[offset + 1] < 0xF0 || image.Bgra[offset + 2] < 0xF0)
                    count++;
            }
        }
        return count;
    }

    [Fact]
    public async Task Image_Overlay_Pixels_Land_In_Target_Rect()
    {
        var path = PdfFixture.WriteToTemp("overlay-img.pdf",
            new PdfFixture.PageSpec(600, 600, Text: " "));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        // Сплошной красный квадрат 10x10 px → прямоугольник 100x100pt в центре.
        var pixels = new byte[10 * 10 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x00;      // B
            pixels[i + 1] = 0x00;  // G
            pixels[i + 2] = 0xFF;  // R
            pixels[i + 3] = 0xFF;  // A
        }

        var target = Path.Combine(Path.GetDirectoryName(path)!, "overlay-img-out.pdf");
        await _engine.ComposeAsync(new[]
        {
            new ComposedPage(doc, 0, 0, new PageOverlay[]
            {
                new ImageOverlay(pixels, 10, 10, 250, 250, 100, 100),
            }),
        }, target, CancellationToken.None);

        await using var result = await _engine.OpenAsync(target, null, CancellationToken.None);
        var image = await result.RenderPageAsync(0, 300, 300, 0, CancellationToken.None);

        // Центр страницы (150,150 в растре 300x300) должен быть красным.
        var offset = 150 * image.Stride + 150 * 4;
        Assert.True(image.Bgra[offset + 2] > 200, "нет красного в центре");
        Assert.True(image.Bgra[offset] < 60, "синий канал должен быть пуст");

        // Угол страницы — нетронутый белый.
        Assert.Equal(0xFF, image.Bgra[10 * image.Stride + 10 * 4]);
    }

    [Fact]
    public async Task Overlay_Operations_Are_Undoable_And_Flow_Through_Save()
    {
        var path = PdfFixture.WriteToTemp("overlay-ops.pdf", new PdfFixture.PageSpec(612, 792));
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("Штамп", 100, 100, 24, 0xFF000000, 0)));
            Assert.Single(document.Session.Model.Pages[0].OverlayList);

            document.Session.Undo();
            Assert.Empty(document.Session.Model.Pages[0].OverlayList);
            document.Session.Redo();

            var target = Path.Combine(Path.GetDirectoryName(path)!, "overlay-ops-out.pdf");
            await new SaveService(_engine).SaveAsAsync(document, target, keepBackup: false, CancellationToken.None);

            // После сохранения оверлей запечён в содержимое, а логическая
            // структура сброшена на чистый сохранённый файл.
            Assert.Empty(document.Session.Model.Pages[0].OverlayList);
            Assert.Contains("Штамп",
                await document.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Annotations_Are_Created_And_Readable_After_Save()
    {
        var path = PdfFixture.WriteToTemp("annots.pdf",
            new PdfFixture.PageSpec(600, 600, Text: " "));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var target = Path.Combine(Path.GetDirectoryName(path)!, "annots-out.pdf");
        await _engine.ComposeAsync(new[]
        {
            new ComposedPage(doc, 0, 0, new PageOverlay[]
            {
                new NoteAnnotationDraft(100, 100, "Проверить раздел 3", "Артур"),
                new ShapeAnnotationDraft(200, 200, 150, 100, 0xFFDC2626, 0x60FDE047, 2, false, "важно", "Артур"),
            }),
        }, target, CancellationToken.None);

        await using var result = await _engine.OpenAsync(target, null, CancellationToken.None);
        var annots = await result.GetAnnotationsAsync(0, CancellationToken.None);

        Assert.Equal(2, annots.Count);
        var note = Assert.Single(annots, a => a.Subtype == 1);
        Assert.Equal("Проверить раздел 3", note.Contents);
        Assert.Equal("Артур", note.Author);
        var square = Assert.Single(annots, a => a.Subtype == 5);
        Assert.Equal("важно", square.Contents);

        // Аннотации должны быть видны при рендере с флагом FPDF_ANNOT:
        // внутри прямоугольника маркера есть неисходно-белые пиксели.
        var image = await result.RenderPageAsync(0, 300, 300, 0, CancellationToken.None);
        var inked = 0;
        for (var y = 102; y < 148; y++)
        {
            for (var x = 102; x < 173; x++)
            {
                var offset = y * image.Stride + x * 4;
                if (image.Bgra[offset] < 0xF0 || image.Bgra[offset + 1] < 0xF0 || image.Bgra[offset + 2] < 0xF0)
                    inked++;
            }
        }
        Assert.True(inked > 0, "маркер не отрисован в области прямоугольника");
    }

    private static int CountInk(RenderedPageImage image, double fromYFraction, double toYFraction)
    {
        var count = 0;
        var fromY = (int)(image.PixelHeight * fromYFraction);
        var toY = (int)(image.PixelHeight * toYFraction);
        for (var y = fromY; y < toY; y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var offset = y * image.Stride + x * 4;
                if (image.Bgra[offset] < 0xF0 || image.Bgra[offset + 1] < 0xF0 || image.Bgra[offset + 2] < 0xF0)
                    count++;
            }
        }
        return count;
    }
}
