using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Замена визуального содержимого страницы растром (возврат из внешнего
/// редактора). Главное требование: аннотации, ссылки и поля форм НЕ теряются,
/// размер страницы сохраняется.
/// </summary>
public sealed class PageRasterReplacementTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static byte[] SolidBgra(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xFF;
        }
        return pixels;
    }

    [Fact]
    public async Task Replacement_Swaps_Content_But_Keeps_Annotations_And_Size()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Страница с текстом, ссылкой и заметкой.
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                  "/Annots [5 0 R 7 0 R] /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n" +
                  "4 0 obj\n<< /Length 52 >>\nstream\nBT /F1 24 Tf 72 700 Td (ORIGINALTEXT) Tj ET\nendstream\nendobj\n" +
                  // Только ASCII: файл пишется как Latin1, кириллица требует
                  // UTF-16BE-строк (она покрыта отдельными тестами аннотаций).
                  "5 0 obj\n<< /Type /Annot /Subtype /Text /Rect [400 700 420 720] /Contents (Keep this note) /T (Author) >>\nendobj\n" +
                  "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
                  "7 0 obj\n<< /Type /Annot /Subtype /Link /Rect [100 300 300 340] /A << /S /URI /URI (https://example.org/keep) >> >>\nendobj\n" +
                  "8 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
                  "trailer\n<< /Size 9 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var path = Path.Combine(dir, "page.pdf");
        await File.WriteAllBytesAsync(path, System.Text.Encoding.Latin1.GetBytes(raw));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            Assert.Contains("ORIGINALTEXT",
                await document.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None));
            // GetAnnotations намеренно не отдаёт Link/Popup — это список для
            // панели комментариев; ссылка проверяется отдельно ниже.
            Assert.Single(await document.PrimaryHandle.GetAnnotationsAsync(0, CancellationToken.None));
            Assert.NotNull(await document.PrimaryHandle.GetLinkAtAsync(0, 0, 200, 792 - 320, CancellationToken.None));

            // Возврат из редактора: синий растр на всю страницу.
            document.Session.Apply(new AddOverlayOperation(0,
                new PageRasterReplacement(SolidBgra(300, 388, 0xFF, 0x40, 0x00), 300, 388)));

            var saved = Path.Combine(dir, "edited.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);

            // 1) Прежнее содержимое исчезло.
            var text = await reopened.GetPageTextAsync(0, CancellationToken.None);
            Assert.DoesNotContain("ORIGINALTEXT", text);

            // 2) Аннотация-заметка сохранилась.
            var annots = await reopened.GetAnnotationsAsync(0, CancellationToken.None);
            Assert.Contains(annots, a => a.Contents == "Keep this note");

            // 3) Ссылка сохранилась и осталась рабочей.
            var link = await reopened.GetLinkAtAsync(0, 0, 200, 792 - 320, CancellationToken.None);
            Assert.NotNull(link);
            Assert.Equal("https://example.org/keep", link!.Uri);

            // 4) Размер страницы не изменился.
            Assert.Equal(612, reopened.Info.Pages[0].WidthPoints, 1);
            Assert.Equal(792, reopened.Info.Pages[0].HeightPoints, 1);
            Assert.Equal(2, reopened.Info.PageCount);

            // 5) Новое изображение реально на странице: центр синий.
            var render = await reopened.RenderPageAsync(0, 306, 396, 0, CancellationToken.None);
            var center = (198 * render.Stride) + (153 * 4);
            Assert.True(render.Bgra[center] > 200, "Синий канал центра страницы должен быть насыщенным");
            Assert.True(render.Bgra[center + 2] < 60, "Красный канал центра страницы должен быть тёмным");
        }
    }

    [Fact]
    public async Task Replacement_Is_Undoable()
    {
        var path = PdfFixture.WriteToTemp("undo-raster.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "KEEPME"));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new PageRasterReplacement(SolidBgra(50, 50, 0, 0, 0xFF), 50, 50)));
            Assert.Single(document.Session.Model.Pages[0].OverlayList);

            document.Session.Undo();
            Assert.Empty(document.Session.Model.Pages[0].OverlayList);

            // После отмены сохраняется исходное содержимое.
            var saved = Path.Combine(Path.GetDirectoryName(path)!, "after-undo.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);
            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            Assert.Contains("KEEPME", await reopened.GetPageTextAsync(0, CancellationToken.None));
        }
    }
}
