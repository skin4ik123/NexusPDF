using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Стирание области: единственный способ изменить текст, лежащий внутри
/// Form XObject. Строка стирается вместе с содержимым страницы (растеризацией
/// при сохранении), а новая пишется поверх настоящим текстовым объектом.
///
/// Главное, что здесь проверяется: старых букв в результате НЕТ. Иначе
/// получилась бы ловушка — на экране одно, а поиск и копирование достают
/// прежний текст.
/// </summary>
public sealed class RegionEraseTests : IAsyncLifetime
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

    /// <summary>Документ с двумя строками: одну стираем, вторая обязана уцелеть.</summary>
    private static string WriteSource(string dir)
    {
        var path = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "SECRETLINE")));
        return path;
    }

    [Fact]
    public async Task Erased_Text_Is_Gone_From_The_Document()
    {
        var dir = NewDir();
        var source = WriteSource(dir);

        await using var document = await OpenedDocument.OpenAsync(
            _pdfium, source, null, CancellationToken.None);

        // Строка фикстуры стоит в (72, 72) снизу, то есть ~703 сверху.
        var erase = new RegionEraseDraft(60, 690, 300, 40, 0xFFFFFFFF);
        var replacement = new TextOverlay(
            "ЧИСТАЯ СТРОКА", 72, 700, 24, 0xFF000000, 0, PdfFontCatalog.DefaultFamily);

        var composition = new[]
        {
            new ComposedPage(document.PrimaryHandle, 0, 0,
                new PageOverlay[] { erase, replacement }),
        };

        Assert.True(RedactionBaker.HasRedactions(composition),
            "страница со стиранием обязана уйти на растеризацию");

        var outPath = Path.Combine(dir, "erased.pdf");
        await using (var baked = await RedactionBaker.BakeAsync(
            _pdfium, document, composition, CancellationToken.None))
        {
            Assert.Equal(1, baked.RedactedPages);
            await _pdfium.ComposeAsync(baked.Composition, outPath, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(outPath, null, CancellationToken.None);
        var text = await result.GetPageTextAsync(0, CancellationToken.None);

        // Старых букв в файле быть не должно — ни видимых, ни копируемых.
        Assert.DoesNotContain("SECRETLINE", text, StringComparison.Ordinal);
        // А новая строка обязана остаться настоящим текстом.
        Assert.Contains("ЧИСТАЯ СТРОКА", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Page_Without_Erase_Is_Not_Rasterised()
    {
        var dir = NewDir();
        var source = WriteSource(dir);

        await using var document = await OpenedDocument.OpenAsync(
            _pdfium, source, null, CancellationToken.None);

        var composition = new[]
        {
            new ComposedPage(document.PrimaryHandle, 0, 0,
                new PageOverlay[]
                {
                    new TextOverlay("ДОБАВКА", 72, 300, 14, 0xFF000000, 0),
                }),
        };

        // Обычная надпись растеризации не требует: страница обязана остаться
        // текстовой, иначе одна подпись убивала бы весь текстовый слой.
        Assert.False(RedactionBaker.HasRedactions(composition));
    }
}
