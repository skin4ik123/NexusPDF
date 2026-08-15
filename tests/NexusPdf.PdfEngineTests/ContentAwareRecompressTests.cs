using System.Runtime.InteropServices;
using System.Text;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Пересжатие по содержимому: схема обязана остаться без потерь и без ореолов,
/// фотография — уйти в JPEG. Это ровно то, чего нельзя проверить на глаз в
/// одном файле, и ровно то, ради чего выбор кодека вообще написан.
/// </summary>
public sealed class ContentAwareRecompressTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _pdfium = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    /// <summary>JPEG через System.Drawing (тест-проект не имеет WPF-кодеков).</summary>
    private static byte[] EncodeJpeg(byte[] bgra, int width, int height, ImageEncodingChoice choice)
    {
        using var bitmap = new System.Drawing.Bitmap(
            width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
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

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
                   ms, System.IO.Compression.CompressionLevel.Optimal, true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>Схема: плоские заливки и резкие границы — случай для Flate.</summary>
    private static byte[] Diagram(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 3;
            var dark = (x / 50 + y / 50) % 2 == 0;
            rgb[o] = (byte)(dark ? 0x1F : 0xFF);
            rgb[o + 1] = (byte)(dark ? 0x6F : 0xFF);
            rgb[o + 2] = (byte)(dark ? 0xEB : 0xFF);
        }
        return rgb;
    }

    /// <summary>Фотография: градиент плюс шум сенсора.</summary>
    private static byte[] PhotoRgb(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        uint seed = 4242;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            seed = seed * 1664525 + 1013904223;
            var o = (y * w + x) * 3;
            rgb[o] = (byte)Math.Clamp(x / 3 + (int)((seed >> 24) % 40), 0, 255);
            rgb[o + 1] = (byte)Math.Clamp(y / 3 + (int)((seed >> 16) % 40), 0, 255);
            rgb[o + 2] = (byte)Math.Clamp((x + y) / 6 + (int)((seed >> 8) % 40), 0, 255);
        }
        return rgb;
    }

    /// <summary>PDF из одного изображения 600x600, размещённого на 144 pt (300 DPI).</summary>
    private static string WritePdf(string dir, string name, byte[] rgb)
    {
        var compressed = Compress(rgb);
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

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Diagram_Is_Kept_Lossless_And_Still_Shrinks()
    {
        var dir = NewDir();
        var source = WritePdf(dir, "diagram.pdf", Diagram(600, 600));
        var target = Path.Combine(dir, "diagram-out.pdf");

        var stats = await _pdfium.RecompressImagesAsync(
            source, null, target, 150, 75, EncodeJpeg, CancellationToken.None);

        Assert.Equal(1, stats.Recompressed);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(target));
        Assert.DoesNotContain("DCTDecode", text);   // JPEG к схеме не применён
        Assert.Contains("FlateDecode", text);       // и она всё-таки сжата
        Assert.True(new FileInfo(target).Length < new FileInfo(source).Length,
            $"Схема без потерь обязана уменьшиться: {new FileInfo(source).Length} → {new FileInfo(target).Length}.");
    }

    [Fact]
    public async Task Diagram_Keeps_Its_Flat_Fills_Exactly()
    {
        var dir = NewDir();
        var source = WritePdf(dir, "flat.pdf", Diagram(600, 600));
        var target = Path.Combine(dir, "flat-out.pdf");
        await _pdfium.RecompressImagesAsync(
            source, null, target, 150, 75, EncodeJpeg, CancellationToken.None);

        await using var doc = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var render = await doc.RenderPageAsync(0, 600, 600, 0, CancellationToken.None);

        // Пиксель изображения → пиксель растра страницы. Картинка 600 px лежит
        // на 144 pt от (50,50), верх изображения — это y = 194 pt снизу;
        // страница 300 pt отрисована в 600 px, то есть 2 px на point.
        (int X, int Y) At(int imageX, int imageY)
        {
            var xPt = 50 + 144.0 * imageX / 600;
            var yPt = 194 - 144.0 * imageY / 600;
            return ((int)(xPt * 2), (int)((300 - yPt) * 2));
        }

        // Клетка (1,0) белая, клетка (1,1) — синяя; центры в 25 px от границ.
        var (wx, wy) = At(75, 25);
        var white = wy * render.Stride + wx * 4;
        Assert.True(render.Bgra[white] > 250 && render.Bgra[white + 1] > 250 && render.Bgra[white + 2] > 250,
            $"Белая заливка обязана остаться белой без ореолов: BGR " +
            $"{render.Bgra[white]},{render.Bgra[white + 1]},{render.Bgra[white + 2]}.");

        var (bx, by) = At(75, 75);
        var blue = by * render.Stride + bx * 4;
        Assert.Equal(0xEB, render.Bgra[blue]);      // B
        Assert.Equal(0x6F, render.Bgra[blue + 1]);  // G
        Assert.Equal(0x1F, render.Bgra[blue + 2]);  // R — цвет ровно исходный, без сдвига
    }

    [Fact]
    public async Task Photograph_Still_Goes_Through_Jpeg()
    {
        var dir = NewDir();
        var source = WritePdf(dir, "photo.pdf", PhotoRgb(600, 600));
        var target = Path.Combine(dir, "photo-out.pdf");

        var stats = await _pdfium.RecompressImagesAsync(
            source, null, target, 150, 75, EncodeJpeg, CancellationToken.None);

        Assert.Equal(1, stats.Recompressed);
        Assert.Contains("DCTDecode", Encoding.Latin1.GetString(File.ReadAllBytes(target)));
        Assert.True(new FileInfo(target).Length < new FileInfo(source).Length / 2,
            "Фотография обязана ужаться минимум вдвое.");
    }
}
