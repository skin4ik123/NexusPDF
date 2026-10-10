using System.Text;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.MuPdf;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;

namespace NexusPdf.PdfEngineTests;

public sealed class ReportedPdfRegressionTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _pdfium = new();
    private readonly MuPdfCompressionEngine _mupdf = new();
    private readonly QpdfEngine _qpdf = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Pixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        uint seed = 4242;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            seed = seed * 1664525 + 1013904223;
            pixels[i] = (byte)(seed >> 16);
            pixels[i + 1] = (byte)(seed >> 8);
            pixels[i + 2] = (byte)seed;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static byte[] Jpeg(byte[] pixels, int width, int height, int quality)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bitmap.UnlockBits(data);
        using var stream = new MemoryStream();
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    [Fact]
    public async Task Jpeg_At_Target_Dpi_Can_Still_Be_Compressed_By_Quality()
    {
        var dir = NewDir();
        var jpeg = Jpeg(Pixels(600, 600), 600, 600, 95);
        var contents = "q 600 0 0 600 0 0 cm /Im1 Do Q";
        var pdf = "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] " +
            "/Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n" +
            $"4 0 obj\n<< /Length {contents.Length} >>\nstream\n{contents}\nendstream\nendobj\n" +
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 600 /Height 600 " +
            "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode " +
            $"/Length {jpeg.Length} >>\nstream\n{Encoding.Latin1.GetString(jpeg)}\nendstream\nendobj\n" +
            "trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n";
        var source = Path.Combine(dir, "photo.pdf");
        var target = Path.Combine(dir, "compressed.pdf");
        File.WriteAllBytes(source, Encoding.Latin1.GetBytes(pdf));
        var result = await _mupdf.CompressAsync(source, target,
            new PdfCompressionRequest(150, 35, false, false), CancellationToken.None);
        Assert.True(result.Recompressed > 0, "The selected JPEG quality was ignored.");
        Assert.True(result.BytesAfter < result.BytesBefore * 0.85);
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        AssertVisible(await reopened.RenderPageAsync(0, 120, 120, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Repeating_Optimization_Preserves_Images_And_Undo(bool clean, bool compress)
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "source.pdf");
        var spec = new ImagePageSpec(Pixels(600, 800), 600, 800, 300, 400);
        await _pdfium.CreateImageDocumentAsync(new[] { spec }, source, CancellationToken.None);
        await using var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None);
        var tools = new DocumentToolsService(_pdfium, _qpdf, _qpdf, _mupdf);
        var plan = new ProcessingPlan(
            clean ? new ScanEnhanceOptions(Deskew: false, Despeckle: false, LevelBackground: true) : null,
            compress ? new PdfCompressionRequest(150, 60, false, false) : null,
            OptimizeStructure: true);
        for (var pass = 0; pass < 3; pass++)
        {
            await tools.ProcessInPlaceAsync(document, plan, dir,
                (pixels, width, height, choice) => Jpeg(pixels, width, height, choice.Quality),
                null, CancellationToken.None);
            AssertVisible(await document.RenderLogicalPageAsync(0, 120, 160, CancellationToken.None));
        }
        document.Session.Undo();
        AssertVisible(await document.RenderLogicalPageAsync(0, 120, 160, CancellationToken.None));
        var saved = Path.Combine(dir, "saved.pdf");
        await new SaveService(_pdfium).SaveCopyAsync(document, saved, CancellationToken.None);
        await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        AssertVisible(await reopened.RenderPageAsync(0, 120, 160, 0, CancellationToken.None));
    }

    private static void AssertVisible(RenderedPageImage image)
    {
        var nonWhite = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4)
            if (image.Bgra[i] < 235 || image.Bgra[i + 1] < 235 || image.Bgra[i + 2] < 235)
                nonWhite++;
        Assert.True(nonWhite > image.PixelWidth * image.PixelHeight / 10,
            "Optimization replaced the page content with a blank page.");
    }
}
