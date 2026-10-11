using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Export;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

public sealed class FullAuditRegressionTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _engine = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfAudit", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }
    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<OpenedDocument> OpenAsync(string text = "Original text")
    {
        var source = PdfFixture.WriteToTemp("audit.pdf", new PdfFixture.PageSpec(400, 600, Text: text));
        return await OpenedDocument.OpenAsync(_engine, source, null, CancellationToken.None);
    }

    [Fact]
    public async Task Cached_Unsaved_Text_Follows_Later_Rotation_And_Undo()
    {
        await using var document = await OpenAsync();
        document.Session.Apply(new AddOverlayOperation(0, new TextOverlay("Unsaved text", 20, 20, 12, 0xFF000000, 0)));
        var (first, _) = await document.ResolveTextPageAsync(0, CancellationToken.None);
        Assert.Equal(400, first.Info.Pages[0].WidthPoints);
        document.Session.Apply(new RotatePagesOperation(new[] { 0 }, 1));
        var (turned, page) = await document.ResolveTextPageAsync(0, CancellationToken.None);
        Assert.Equal(400, turned.Info.Pages[0].WidthPoints);
        var (export, exportIndex, extra) = await document.ResolveExportPageAsync(0, CancellationToken.None);
        Assert.Equal(1, extra);
        var preview = await export.RenderPageAsync(exportIndex, 300, 200, extra, CancellationToken.None);
        var logical = await document.RenderLogicalPageAsync(0, 300, 200, CancellationToken.None);
        Assert.Equal(logical.Bgra, preview.Bgra);
        Assert.Contains("Unsaved text", await turned.GetPageTextAsync(page, CancellationToken.None));
        document.Session.Undo();
        var (restored, _) = await document.ResolveTextPageAsync(0, CancellationToken.None);
        Assert.Equal(400, restored.Info.Pages[0].WidthPoints);
    }

    [Fact]
    public async Task Word_Export_Of_A_Rotated_Page_With_A_New_Note_Keeps_Both()
    {
        await using var document = await OpenAsync();
        document.Session.Apply(new RotatePagesOperation(new[] { 0 }, 1));
        document.Session.Apply(new AddOverlayOperation(0, new NoteAnnotationDraft(30, 30, "Audit note", "Reviewer")));
        var target = Path.Combine(_dir, "turned.docx");
        await new ConvertService(_engine).ExportToWordAsync(document, target, null, new WordExportOptions(),
            new PageAnalysisOptions(RecognizeScans: false), null, CancellationToken.None);
        using var word = WordprocessingDocument.Open(target, false);
        var size = word.MainDocumentPart!.Document!.Body!.Descendants<PageSize>().Last();
        Assert.Equal(12000u, size.Width!.Value);
        Assert.Equal(8000u, size.Height!.Value);
        Assert.Contains("Audit note", word.MainDocumentPart.WordprocessingCommentsPart!.Comments!.InnerText);
    }

    [Fact]
    public async Task Removed_Annotations_Disappear_From_Rendering_And_Export()
    {
        await using var fixture = await OpenAsync();
        var annotated = Path.Combine(_dir, "annotated.pdf");
        await _engine.ComposeAsync(new[] { new ComposedPage(fixture.PrimaryHandle, 0, 0,
            new PageOverlay[] { new NoteAnnotationDraft(35, 35, "Removed note", "Author") }) }, annotated, CancellationToken.None);
        await using var document = await OpenedDocument.OpenAsync(_engine, annotated, null, CancellationToken.None);
        var notes = await document.PrimaryHandle.GetAnnotationsAsync(0, CancellationToken.None);
        var note = notes.First(n => n.Contents == "Removed note");
        document.Session.Apply(new RemoveExistingAnnotationOperation(0, note.AnnotIndex));
        var (edited, index, rotation) = await document.ResolveExportPageAsync(0, CancellationToken.None);
        Assert.DoesNotContain(await edited.GetAnnotationsAsync(index, CancellationToken.None), n => n.Contents == "Removed note");
        var expected = await edited.RenderPageAsync(index, 200, 300, rotation, CancellationToken.None);
        var visible = await document.RenderLogicalPageAsync(0, 200, 300, CancellationToken.None);
        Assert.Equal(expected.Bgra, visible.Bgra);
        document.Session.Undo();
        var (restored, restoredIndex, _) = await document.ResolveExportPageAsync(0, CancellationToken.None);
        Assert.Contains(await restored.GetAnnotationsAsync(restoredIndex, CancellationToken.None), n => n.Contents == "Removed note");
    }

    [Fact]
    public async Task Redacted_Text_Cannot_Leak_Into_Word_Or_Excel()
    {
        await using var document = await OpenAsync("TOPSECRET42");
        document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(0, 0, 400, 600)));
        var wordPath = Path.Combine(_dir, "redacted.docx");
        var excelPath = Path.Combine(_dir, "redacted.xlsx");
        var convert = new ConvertService(_engine);
        await convert.ExportToWordAsync(document, wordPath, null, new WordExportOptions(),
            new PageAnalysisOptions(RecognizeScans: false), null, CancellationToken.None);
        await convert.ExportToExcelAsync(document, excelPath, null, new ExcelExportOptions(),
            new PageAnalysisOptions(RecognizeScans: false), null, CancellationToken.None);
        using var word = WordprocessingDocument.Open(wordPath, false);
        using var excel = SpreadsheetDocument.Open(excelPath, false);
        Assert.DoesNotContain("TOPSECRET42", word.MainDocumentPart!.Document!.InnerText);
        Assert.DoesNotContain("TOPSECRET42", excel.WorkbookPart!.SharedStringTablePart?.SharedStringTable?.InnerText ?? "");
        Assert.Contains("TOPSECRET42", await document.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None));
        Assert.DoesNotContain("TOPSECRET42", await convert.ExtractTextAsync(document, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelling_Office_Export_Preserves_The_Previous_Output(bool word)
    {
        await using var document = await OpenAsync();
        var target = Path.Combine(_dir, word ? "existing.docx" : "existing.xlsx");
        await File.WriteAllTextAsync(target, "Previous output");
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelProgress(cancellation);
        var convert = new ConvertService(_engine);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (word)
                await convert.ExportToWordAsync(document, target, null, new WordExportOptions(),
                    new PageAnalysisOptions(RecognizeScans: false), progress, cancellation.Token);
            else
                await convert.ExportToExcelAsync(document, target, null, new ExcelExportOptions(),
                    new PageAnalysisOptions(RecognizeScans: false), progress, cancellation.Token);
        });
        Assert.Equal("Previous output", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.nexustmp-*"));
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => cancellation.Cancel();
    }

    [Fact]
    public async Task Closing_A_Document_Releases_Baked_Pages_And_Their_Files()
    {
        var document = await OpenAsync();
        document.Session.Apply(new AddOverlayOperation(0, new TextOverlay("Added", 20, 20, 12, 0xFF000000, 0)));
        var (text, index) = await document.ResolveTextPageAsync(0, CancellationToken.None);
        document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(0, 0, 400, 600)));
        var (export, _, _) = await document.ResolveExportPageAsync(0, CancellationToken.None);
        var temporary = export.FilePath;
        Assert.True(File.Exists(temporary));
        await document.DisposeAsync();
        Assert.False(File.Exists(temporary));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => text.GetPageTextAsync(index, CancellationToken.None));
    }

    [Fact]
    public async Task Dropped_Image_Temporaries_Survive_Transfer_And_Are_Cleaned_After_All_Tabs_Close()
    {
        var source = await OpenAsync();
        var target = await OpenAsync();
        var image = Path.Combine(_dir, "image.jpg");
        File.WriteAllText(image, "Decoder fixture");
        var pixels = Enumerable.Repeat((byte)255, 40 * 60 * 4).ToArray();
        await source.InsertFilesAsync(_engine, new[] { image }, 1,
            _ => new ImagePageSpec(pixels, 40, 60, 400, 600), _dir, null, CancellationToken.None);
        var temporary = source.Session.Model.Sources.Values.Single(p => p.StartsWith(_dir) && p.EndsWith(".pdf"));
        await target.InsertPagesFromAsync(_engine, source, new[] { 1 }, 1, CancellationToken.None);
        await source.DisposeAsync();
        Assert.True(File.Exists(temporary));
        var saved = Path.Combine(_dir, "saved.pdf");
        await new SaveService(_engine).SaveCopyAsync(target, saved, CancellationToken.None);
        await target.DisposeAsync();
        Assert.False(File.Exists(temporary));
        Assert.True(File.Exists(saved));
        Assert.True(File.Exists(image));
        await using var reopened = await _engine.OpenAsync(saved, null, CancellationToken.None);
        Assert.Equal(2, reopened.Info.PageCount);
    }
}
