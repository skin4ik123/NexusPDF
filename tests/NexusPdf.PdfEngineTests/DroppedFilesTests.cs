using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Файлы, перетащенные из Проводника в режим систематизации. Проверяется то,
/// чего пользователь ждёт: страницы встают ИМЕННО туда, куда он отпустил мышь;
/// картинки становятся страницами; негодный файл не рушит всю вставку, а
/// честно называется в списке пропущенных.
/// </summary>
public sealed class DroppedFilesTests : IAsyncLifetime
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

    private static ImagePageSpec Solid(byte tint)
    {
        var bgra = new byte[120 * 160 * 4];
        for (long o = 0; o + 3 < bgra.Length; o += 4)
        {
            bgra[o] = tint;
            bgra[o + 1] = 128;
            bgra[o + 2] = 200;
            bgra[o + 3] = 255;
        }
        return new ImagePageSpec(bgra, 120, 160, 595, 842);
    }

    private async Task<string> MakePdfAsync(string dir, string name, int pages, byte tint)
    {
        var path = Path.Combine(dir, name);
        await _pdfium.CreateImageDocumentAsync(
            Enumerable.Range(0, pages).Select(_ => Solid(tint)).ToList(), path, CancellationToken.None);
        return path;
    }

    /// <summary>Заглушка декодера картинок: тесты не тянут WPF-кодеки.</summary>
    private static ImagePageSpec FakeDecode(string path) => Solid(90);

    [Fact]
    public async Task Dropped_Pdf_Pages_Land_At_The_Drop_Position()
    {
        var dir = NewDir();
        var basePath = await MakePdfAsync(dir, "base.pdf", 4, 30);
        var dropped = await MakePdfAsync(dir, "dropped.pdf", 2, 220);

        await using var doc = await OpenedDocument.OpenAsync(_pdfium, basePath, null, CancellationToken.None);
        var result = await doc.InsertFilesAsync(
            _pdfium, new[] { dropped }, insertIndex: 2, FakeDecode, dir, null, CancellationToken.None);

        Assert.Equal(2, result.PagesAdded);
        Assert.Equal(1, result.FilesUsed);
        Assert.Empty(result.Skipped);
        Assert.Equal(6, doc.Session.Model.Pages.Count);

        // Вставка ровно на третью позицию, а не в конец.
        var sourceOfDropped = doc.Session.Model.Sources
            .First(p => string.Equals(p.Value, dropped, StringComparison.OrdinalIgnoreCase)).Key;
        Assert.Equal(sourceOfDropped, doc.Session.Model.Pages[2].SourceId);
        Assert.Equal(sourceOfDropped, doc.Session.Model.Pages[3].SourceId);
        Assert.NotEqual(sourceOfDropped, doc.Session.Model.Pages[4].SourceId);
    }

    [Fact]
    public async Task Several_Files_Go_In_The_Order_They_Were_Given()
    {
        var dir = NewDir();
        var basePath = await MakePdfAsync(dir, "b.pdf", 1, 30);
        var first = await MakePdfAsync(dir, "first.pdf", 1, 100);
        var second = await MakePdfAsync(dir, "second.pdf", 1, 200);

        await using var doc = await OpenedDocument.OpenAsync(_pdfium, basePath, null, CancellationToken.None);
        await doc.InsertFilesAsync(_pdfium, new[] { first, second }, 0, FakeDecode, dir, null, CancellationToken.None);

        var firstId = doc.Session.Model.Sources.First(p => p.Value == first).Key;
        var secondId = doc.Session.Model.Sources.First(p => p.Value == second).Key;
        Assert.Equal(firstId, doc.Session.Model.Pages[0].SourceId);
        Assert.Equal(secondId, doc.Session.Model.Pages[1].SourceId);
    }

    [Fact]
    public async Task Images_Become_Pages()
    {
        var dir = NewDir();
        var basePath = await MakePdfAsync(dir, "base.pdf", 1, 30);
        // Файлы должны существовать: расширение решает, как их читать.
        var png = Path.Combine(dir, "снимок.png");
        await File.WriteAllBytesAsync(png, new byte[] { 1, 2, 3 });
        var jpg = Path.Combine(dir, "фото.jpg");
        await File.WriteAllBytesAsync(jpg, new byte[] { 1, 2, 3 });

        await using var doc = await OpenedDocument.OpenAsync(_pdfium, basePath, null, CancellationToken.None);
        var result = await doc.InsertFilesAsync(
            _pdfium, new[] { png, jpg }, 1, FakeDecode, dir, null, CancellationToken.None);

        Assert.Equal(2, result.PagesAdded);
        Assert.Equal(3, doc.Session.Model.Pages.Count);
        Assert.Empty(result.Skipped);
    }

    /// <summary>
    /// Картинки и PDF вперемешку сохраняют порядок, в котором их дали: человек
    /// выделил файлы в нужной последовательности и вправе её увидеть.
    /// </summary>
    [Fact]
    public async Task Images_And_Pdfs_Keep_The_Given_Order()
    {
        var dir = NewDir();
        var basePath = await MakePdfAsync(dir, "base.pdf", 1, 30);
        var picture = Path.Combine(dir, "первая.png");
        await File.WriteAllBytesAsync(picture, new byte[] { 1, 2, 3 });
        var pdf = await MakePdfAsync(dir, "второй.pdf", 1, 210);

        await using var doc = await OpenedDocument.OpenAsync(_pdfium, basePath, null, CancellationToken.None);
        await doc.InsertFilesAsync(
            _pdfium, new[] { picture, pdf }, 0, FakeDecode, dir, null, CancellationToken.None);

        var pdfSource = doc.Session.Model.Sources.First(p => p.Value == pdf).Key;
        // Картинка была первой в списке — она и должна оказаться первой.
        Assert.NotEqual(pdfSource, doc.Session.Model.Pages[0].SourceId);
        Assert.Equal(pdfSource, doc.Session.Model.Pages[1].SourceId);
    }

    [Fact]
    public async Task A_Bad_File_Is_Skipped_And_The_Rest_Are_Inserted()
    {
        var dir = NewDir();
        var basePath = await MakePdfAsync(dir, "base.pdf", 1, 30);
        var good = await MakePdfAsync(dir, "good.pdf", 2, 200);
        var broken = Path.Combine(dir, "битый.pdf");
        await File.WriteAllTextAsync(broken, "это не PDF");
        var alien = Path.Combine(dir, "заметки.txt");
        await File.WriteAllTextAsync(alien, "текст");

        await using var doc = await OpenedDocument.OpenAsync(_pdfium, basePath, null, CancellationToken.None);
        var result = await doc.InsertFilesAsync(
            _pdfium, new[] { broken, good, alien }, 1, FakeDecode, dir, null, CancellationToken.None);

        Assert.Equal(2, result.PagesAdded);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Skipped, x => x.File == broken);
        Assert.Contains(result.Skipped, x => x.File == alien && x.Reason.Contains("не PDF"));
        Assert.Equal(3, doc.Session.Model.Pages.Count);
    }

    [Fact]
    public async Task Everything_Dropped_Undoes_In_One_Step()
    {
        var dir = NewDir();
        var basePath = await MakePdfAsync(dir, "base.pdf", 2, 30);
        var one = await MakePdfAsync(dir, "one.pdf", 2, 100);
        var two = await MakePdfAsync(dir, "two.pdf", 3, 200);

        await using var doc = await OpenedDocument.OpenAsync(_pdfium, basePath, null, CancellationToken.None);
        await doc.InsertFilesAsync(_pdfium, new[] { one, two }, 2, FakeDecode, dir, null, CancellationToken.None);
        Assert.Equal(7, doc.Session.Model.Pages.Count);

        doc.Session.Undo();
        Assert.Equal(2, doc.Session.Model.Pages.Count);
    }
}
