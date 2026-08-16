using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.MuPdf;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Обработка ОТКРЫТОГО документа: чистка, пересжатие и оптимизация одним
/// конвейером, результат которого попадает во вкладку, а не в новый файл.
///
/// Проверяется то, ради чего это и делалось: файл на диске не меняется, пока
/// пользователь не сохранит; документ становится изменённым; «Отменить»
/// возвращает как было; и — главное — порядок шагов соблюдается, потому что
/// чистка кладёт растры несжатыми и без последующего пересжатия документ
/// раздувается.
/// </summary>
public sealed class ProcessInPlaceTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _pdfium = new();
    private readonly QpdfEngine _qpdf = new();
    private readonly MuPdfCompressionEngine _mupdf = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private DocumentToolsService Tools => new(_pdfium, _qpdf, _qpdf, _mupdf);

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Скан: серая неровно освещённая бумага со строками «текста» и пылью.
    /// Именно такой лист чистка обязана превратить в белый.
    /// </summary>
    private static ImagePageSpec ScanPage(int width = 1000, int height = 1400)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var o = ((long)y * width + x) * 4;
            // Жёлто-серая бумага с тенью слева.
            var shade = 158 + 50.0 * x / width;
            bgra[o] = (byte)(shade * 0.86);      // B
            bgra[o + 1] = (byte)(shade * 0.96);  // G
            bgra[o + 2] = (byte)shade;           // R
            bgra[o + 3] = 255;
        }
        for (var line = 0; line < 26; line++)
        for (var thickness = 0; thickness < 10; thickness++)
        for (var x = 120; x < width - 120; x++)
        {
            if ((x / 45) % 5 == 4) continue;
            var o = ((long)(90 + line * 46 + thickness) * width + x) * 4;
            bgra[o] = bgra[o + 1] = bgra[o + 2] = 25;
        }
        for (var i = 0; i < 60; i++)
        {
            var o = ((long)(1300 + i % 7) * width + 40 + i * 15) * 4;
            bgra[o] = bgra[o + 1] = bgra[o + 2] = 20;
        }
        return new ImagePageSpec(bgra, width, height, 595, 842);
    }

    private async Task<string> MakeScanAsync(string dir, string name, int pages = 2)
    {
        var specs = Enumerable.Range(0, pages).Select(_ => ScanPage()).ToList();
        var path = Path.Combine(dir, name);
        await _pdfium.CreateImageDocumentAsync(specs, path, CancellationToken.None);
        return path;
    }

    [Fact]
    public async Task Processing_Changes_The_Open_Document_And_Leaves_The_File_Alone()
    {
        var dir = NewDir();
        var path = await MakeScanAsync(dir, "scan.pdf");
        var onDisk = File.ReadAllBytes(path);

        await using var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        var sourceBefore = document.Session.Model.Pages[0].SourceId;

        var result = await Tools.ProcessInPlaceAsync(
            document,
            new ProcessingPlan(new ScanEnhanceOptions(LevelBackground: true), null, false),
            dir, ImageEncoder, null, CancellationToken.None);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, document.Session.Model.Pages.Count);
        // Страницы переехали на другой источник, документ стал изменённым.
        Assert.NotEqual(sourceBefore, document.Session.Model.Pages[0].SourceId);
        Assert.True(document.Session.IsDirty);
        // А файл пользователя не тронут: сохранять его никто не просил.
        Assert.Equal(onDisk, File.ReadAllBytes(path));
        Assert.Equal(path, document.Session.FilePath);
    }

    [Fact]
    public async Task Undo_Brings_The_Document_Back()
    {
        var dir = NewDir();
        var path = await MakeScanAsync(dir, "undo.pdf");

        await using var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        var before = document.Session.Model.Pages.ToList();

        await Tools.ProcessInPlaceAsync(
            document,
            new ProcessingPlan(new ScanEnhanceOptions(LevelBackground: true), null, false),
            dir, ImageEncoder, null, CancellationToken.None);
        Assert.NotEqual(before[0].SourceId, document.Session.Model.Pages[0].SourceId);

        Assert.True(document.Session.History.CanUndo);
        document.Session.Undo();

        Assert.Equal(before.Count, document.Session.Model.Pages.Count);
        Assert.Equal(before[0].SourceId, document.Session.Model.Pages[0].SourceId);
        // Прежний источник остался открытым — страницы обязаны рисоваться.
        var image = await document.RenderLogicalPageAsync(0, 120, 170, CancellationToken.None);
        Assert.Equal(120, image.PixelWidth);
    }

    /// <summary>
    /// Очередь шагов — не мелочь. Чистка возвращает растры в PDF без сжатия,
    /// поэтому «почистить и на этом закончить» даёт файл в разы тяжелее
    /// исходного. Пересжатие ПОСЛЕ неё это исправляет; порядок наоборот
    /// невозможен по построению конвейера, и тест закрепляет именно выигрыш.
    /// </summary>
    [Fact]
    public async Task Cleaning_Alone_Inflates_The_File_And_Compression_After_It_Does_Not()
    {
        var dir = NewDir();
        var enhance = new ScanEnhanceOptions(Deskew: false, Despeckle: true, LevelBackground: true);
        var compress = new PdfCompressionRequest(150, 75, false, true);

        long CleanedOnly;
        var pathA = await MakeScanAsync(dir, "a.pdf");
        var original = new FileInfo(pathA).Length;
        await using (var a = await OpenedDocument.OpenAsync(_pdfium, pathA, null, CancellationToken.None))
        {
            var r = await Tools.ProcessInPlaceAsync(
                a, new ProcessingPlan(enhance, null, false), dir, ImageEncoder, null, CancellationToken.None);
            CleanedOnly = r.BytesAfter;
        }

        var pathB = await MakeScanAsync(dir, "b.pdf");
        long cleanedAndCompressed;
        await using (var b = await OpenedDocument.OpenAsync(_pdfium, pathB, null, CancellationToken.None))
        {
            var r = await Tools.ProcessInPlaceAsync(
                b, new ProcessingPlan(enhance, compress, false), dir, ImageEncoder, null, CancellationToken.None);
            cleanedAndCompressed = r.BytesAfter;
        }

        Assert.True(cleanedAndCompressed < CleanedOnly,
            $"Пересжатие после чистки не помогло: только чистка {CleanedOnly} Б, с пересжатием {cleanedAndCompressed} Б.");
        Assert.True(cleanedAndCompressed < original * 3,
            $"Обработанный документ раздулся: было {original} Б, стало {cleanedAndCompressed} Б.");
    }

    /// <summary>Пустой план — это ошибка вызова, а не тихое ничего.</summary>
    [Fact]
    public async Task An_Empty_Plan_Is_Refused()
    {
        var dir = NewDir();
        var path = await MakeScanAsync(dir, "empty.pdf", 1);
        await using var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tools.ProcessInPlaceAsync(document, new ProcessingPlan(), dir, ImageEncoder, null, CancellationToken.None));
    }

    /// <summary>Обработанный документ сохраняется в настоящий читаемый PDF.</summary>
    [Fact]
    public async Task The_Processed_Document_Saves_Into_A_Readable_Pdf()
    {
        var dir = NewDir();
        var path = await MakeScanAsync(dir, "save.pdf");
        var saved = Path.Combine(dir, "saved.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None))
        {
            await Tools.ProcessInPlaceAsync(
                document,
                new ProcessingPlan(new ScanEnhanceOptions(LevelBackground: true),
                    new PdfCompressionRequest(150, 75, false, true), _qpdf.IsAvailable),
                dir, ImageEncoder, null, CancellationToken.None);
            await new SaveService(_pdfium).SaveAsAsync(document, saved, keepBackup: false, CancellationToken.None);
        }

        await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        Assert.Equal(2, reopened.Info.PageCount);

        // Бумага после чистки обязана быть белой, а не серо-жёлтой.
        var render = await reopened.RenderPageAsync(0, 400, 560, 0, CancellationToken.None);
        var corner = render.Bgra[((long)20 * 400 + 20) * 4];
        Assert.True(corner > 235, $"Бумага осталась серой: {corner}.");
    }

    /// <summary>Кодек запасного пути: JPEG через System.Drawing (в тестах нет WPF).</summary>
    private static byte[] ImageEncoder(byte[] bgra, int width, int height, ImageEncodingChoice choice)
    {
        using var bitmap = new System.Drawing.Bitmap(
            width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        bitmap.UnlockBits(data);

        using var stream = new MemoryStream();
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)choice.Quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }
}
