using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Удаление существующих аннотаций файла: пометка в сессии → компоновка без них.</summary>
public sealed class AnnotationRemovalTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    /// <summary>Файл с двумя НАСТОЯЩИМИ аннотациями-заметками (запечёнными нашим же движком).</summary>
    private async Task<string> BuildAnnotatedAsync(string dir)
    {
        var basePath = Path.Combine(dir, "base.pdf");
        File.WriteAllBytes(basePath, PdfFixture.Build(new PdfFixture.PageSpec(612, 792)));
        var annotated = Path.Combine(dir, "annotated.pdf");
        await using (var source = await _pdfium.OpenAsync(basePath, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(source, 0, 0, new PageOverlay[]
                    {
                        new NoteAnnotationDraft(100, 100, "Первая заметка", "Автор"),
                        new NoteAnnotationDraft(300, 300, "Вторая заметка", "Автор"),
                    }),
                },
                annotated, CancellationToken.None);
        }
        return annotated;
    }

    [Fact]
    public async Task Marked_Annotation_Is_Removed_On_Save_And_Undoable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = await BuildAnnotatedAsync(dir);

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var annotations = await document.PrimaryHandle.GetAnnotationsAsync(0, CancellationToken.None);
            Assert.Equal(2, annotations.Count);
            var first = annotations.First(a => a.Contents == "Первая заметка");

            document.Session.Apply(new RemoveExistingAnnotationOperation(0, first.AnnotIndex));
            Assert.False(SaveService.CanSaveDirect(document)); // структурная правка → компоновка

            // Undo снимает пометку, Redo возвращает.
            document.Session.Undo();
            Assert.Empty(document.Session.Model.Pages[0].RemovedAnnotationList);
            document.Session.Redo();
            Assert.Single(document.Session.Model.Pages[0].RemovedAnnotationList);

            var saved = Path.Combine(dir, "without-first.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            var remaining = await reopened.GetAnnotationsAsync(0, CancellationToken.None);
            var note = Assert.Single(remaining);
            Assert.Equal("Вторая заметка", note.Contents);
        }
    }

    [Fact]
    public async Task Removing_Parent_Note_Cascades_To_Its_Popup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Заметка в стиле Acrobat: Text-аннотация с парной Popup (/Popup ↔ /Parent).
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R] >>\nendobj\n" +
                  "4 0 obj\n<< /Type /Annot /Subtype /Text /Rect [100 700 120 720] /Contents (Note from Acrobat) /T (Author) /Popup 5 0 R >>\nendobj\n" +
                  "5 0 obj\n<< /Type /Annot /Subtype /Popup /Rect [100 600 300 700] /Parent 4 0 R >>\nendobj\n" +
                  "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var path = Path.Combine(dir, "acrobat-note.pdf");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(raw));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var annotations = await document.PrimaryHandle.GetAnnotationsAsync(0, CancellationToken.None);
            var note = Assert.Single(annotations); // Popup в панель не попадает
            Assert.Equal("Note from Acrobat", note.Contents);

            document.Session.Apply(new RemoveExistingAnnotationOperation(0, note.AnnotIndex));
            var saved = Path.Combine(dir, "no-note.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            // Ни заметки, ни попапа в /Annots страницы. (Мёртвый объект-сирота
            // в файле допустим — SaveAsCopy копирует таблицу объектов целиком;
            // важно, что из массива аннотаций страницы вычищены ОБА.)
            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            Assert.Empty(await reopened.GetAnnotationsAsync(0, CancellationToken.None));
            var bytes = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(saved));
            var annots = System.Text.RegularExpressions.Regex.Match(bytes, @"/Annots\s*\[([^\]]*)\]");
            if (annots.Success)
                Assert.DoesNotMatch(@"\d+\s+0\s+R", annots.Groups[1].Value);
        }
    }

    [Fact]
    public async Task Duplicated_Page_Copies_Do_Not_Share_Removals()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = await BuildAnnotatedAsync(dir);

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            // Дубль страницы: копия 1 теряет первую заметку, копия 2 сохраняет обе.
            var model = document.Session.Model;
            model.Pages.Add(model.Pages[0] with { RemovedAnnotations = null, Overlays = null });
            var annotations = await document.PrimaryHandle.GetAnnotationsAsync(0, CancellationToken.None);
            var first = annotations.First(a => a.Contents == "Первая заметка");
            document.Session.Apply(new RemoveExistingAnnotationOperation(0, first.AnnotIndex));

            var saved = Path.Combine(dir, "dup.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            Assert.Equal(2, reopened.Info.PageCount);
            var pageOne = await reopened.GetAnnotationsAsync(0, CancellationToken.None);
            var pageTwo = await reopened.GetAnnotationsAsync(1, CancellationToken.None);
            Assert.Single(pageOne);
            Assert.Equal("Вторая заметка", pageOne[0].Contents);
            Assert.Equal(2, pageTwo.Count); // копия без пометок не пострадала
        }
    }

    [Fact]
    public async Task Removing_Both_Annotations_Leaves_Clean_Page()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = await BuildAnnotatedAsync(dir);

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            var annotations = await document.PrimaryHandle.GetAnnotationsAsync(0, CancellationToken.None);
            foreach (var annotation in annotations)
                document.Session.Apply(new RemoveExistingAnnotationOperation(0, annotation.AnnotIndex));

            var saved = Path.Combine(dir, "clean.pdf");
            await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);

            await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
            Assert.Empty(await reopened.GetAnnotationsAsync(0, CancellationToken.None));
        }
    }
}
