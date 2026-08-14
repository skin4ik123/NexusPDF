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
