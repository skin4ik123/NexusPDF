using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Перенос страниц из одного открытого документа в другой — то, что происходит,
/// когда пользователь тащит страницы на вкладку соседа. Проверяется главное:
/// страницы приходят целыми, исходный документ не страдает, а результат
/// сохраняется в настоящий PDF.
/// </summary>
public sealed class CrossDocumentPagesTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _pdfium = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Документ из цветных страниц: по оттенку видно, откуда пришла страница.</summary>
    private async Task<string> MakeDocumentAsync(string dir, string name, int pages, byte tint)
    {
        var specs = new List<ImagePageSpec>();
        for (var i = 0; i < pages; i++)
        {
            var bgra = new byte[200 * 280 * 4];
            for (long o = 0; o + 3 < bgra.Length; o += 4)
            {
                bgra[o] = tint;
                bgra[o + 1] = (byte)(120 + i * 20);
                bgra[o + 2] = 240;
                bgra[o + 3] = 255;
            }
            specs.Add(new ImagePageSpec(bgra, 200, 280, 595, 842));
        }
        var path = Path.Combine(dir, name);
        await _pdfium.CreateImageDocumentAsync(specs, path, CancellationToken.None);
        return path;
    }

    [Fact]
    public async Task Pages_Arrive_In_The_Target_And_The_Source_Is_Untouched()
    {
        var dir = NewDir();
        var targetPath = await MakeDocumentAsync(dir, "target.pdf", 3, 40);
        var sourcePath = await MakeDocumentAsync(dir, "source.pdf", 4, 200);

        await using var target = await OpenedDocument.OpenAsync(_pdfium, targetPath, null, CancellationToken.None);
        await using var source = await OpenedDocument.OpenAsync(_pdfium, sourcePath, null, CancellationToken.None);

        var inserted = await target.InsertPagesFromAsync(
            _pdfium, source, new[] { 1, 2 }, target.Session.Model.Pages.Count, CancellationToken.None);

        Assert.Equal(2, inserted);
        Assert.Equal(5, target.Session.Model.Pages.Count);
        Assert.Equal(4, source.Session.Model.Pages.Count); // источник не тронут
        // Пришедшие страницы ссылаются на ЧУЖОЙ файл, зарегистрированный как
        // второй источник этого документа.
        Assert.Equal(2, target.Session.Model.Sources.Count);
        Assert.Contains(target.Session.Model.Sources.Values,
            v => string.Equals(v, sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rotation_Of_A_Moved_Page_Comes_Along()
    {
        var dir = NewDir();
        var targetPath = await MakeDocumentAsync(dir, "t.pdf", 1, 40);
        var sourcePath = await MakeDocumentAsync(dir, "s.pdf", 2, 200);

        await using var target = await OpenedDocument.OpenAsync(_pdfium, targetPath, null, CancellationToken.None);
        await using var source = await OpenedDocument.OpenAsync(_pdfium, sourcePath, null, CancellationToken.None);

        source.Session.Apply(new RotatePagesOperation(new[] { 0 }, 1));
        await target.InsertPagesFromAsync(_pdfium, source, new[] { 0 }, 1, CancellationToken.None);

        Assert.Equal(1, target.Session.Model.Pages[1].RotationOffset);
    }

    [Fact]
    public async Task The_Same_File_Is_Not_Opened_Twice()
    {
        var dir = NewDir();
        var targetPath = await MakeDocumentAsync(dir, "one.pdf", 2, 40);
        var sourcePath = await MakeDocumentAsync(dir, "two.pdf", 3, 200);

        await using var target = await OpenedDocument.OpenAsync(_pdfium, targetPath, null, CancellationToken.None);
        await using var source = await OpenedDocument.OpenAsync(_pdfium, sourcePath, null, CancellationToken.None);

        await target.InsertPagesFromAsync(_pdfium, source, new[] { 0 }, 2, CancellationToken.None);
        await target.InsertPagesFromAsync(_pdfium, source, new[] { 1 }, 3, CancellationToken.None);

        // Второй перенос из того же файла обязан переиспользовать источник.
        Assert.Equal(2, target.Session.Model.Sources.Count);
        Assert.Equal(4, target.Session.Model.Pages.Count);
    }

    [Fact]
    public async Task The_Result_Saves_Into_A_Readable_Pdf()
    {
        var dir = NewDir();
        var targetPath = await MakeDocumentAsync(dir, "base.pdf", 2, 40);
        var sourcePath = await MakeDocumentAsync(dir, "extra.pdf", 2, 200);
        var savedPath = Path.Combine(dir, "saved.pdf");

        await using (var target = await OpenedDocument.OpenAsync(_pdfium, targetPath, null, CancellationToken.None))
        await using (var source = await OpenedDocument.OpenAsync(_pdfium, sourcePath, null, CancellationToken.None))
        {
            await target.InsertPagesFromAsync(
                _pdfium, source, new[] { 0, 1 }, target.Session.Model.Pages.Count, CancellationToken.None);
            await new SaveService(_pdfium).SaveAsAsync(target, savedPath, keepBackup: false, CancellationToken.None);
        }

        await using var reopened = await _pdfium.OpenAsync(savedPath, null, CancellationToken.None);
        Assert.Equal(4, reopened.Info.PageCount);
        var render = await reopened.RenderPageAsync(3, 100, 140, 0, CancellationToken.None);
        // Последняя страница пришла из ЧУЖОГО документа — её оттенок другой.
        Assert.True(render.Bgra[0] > 150, $"Ожидался оттенок чужого документа, а получено {render.Bgra[0]}.");
    }

    [Fact]
    public async Task Moving_Pages_Into_The_Same_Document_Is_Refused()
    {
        var dir = NewDir();
        var path = await MakeDocumentAsync(dir, "self.pdf", 2, 40);
        await using var doc = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            doc.InsertPagesFromAsync(_pdfium, doc, new[] { 0 }, 1, CancellationToken.None));
    }
}
