using System.Text;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.MuPdf;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Сжатие через MuPDF. Проверяется поведение, за которое движок и взят:
/// файл уменьшается, страницы остаются на месте и читаются, а если выигрыша
/// нет — отдаётся исходник, а не «оптимизированный» файл побольше.
/// </summary>
public sealed class MuPdfCompressionTests : IAsyncLifetime
{
    private readonly MuPdfCompressionEngine _mupdf = new();
    private readonly PdfiumRenderEngine _pdfium = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
                   ms, System.IO.Compression.CompressionLevel.Optimal, true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>Фотография 600x600, размещённая на 144 pt — 300 DPI, есть что уменьшать.</summary>
    private static string WritePhotoPdf(string dir, string name)
    {
        var rgb = new byte[600 * 600 * 3];
        uint seed = 4242;
        for (var i = 0; i < rgb.Length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            rgb[i] = (byte)(seed >> 16);
        }
        var compressed = Deflate(rgb);
        var image = "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 600 /Height 600 " +
                    "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
                    $"/Length {compressed.Length} >>\nstream\n{Encoding.Latin1.GetString(compressed)}\nendstream\nendobj\n";
        var content = "q 144 0 0 144 50 50 cm /Im1 Do Q";
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
                  "/Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n" +
                  $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n" +
                  image + "trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n";
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(raw));
        return path;
    }

    [Fact]
    public void Engine_Is_Available()
    {
        Assert.True(_mupdf.IsAvailable,
            $"Нативная библиотека MuPDF обязана быть рядом: {_mupdf.UnavailableReason}");
    }

    [Fact]
    public async Task Photo_Document_Shrinks_And_Stays_Readable()
    {
        var dir = NewDir();
        var source = WritePhotoPdf(dir, "photo.pdf");
        var target = Path.Combine(dir, "photo-out.pdf");

        var result = await _mupdf.CompressAsync(source, target,
            new PdfCompressionRequest(150, 75, StructureOnly: false, SubsetFonts: true),
            CancellationToken.None);

        Assert.False(result.KeptOriginal);
        Assert.True(result.BytesAfter < result.BytesBefore / 2,
            $"Фотография 300 DPI обязана ужаться вдвое: {result.BytesBefore} → {result.BytesAfter}.");

        // Файл читается посторонним движком — значит это валидный PDF, а не
        // «получилось меньше, зато не открывается».
        await using var doc = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, doc.Info.PageCount);
        // Проба берётся в СЕРЕДИНЕ картинки (она лежит на 50..194 pt страницы
        // в 300 pt, растр 300 px = 1 px на point), а не в белом поле сверху.
        var render = await doc.RenderPageAsync(0, 300, 300, 0, CancellationToken.None);
        var o = (300 - 122) * render.Stride + 122 * 4;
        Assert.True(render.Bgra[o] != 0xFF || render.Bgra[o + 1] != 0xFF || render.Bgra[o + 2] != 0xFF,
            "На месте фотографии не должно быть белого листа.");
    }

    [Fact]
    public async Task Structure_Only_Leaves_The_Image_Alone()
    {
        var dir = NewDir();
        var source = WritePhotoPdf(dir, "keep.pdf");
        var target = Path.Combine(dir, "keep-out.pdf");

        var result = await _mupdf.CompressAsync(source, target,
            new PdfCompressionRequest(150, 75, StructureOnly: true, SubsetFonts: false),
            CancellationToken.None);

        // Картинку не трогали: заметного выигрыша быть не может, и движок
        // обязан честно отдать исходник вместо пересобранного файла.
        Assert.True(result.KeptOriginal || result.BytesAfter > result.BytesBefore / 2,
            $"Без пересжатия изображений файл не может ужаться вдвое: {result.BytesBefore} → {result.BytesAfter}.");
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task An_Already_Tight_File_Is_Returned_As_Is()
    {
        var dir = NewDir();
        var source = WritePhotoPdf(dir, "tight.pdf");
        var once = Path.Combine(dir, "once.pdf");
        var twice = Path.Combine(dir, "twice.pdf");
        var request = new PdfCompressionRequest(150, 75, false, true);

        await _mupdf.CompressAsync(source, once, request, CancellationToken.None);
        var second = await _mupdf.CompressAsync(once, twice, request, CancellationToken.None);

        // Второй заход по тому же файлу отдавать не должен почти ничего:
        // повторное сжатие только теряет качество.
        Assert.True(second.KeptOriginal || second.BytesAfter >= second.BytesBefore * 0.985,
            $"Повторное сжатие не должно давать выигрыш: {second.BytesBefore} → {second.BytesAfter}.");
        Assert.Equal(new FileInfo(twice).Length, second.BytesAfter);
    }

    [Fact]
    public async Task A_Password_Protected_File_Is_Refused_Not_Silently_Unlocked()
    {
        var dir = NewDir();
        var plain = WritePhotoPdf(dir, "plain.pdf");
        var locked = Path.Combine(dir, "locked.pdf");
        var qpdf = new NexusPdf.Pdf.Qpdf.QpdfEngine();
        if (!qpdf.IsAvailable) return; // без qpdf защищённый файл не сделать
        await qpdf.EncryptAsync(plain, locked, "тайна", null, CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => _mupdf.CompressAsync(
            locked, Path.Combine(dir, "out.pdf"),
            new PdfCompressionRequest(150, 75, false, false), CancellationToken.None));
    }
}
