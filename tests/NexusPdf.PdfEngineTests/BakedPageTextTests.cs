using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Текст несохранённых правок должен искаться и выделяться СРАЗУ. Ради этого
/// текстовые операции идут по странице с применёнными правками, а не по
/// исходному файлу. Здесь это и проверяется — без «заработает после
/// сохранения».
/// </summary>
public sealed class BakedPageTextTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Recognized_Text_Is_Searchable_Without_Saving()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "scan.pdf");
        // Страница без текста — как настоящий скан.
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            Assert.Equal("", (await document.PrimaryHandle
                .GetPageTextAsync(0, CancellationToken.None)).Trim());

            // Распознанный слой: невидимые слова поверх изображения.
            document.Session.Apply(new AddOverlayOperation(0, new OcrTextLayerOverlay(new[]
            {
                new OcrWordBox("SEAFARER", 100, 100, 90, 14),
                new OcrWordBox("AGREEMENT", 200, 100, 100, 14),
            })));

            // НИЧЕГО не сохраняем — и текст уже доступен.
            var (handle, pageIndex) = await document.ResolveTextPageAsync(0, CancellationToken.None);
            var text = await handle.GetPageTextAsync(pageIndex, CancellationToken.None);

            Assert.Contains("SEAFARER", text);
            Assert.Contains("AGREEMENT", text);
        }
    }

    [Fact]
    public async Task Search_Finds_Recognized_Text_Without_Saving()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "scan.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var search = new SearchService();
            Assert.Empty(await search.SearchAsync(document, "SEAFARER", false, CancellationToken.None));

            document.Session.Apply(new AddOverlayOperation(0, new OcrTextLayerOverlay(new[]
            {
                new OcrWordBox("SEAFARER", 100, 100, 90, 14),
            })));

            var matches = await search.SearchAsync(document, "SEAFARER", false, CancellationToken.None);
            Assert.NotEmpty(matches);
            Assert.Equal(0, matches[0].LogicalPageIndex);
        }
    }

    [Fact]
    public async Task Added_Text_Is_Selectable_Without_Saving()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "page.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("HELLOWORLD", 100, 100, 24, 0xFF000000, 0)));

            var (handle, pageIndex) = await document.ResolveTextPageAsync(0, CancellationToken.None);
            Assert.Contains("HELLOWORLD",
                await handle.GetPageTextAsync(pageIndex, CancellationToken.None));

            // Символ под курсором находится там же, где нарисован текст:
            // без этого выделение мышью по добавленной надписи не работало бы.
            var index = await handle.GetCharIndexAtAsync(
                pageIndex, 0, 110, 112, CancellationToken.None);
            Assert.True(index >= 0, "под надписью должен находиться символ");
        }
    }

    [Fact]
    public async Task Baked_Page_Is_Rebuilt_When_Edits_Change()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "page.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("FIRST", 100, 100, 24, 0xFF000000, 0)));
            var (h1, p1) = await document.ResolveTextPageAsync(0, CancellationToken.None);
            Assert.Contains("FIRST", await h1.GetPageTextAsync(p1, CancellationToken.None));

            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("SECOND", 100, 200, 24, 0xFF000000, 0)));
            var (h2, p2) = await document.ResolveTextPageAsync(0, CancellationToken.None);
            var text = await h2.GetPageTextAsync(p2, CancellationToken.None);
            Assert.Contains("FIRST", text);
            Assert.Contains("SECOND", text);

            // Отмена возвращает страницу к прежнему состоянию.
            document.Session.Undo();
            var (h3, p3) = await document.ResolveTextPageAsync(0, CancellationToken.None);
            var afterUndo = await h3.GetPageTextAsync(p3, CancellationToken.None);
            Assert.Contains("FIRST", afterUndo);
            Assert.DoesNotContain("SECOND", afterUndo);
        }
    }
}
