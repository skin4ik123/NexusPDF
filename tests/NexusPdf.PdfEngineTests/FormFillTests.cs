using NexusPdf.Application;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Интерактивное заполнение AcroForm: клик в поле, ввод, сохранение, чтение значения.</summary>
public sealed class FormFillTests : IAsyncLifetime
{
    private PdfiumRenderEngine _engine = null!;

    public Task InitializeAsync()
    {
        _engine = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    [Fact]
    public async Task Fixture_Reports_AcroForm_Type()
    {
        var path = PdfFixture.WriteTextFieldToTemp("form.pdf", "name1");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(1, await doc.GetFormTypeAsync(CancellationToken.None));
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Plain_Document_Has_No_Forms()
    {
        var path = PdfFixture.WriteToTemp("noform.pdf", new PdfFixture.PageSpec(612, 792));
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(0, await doc.GetFormTypeAsync(CancellationToken.None));
        Assert.False(await doc.InitFormsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Click_Type_Save_Roundtrips_Field_Value()
    {
        // Поле /Rect [100 600 400 640] на странице 612x792:
        // в отображаемых координатах (сверху-слева) центр = (250, 792-620=172).
        var path = PdfFixture.WriteTextFieldToTemp("fill.pdf", "name1");
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            var handle = document.PrimaryHandle;
            Assert.True(await handle.InitFormsAsync(CancellationToken.None));

            await handle.FormClickAsync(0, 0, 250, 172, CancellationToken.None);
            foreach (var c in "Hello 123")
                await handle.FormCharAsync(c, CancellationToken.None);
            await handle.FormKillFocusAsync(CancellationToken.None);

            // Прямое сохранение (документ без структурных правок).
            Assert.True(SaveService.CanSaveDirect(document));
            var target = Path.Combine(Path.GetDirectoryName(path)!, "filled.pdf");
            await new SaveService(_engine).SaveAsAsync(document, target, keepBackup: false, CancellationToken.None);

            await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
            var annots = await reopened.GetAnnotationsAsync(0, CancellationToken.None);
            var widget = Assert.Single(annots, a => a.Subtype == 20);
            Assert.Equal("Hello 123", widget.Value);
        }
    }

    [Fact]
    public async Task Backspace_Edits_Field_Value()
    {
        var path = PdfFixture.WriteTextFieldToTemp("edit.pdf", "name1");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));

        await doc.FormClickAsync(0, 0, 250, 172, CancellationToken.None);
        foreach (var c in "abcd")
            await doc.FormCharAsync(c, CancellationToken.None);
        await doc.FormCharAsync((char)8, CancellationToken.None); // Backspace
        await doc.FormKillFocusAsync(CancellationToken.None);

        var target = Path.Combine(Path.GetDirectoryName(path)!, "edited.pdf");
        await doc.SaveCurrentAsync(target, CancellationToken.None);

        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        var widget = Assert.Single(
            await reopened.GetAnnotationsAsync(0, CancellationToken.None), a => a.Subtype == 20);
        Assert.Equal("abc", widget.Value);
    }

    /// <summary>
    /// Надпись на бланке НЕ должна убивать форму. Перекомпоновка собирает файл
    /// из импортов страниц и не переносит AcroForm из каталога: поля оставались
    /// на своих местах, но заполнить их было уже нельзя. Стоило это одной
    /// добавленной строки текста.
    /// </summary>
    [Fact]
    public async Task Adding_Text_To_A_Form_Keeps_The_Form_Fillable()
    {
        var path = PdfFixture.WriteTextFieldToTemp("stamped.pdf", "name1");
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "stamped-out.pdf");
        await using (document)
        {
            document.Session.Apply(new NexusPdf.Domain.AddOverlayOperation(0,
                new NexusPdf.Pdf.Abstractions.TextOverlay("Проверено", 50, 50, 12, 0xFF000000, 0)));

            // Прямой путь недоступен (правка есть), но структура страниц цела —
            // значит правка ложится на страницу, а не пересобирает документ.
            Assert.False(SaveService.CanSaveDirect(document));
            Assert.True(SaveService.CanSaveDirectWithOverlays(document));

            await new SaveService(_engine).SaveAsAsync(
                document, target, keepBackup: false, CancellationToken.None);
        }

        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, await reopened.GetFormTypeAsync(CancellationToken.None));
        Assert.True(await reopened.InitFormsAsync(CancellationToken.None));

        // И сама надпись на месте — путь не должен «сохранять форму» ценой
        // потери того, ради чего сохраняли.
        var text = await reopened.GetPageTextAsync(0, CancellationToken.None);
        Assert.Contains("Проверено", text);
    }

    /// <summary>
    /// Поле, заполненное в этом же сеансе, переживает добавление надписи:
    /// значения живут в документе в памяти, и сохранять надо ЕГО, а не копию
    /// с диска.
    /// </summary>
    [Fact]
    public async Task Filled_Value_Survives_Adding_Text()
    {
        var path = PdfFixture.WriteTextFieldToTemp("filled-stamp.pdf", "name1");
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "filled-stamp-out.pdf");
        await using (document)
        {
            var handle = document.PrimaryHandle;
            Assert.True(await handle.InitFormsAsync(CancellationToken.None));
            await handle.FormClickAsync(0, 0, 250, 172, CancellationToken.None);
            foreach (var c in "Ivan")
                await handle.FormCharAsync(c, CancellationToken.None);

            document.Session.Apply(new NexusPdf.Domain.AddOverlayOperation(0,
                new NexusPdf.Pdf.Abstractions.TextOverlay("Проверено", 50, 50, 12, 0xFF000000, 0)));

            await new SaveService(_engine).SaveAsAsync(
                document, target, keepBackup: false, CancellationToken.None);
        }

        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        var widget = Assert.Single(
            await reopened.GetAnnotationsAsync(0, CancellationToken.None), a => a.Subtype == 20);
        Assert.Equal("Ivan", widget.Value);
    }
}
