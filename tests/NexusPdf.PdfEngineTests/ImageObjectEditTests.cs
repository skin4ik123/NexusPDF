using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Правка ВЫБРАННОГО изображения: клик находит картинку, замена меняет только
/// её, а текст страницы остаётся текстом (страница не растрируется).
/// </summary>
public sealed class ImageObjectEditTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 0xFF;
        }
        return pixels;
    }

    /// <summary>Страница с текстом и ОДНИМ красным изображением 200×100 pt в точке (100, 100) сверху.</summary>
    private async Task<string> BuildPageWithImageAsync(string dir)
    {
        var basePath = Path.Combine(dir, "base.pdf");
        File.WriteAllBytes(basePath, PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "TEXTSTAYSTEXT")));
        var withImage = Path.Combine(dir, "with-image.pdf");
        await using (var source = await _pdfium.OpenAsync(basePath, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(source, 0, 0, new PageOverlay[]
                    {
                        new ImageOverlay(Solid(40, 20, 0, 0, 0xFF), 40, 20, 100, 100, 200, 100),
                    }),
                },
                withImage, CancellationToken.None);
        }
        return withImage;
    }

    [Fact]
    public async Task Image_Is_Found_By_Click_And_Replaced_Without_Rasterizing_Page()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = await BuildPageWithImageAsync(dir);

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            // Клик в центр картинки: (100..300, 100..200) в отображаемых пунктах.
            var found = await document.PrimaryHandle.GetImageObjectAtAsync(
                0, 0, 200, 150, CancellationToken.None);
            Assert.NotNull(found);
            Assert.True(found!.PixelWidth > 0 && found.PixelHeight > 0);
            // Рамка найденного объекта совпадает с местом вставки.
            Assert.Equal(100, found.XPt, 1);
            Assert.Equal(100, found.YPt, 1);
            Assert.Equal(200, found.WidthPt, 1);
            Assert.Equal(100, found.HeightPt, 1);
            // Исходный растр красный.
            Assert.True(found.Bgra[2] > 200 && found.Bgra[0] < 60);

            // Мимо картинки изображение не находится.
            Assert.Null(await document.PrimaryHandle.GetImageObjectAtAsync(
                0, 0, 500, 700, CancellationToken.None));

            // Возврат из редактора: та же картинка, но синяя.
            document.Session.Apply(new AddOverlayOperation(0,
                new ImageObjectReplacement(found.ObjectIndex, Solid(40, 20, 0xFF, 0, 0), 40, 20)));

            var saved = Path.Combine(dir, "image-edited.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);

            // 1) ТЕКСТ СТРАНИЦЫ ОСТАЛСЯ ТЕКСТОМ — страница не растрирована.
            Assert.Contains("TEXTSTAYSTEXT",
                await reopened.GetPageTextAsync(0, CancellationToken.None));

            // 2) Картинка на прежнем месте и стала синей.
            var after = await reopened.GetImageObjectAtAsync(0, 0, 200, 150, CancellationToken.None);
            Assert.NotNull(after);
            Assert.True(after!.Bgra[0] > 200, "Синий канал должен быть насыщенным");
            Assert.True(after.Bgra[2] < 60, "Красный канал должен быть тёмным");

            // 3) Координаты и размер объекта сохранены (матрица не тронута).
            Assert.Equal(100, after.XPt, 1);
            Assert.Equal(100, after.YPt, 1);
            Assert.Equal(200, after.WidthPt, 1);
            Assert.Equal(100, after.HeightPt, 1);

            // 4) Размер страницы не изменился.
            Assert.Equal(612, reopened.Info.Pages[0].WidthPoints, 1);
            Assert.Equal(792, reopened.Info.Pages[0].HeightPoints, 1);
        }
    }

    [Fact]
    public async Task Replacement_Is_Undoable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = await BuildPageWithImageAsync(dir);
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var found = await document.PrimaryHandle.GetImageObjectAtAsync(
                0, 0, 200, 150, CancellationToken.None);
            document.Session.Apply(new AddOverlayOperation(0,
                new ImageObjectReplacement(found!.ObjectIndex, Solid(10, 10, 0, 0xFF, 0), 10, 10)));
            Assert.Single(document.Session.Model.Pages[0].OverlayList);

            document.Session.Undo();
            Assert.Empty(document.Session.Model.Pages[0].OverlayList);

            var saved = Path.Combine(dir, "undo.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);
            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            var after = await reopened.GetImageObjectAtAsync(0, 0, 200, 150, CancellationToken.None);
            Assert.NotNull(after);
            Assert.True(after!.Bgra[2] > 200, "После отмены изображение снова красное");
        }
    }
}
