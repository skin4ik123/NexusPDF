using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Выделение текста (символ под курсором) и ссылки PDF: проверка на настоящих
/// координатах, а не на факте наличия метода.
/// </summary>
public sealed class TextSelectionAndLinkTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Fact]
    public async Task Char_Index_At_Point_Hits_Real_Glyph()
    {
        // Фикстура рисует текст 24pt в точке (72, 72) от НИЗА страницы 612x792,
        // значит в отображаемых координатах базовая линия ≈ y = 792-72 = 720.
        var path = PdfFixture.WriteToTemp("hit.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "ABCDEFGH"));
        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var text = await handle.GetPageTextAsync(0, CancellationToken.None);
        Assert.Contains("ABCDEFGH", text);

        // Точка внутри первых глифов (чуть выше базовой линии).
        var index = await handle.GetCharIndexAtAsync(0, 0, 78, 712, CancellationToken.None);
        Assert.True(index >= 0, "Символ под курсором должен определяться");
        Assert.InRange(index, 0, text.Length - 1);
        Assert.Contains(text[index], "ABCDEFGH");

        // Пустое поле страницы — текста нет.
        var empty = await handle.GetCharIndexAtAsync(0, 0, 500, 100, CancellationToken.None);
        Assert.True(empty < 0, $"В пустой области символа быть не должно, получено {empty}");

        // Прямоугольник выделения найденного символа непустой.
        var rects = await handle.GetTextRectsAsync(0, index, 3, CancellationToken.None);
        Assert.NotEmpty(rects);
        Assert.True(rects[0].Right > rects[0].Left && rects[0].Top > rects[0].Bottom);
    }

    [Fact]
    public async Task Web_Link_And_Internal_Link_Are_Detected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Стр. 1: веб-ссылка (URI) в прямоугольнике [100 600 300 640];
        // стр. 2 существует, чтобы внутренняя ссылка вела на неё.
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 6 0 R] >>\nendobj\n" +
                  "4 0 obj\n<< /Type /Annot /Subtype /Link /Rect [100 600 300 640] /Border [0 0 0] /A 5 0 R >>\nendobj\n" +
                  "5 0 obj\n<< /Type /Action /S /URI /URI (https://example.org/doc) >>\nendobj\n" +
                  "6 0 obj\n<< /Type /Annot /Subtype /Link /Rect [100 400 300 440] /Border [0 0 0] /Dest [7 0 R /Fit] >>\nendobj\n" +
                  "7 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
                  "trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var path = Path.Combine(dir, "links.pdf");
        await File.WriteAllBytesAsync(path, System.Text.Encoding.Latin1.GetBytes(raw));

        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(2, handle.Info.PageCount);

        // Центр веб-ссылки: PDF y=620 → отображаемое y = 792-620 = 172.
        var web = await handle.GetLinkAtAsync(0, 0, 200, 172, CancellationToken.None);
        Assert.NotNull(web);
        Assert.Equal("https://example.org/doc", web!.Uri);
        Assert.True(web.TargetPageIndex < 0);

        // Центр внутренней ссылки: PDF y=420 → отображаемое y = 372.
        var internalLink = await handle.GetLinkAtAsync(0, 0, 200, 372, CancellationToken.None);
        Assert.NotNull(internalLink);
        Assert.Null(internalLink!.Uri);
        Assert.Equal(1, internalLink.TargetPageIndex);

        // Пустое место — ссылки нет.
        Assert.Null(await handle.GetLinkAtAsync(0, 0, 50, 50, CancellationToken.None));

        // Перечисление ссылок страницы (для подсветки и курсора-руки).
        var all = await handle.GetPageLinksAsync(0, CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, l => l.Uri == "https://example.org/doc");
        Assert.Contains(all, l => l.Uri == null && l.TargetPageIndex == 1);
        var webRect = all.First(l => l.Uri != null).RectPt;
        Assert.Equal(100, webRect.Left, 1);
        Assert.Equal(640, webRect.Top, 1);
        Assert.Empty(await handle.GetPageLinksAsync(1, CancellationToken.None));
    }
}
