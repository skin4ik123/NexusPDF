using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Вымарывание: содержимое под областью УНИЧТОЖАЕТСЯ (страница растеризуется),
/// а не прикрывается прямоугольником.
/// </summary>
public sealed class RedactionTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Fact]
    public async Task Redacted_Text_Is_Physically_Absent_From_Saved_File()
    {
        // Фикстура: текст на 72,72 от низа (24pt) — вымарка накрывает его.
        var path = PdfFixture.WriteToTemp("secret.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "TOPSECRET42 visible"));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            // Текст на странице есть до вымарки.
            Assert.Contains("TOPSECRET42",
                await document.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None));

            // Вымарка всей нижней трети страницы (текст фикстуры внизу).
            document.Session.Apply(new AddOverlayOperation(0,
                new RedactionDraft(0, 792 - 260, 612, 260)));

            var saved = Path.Combine(Path.GetDirectoryName(path)!, "redacted.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            // 1) Текста нет в текстовом слое (страница растеризована целиком).
            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            var text = await reopened.GetPageTextAsync(0, CancellationToken.None);
            Assert.DoesNotContain("TOPSECRET42", text);
            Assert.DoesNotContain("visible", text);

            // 2) Строки нет и в БАЙТАХ файла — «отклеить» вымарку невозможно.
            var bytes = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(saved));
            Assert.DoesNotContain("TOPSECRET42", bytes);

            // 3) Область вымарки на рендере — чёрная.
            var render = await reopened.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
            var probe = ((792 - 130) * render.Stride) + (306 * 4);
            Assert.True(render.Bgra[probe] < 30 && render.Bgra[probe + 1] < 30 && render.Bgra[probe + 2] < 30,
                "Центр вымарки обязан быть чёрным.");

            // 4) Верх страницы остался белым (страница не «залита» целиком).
            var top = (60 * render.Stride) + (306 * 4);
            Assert.True(render.Bgra[top] > 240, "Верх страницы должен остаться нетронутым.");

            Assert.Equal(1, reopened.Info.PageCount);
        }
    }

    [Fact]
    public async Task Pages_Without_Redactions_Keep_Their_Text()
    {
        var path = PdfFixture.WriteToTemp("partial.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Secret page"),
            new PdfFixture.PageSpec(612, 792, Text: "Public page"));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new RedactionDraft(0, 0, 612, 792))); // вся первая страница

            var saved = Path.Combine(Path.GetDirectoryName(path)!, "partial-redacted.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            Assert.Equal(2, reopened.Info.PageCount);
            Assert.DoesNotContain("Secret",
                await reopened.GetPageTextAsync(0, CancellationToken.None));
            // Вторая страница не тронута: текст жив.
            Assert.Contains("Public page",
                await reopened.GetPageTextAsync(1, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Redaction_Draft_Is_Undoable_Before_Save()
    {
        var path = PdfFixture.WriteToTemp("undo.pdf", new PdfFixture.PageSpec(612, 792));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(10, 10, 100, 50)));
            Assert.Single(document.Session.Model.Pages[0].OverlayList);
            document.Session.Undo();
            Assert.Empty(document.Session.Model.Pages[0].OverlayList);
        }
    }
}
