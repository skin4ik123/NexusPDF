using System.Runtime.InteropServices;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Пересжатие изображений: уменьшение до целевого DPI + JPEG на место потока.</summary>
public sealed class RecompressTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    /// <summary>JPEG через System.Drawing (тест-проект не имеет WPF-кодеков).</summary>
    private static byte[] EncodeJpeg(byte[] bgra, int width, int height, int quality)
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
            System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    /// <summary>Шумное изображение (flate его не жмёт) с заданным эффективным DPI на странице.</summary>
    private static ImagePageSpec NoisyPage(int pixelWidth, int pixelHeight, double dpi)
    {
        var bgra = new byte[pixelWidth * pixelHeight * 4];
        uint seed = 12345;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            seed = seed * 1664525 + 1013904223; // детерминированный LCG
            bgra[i] = (byte)seed;
            bgra[i + 1] = (byte)(seed >> 8);
            bgra[i + 2] = (byte)(seed >> 16);
            bgra[i + 3] = 0xFF;
        }
        return new ImagePageSpec(
            bgra, pixelWidth, pixelHeight,
            pixelWidth / dpi * 72.0, pixelHeight / dpi * 72.0);
    }

    [Fact]
    public async Task HighDpi_Image_Is_Recompressed_And_File_Shrinks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "noisy300.pdf");
        await _pdfium.CreateImageDocumentAsync(
            new[] { NoisyPage(1200, 1600, 300) }, source, CancellationToken.None);
        var before = new FileInfo(source).Length;

        var target = Path.Combine(dir, "compressed.pdf");
        var stats = await _pdfium.RecompressImagesAsync(
            source, null, target, 100,
            (bgra, w, h) => EncodeJpeg(bgra, w, h, 75), CancellationToken.None);

        Assert.Equal(1, stats.Recompressed);
        var after = new FileInfo(target).Length;
        Assert.True(after < before / 2,
            $"Файл должен ужаться минимум вдвое: {before} → {after}");

        // Результат — валидный PDF: страница открывается и рендерится не белой.
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, reopened.Info.PageCount);
        var render = await reopened.RenderPageAsync(0, 200, 267, 0, CancellationToken.None);
        var center = (133 * render.Stride) + (100 * 4);
        var isWhite = render.Bgra[center] > 250 && render.Bgra[center + 1] > 250 && render.Bgra[center + 2] > 250;
        Assert.False(isWhite, "Центр страницы не должен быть белым — изображение обязано остаться.");
    }

    [Fact]
    public async Task LowDpi_Image_Is_Left_Untouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "noisy96.pdf");
        await _pdfium.CreateImageDocumentAsync(
            new[] { NoisyPage(400, 500, 96) }, source, CancellationToken.None);

        var target = Path.Combine(dir, "untouched.pdf");
        var stats = await _pdfium.RecompressImagesAsync(
            source, null, target, 150,
            (bgra, w, h) => EncodeJpeg(bgra, w, h, 75), CancellationToken.None);

        // 96 DPI ниже целевых 150 — пересжимать нечего.
        Assert.Equal(0, stats.Recompressed);
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, reopened.Info.PageCount);
    }
}
