using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Сохранение правит КОПИЮ файла-основы, а не собирает новый документ. Здесь
/// закреплено то, ради чего это сделано: после правки страниц в файле
/// остаются пароль, закладки, форма и вложения; вымарка удаляет текст при
/// обычном сохранении; неудачное сохранение не теряет ни одной правки.
/// </summary>
public sealed class SaveStructureTests : IAsyncLifetime
{
    private PdfiumRenderEngine _engine = null!;

    public Task InitializeAsync()
    {
        _engine = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    private static string Write(string name, byte[] bytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Bookmarks_Survive_Moving_And_Rotating_Pages()
    {
        var path = Write("outline.pdf", PdfFixture.BuildWithOutline());
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new MovePagesOperation(new[] { 2 }, 0));   // третья — первой
            document.Session.Apply(new RotatePagesOperation(new[] { 1 }, 1));
            await new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None);
        }

        await using var saved = await _engine.OpenAsync(path, null, CancellationToken.None);
        var bookmarks = await saved.GetBookmarksAsync(CancellationToken.None);
        Assert.Equal(new[] { "Chapter One", "Chapter Two" }, bookmarks.Select(b => b.Title));
        // Закладки указывают на те же страницы, что и раньше, — уже на новых местах.
        Assert.Equal(1, bookmarks[0].TargetPageIndex);   // бывшая первая теперь вторая
        Assert.Equal(0, bookmarks[1].TargetPageIndex);   // бывшая третья теперь первая
    }

    [Fact]
    public async Task Form_Survives_A_Page_Rotation_And_An_Added_Note()
    {
        var path = Write("form.pdf", PdfFixture.BuildWithTextField("name", 100, 600, 300, 630));
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            Assert.Equal(1, await document.PrimaryHandle.GetFormTypeAsync(CancellationToken.None));
            document.Session.Apply(new RotatePagesOperation(new[] { 0 }, 1));
            document.Session.Apply(new AddOverlayOperation(0, new NoteAnnotationDraft(50, 50, "заметка", "Тест")));
            await new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None);
        }

        await using var saved = await _engine.OpenAsync(path, null, CancellationToken.None);
        // Пересборка превращала форму в картинку с мёртвыми полями.
        Assert.Equal(1, await saved.GetFormTypeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Attachment_Survives_Duplicating_And_Deleting_Pages()
    {
        var path = Write("attach.pdf", PdfFixture.BuildWithAttachment("note.txt", "inside"));
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new DuplicatePagesOperation(new[] { 0 }));
            document.Session.Apply(new DeletePagesOperation(new[] { 0 }));
            await new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None);
        }

        await using var saved = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(1, saved.Info.PageCount);
        var attachments = await saved.GetAttachmentsAsync(CancellationToken.None);
        Assert.Contains(attachments, a => a.Name == "note.txt");
    }

    [Fact]
    public async Task Password_Survives_Deleting_A_Page()
    {
        var qpdf = new QpdfEngine();
        if (!qpdf.IsAvailable) return;
        var plain = PdfFixture.WriteToTemp("pw-src.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "first"),
            new PdfFixture.PageSpec(612, 792, Text: "second"));
        var path = Path.Combine(Path.GetDirectoryName(plain)!, "pw.pdf");
        await qpdf.EncryptAsync(plain, path, "pw123", null, CancellationToken.None);

        var document = await OpenedDocument.OpenAsync(_engine, path, "pw123", CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new DeletePagesOperation(new[] { 0 }));
            await new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None);
        }

        // Раньше правка страниц молча снимала пароль с файла.
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(
            () => _engine.OpenAsync(path, null, CancellationToken.None));
        await using var saved = await _engine.OpenAsync(path, "pw123", CancellationToken.None);
        Assert.Equal(1, saved.Info.PageCount);
        Assert.Contains("second", await saved.GetPageTextAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Redaction_Removes_The_Text_On_A_Plain_Save()
    {
        var path = PdfFixture.WriteToTemp("redact.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "SECRET VALUE"),
            new PdfFixture.PageSpec(612, 792, Text: "KEEP ME"));
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            // Вся страница под вымаркой: какой бы ни была позиция текста, его не должно остаться.
            document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(0, 0, 612, 792)));
            await new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None);
        }

        await using var saved = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(2, saved.Info.PageCount);
        Assert.DoesNotContain("SECRET", await saved.GetPageTextAsync(0, CancellationToken.None));
        Assert.Contains("KEEP ME", await saved.GetPageTextAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_Save_Keeps_Every_Edit_And_The_Open_File()
    {
        var path = PdfFixture.WriteToTemp("fail.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "one"),
            new PdfFixture.PageSpec(612, 792, Text: "two"));
        var original = await File.ReadAllBytesAsync(path);
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            document.Session.Apply(new RotatePagesOperation(new[] { 1 }, 1));
            document.Session.Apply(new AddOverlayOperation(0, new TextOverlay("added", 100, 100, 12, 0xFF000000, 0)));
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                var error = await Assert.ThrowsAsync<SaveFailedException>(() =>
                    new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None));
                Assert.Equal(SaveFailure.ReadOnly, error.Failure);
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            // Правки, история и признак несохранённости на месте; страницы рисуются.
            Assert.True(document.Session.IsDirty);
            Assert.Equal(1, document.Session.Model.Pages[1].RotationOffset);
            Assert.Single(document.Session.Model.Pages[0].OverlayList);
            Assert.True(document.Session.History.CanUndo);
            var image = await document.RenderLogicalPageAsync(0, 60, 80, CancellationToken.None);
            Assert.Equal(60, image.PixelWidth);

            // И следующее сохранение проходит.
            await new SaveService(_engine).SaveAsAsync(document, path, keepBackup: false, CancellationToken.None);
            Assert.False(document.Session.IsDirty);
        }
        Assert.NotEqual(original, await File.ReadAllBytesAsync(path));
        await using var saved = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Contains("added", await saved.GetPageTextAsync(0, CancellationToken.None));
        // Прежняя версия, отодвинутая при подмене, не осталась в папке.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".*nexusold-*"));
    }
}
