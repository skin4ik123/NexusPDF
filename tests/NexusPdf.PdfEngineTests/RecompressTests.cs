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
    private static byte[] EncodeJpeg(byte[] bgra, int width, int height, ImageEncodingChoice choice)
    {
        var quality = choice.Quality;
        if (choice.IsGray)
        {
            // System.Drawing не пишет одноканальный JPEG; для теста достаточно
            // обесцветить пиксели — приложение на WPF кодирует настоящий Gray8.
            bgra = (byte[])bgra.Clone();
            for (var i = 0; i + 2 < bgra.Length; i += 4)
            {
                var luma = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
                bgra[i] = bgra[i + 1] = bgra[i + 2] = luma;
            }
        }
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
            75, EncodeJpeg, CancellationToken.None);

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

    /// <summary>Минимальный PDF с одним изображением, заданным сырыми объектами (для /SMask, ImageMask, матриц).</summary>
    private static string WriteRawImagePdf(string dir, string name, string imageObjects, string contentOps, string resources)
    {
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
                  $"/Contents 4 0 R /Resources << /XObject << {resources} >> >> >>\nendobj\n" +
                  $"4 0 obj\n<< /Length {contentOps.Length} >>\nstream\n{contentOps}\nendstream\nendobj\n" +
                  imageObjects +
                  "trailer\n<< /Size 9 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(raw));
        return path;
    }

    private static string FlateImageObject(int number, int width, int height, byte[] rgb, string extraKeys)
    {
        var compressed = Compress(rgb);
        var body = $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                   "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
                   $"/Length {compressed.Length} {extraKeys} >>";
        return $"{number} 0 obj\n{body}\nstream\n{System.Text.Encoding.Latin1.GetString(compressed)}\nendstream\nendobj\n";
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(
                   output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// Действительно несжимаемый шум. Берётся СТАРШИЙ байт: у младших битов
    /// LCG период 256, и такой «шум» Flate ужимает в сотню раз — пересжатие
    /// на нём проверять нечего.
    /// </summary>
    private static byte[] NoisyRgb(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        uint seed = 777;
        for (var i = 0; i < rgb.Length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            rgb[i] = (byte)(seed >> 16);
        }
        return rgb;
    }

    [Fact]
    public async Task Image_With_SMask_Is_Skipped_Not_Flattened()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Красный RGB 600x600 (300 DPI на 144pt) с ПОЛНОСТЬЮ прозрачной /SMask.
        var red = new byte[600 * 600 * 3];
        for (var i = 0; i < red.Length; i += 3) red[i] = 0xFF;
        var smask = Compress(new byte[600 * 600]); // альфа 0 всюду
        var images =
            FlateImageObject(5, 600, 600, red, "/SMask 6 0 R") +
            $"6 0 obj\n<< /Type /XObject /Subtype /Image /Width 600 /Height 600 " +
            $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {smask.Length} >>\n" +
            $"stream\n{System.Text.Encoding.Latin1.GetString(smask)}\nendstream\nendobj\n";
        var path = WriteRawImagePdf(dir, "smask.pdf", images,
            "q 144 0 0 144 50 50 cm /Im1 Do Q", "/Im1 5 0 R");

        var target = Path.Combine(dir, "smask-out.pdf");
        var stats = await _pdfium.RecompressImagesAsync(path, null, target, 100,
            75, EncodeJpeg, CancellationToken.None);

        // Прозрачное изображение обязано быть пропущено, а не «сплющено».
        Assert.Equal(0, stats.Recompressed);
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var render = await reopened.RenderPageAsync(0, 300, 300, 0, CancellationToken.None);
        var center = (150 * render.Stride) + (120 * 4);
        Assert.True(render.Bgra[center + 1] > 250 && render.Bgra[center + 2] > 250,
            "Полностью прозрачное изображение не должно проявиться на странице.");
    }

    [Fact]
    public async Task Stencil_ImageMask_Is_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 1-битный трафарет 600x600: все биты 0 = закрашивать текущим цветом.
        var bits = Compress(new byte[600 / 8 * 600]);
        var images =
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 600 /Height 600 " +
            $"/ImageMask true /BitsPerComponent 1 /Filter /FlateDecode /Length {bits.Length} >>\n" +
            $"stream\n{System.Text.Encoding.Latin1.GetString(bits)}\nendstream\nendobj\n";
        var path = WriteRawImagePdf(dir, "stencil.pdf", images,
            "q 1 0 0 rg 144 0 0 144 50 50 cm /Im1 Do Q", "/Im1 5 0 R");

        var target = Path.Combine(dir, "stencil-out.pdf");
        var stats = await _pdfium.RecompressImagesAsync(path, null, target, 100,
            75, EncodeJpeg, CancellationToken.None);

        Assert.Equal(0, stats.Recompressed); // трафарет не трогаем
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var render = await reopened.RenderPageAsync(0, 300, 300, 0, CancellationToken.None);
        var center = (150 * render.Stride) + (120 * 4);
        Assert.True(render.Bgra[center + 2] > 200 && render.Bgra[center + 1] < 60,
            "Красный трафарет обязан остаться видимым после пересжатия файла.");
    }

    [Fact]
    public async Task Rotated_Placement_Uses_Matrix_Dpi_Not_Metadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 600x150 px, размещено с поворотом 90°: истинные 300 DPI по обеим осям
        // (метаданные pdfium для такого размещения врут в разы).
        var images = FlateImageObject(5, 600, 150, NoisyRgb(600, 150), "");
        var path = WriteRawImagePdf(dir, "rotated.pdf", images,
            "q 0 144 -36 0 150 50 cm /Im1 Do Q", "/Im1 5 0 R");

        var target = Path.Combine(dir, "rotated-out.pdf");
        var stats = await _pdfium.RecompressImagesAsync(path, null, target, 150,
            75, EncodeJpeg, CancellationToken.None);

        Assert.Equal(1, stats.Recompressed);
        // DPI из матрицы: 600px на 144pt = 300 DPI → цель 150 → уменьшение
        // ровно вдвое (300x75), а не вчетверо+ по врущим метаданным (75x19).
        // Прокси-проверка масштаба: JPEG шума 300x75 весит на порядок больше
        // замыленного 75x19.
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, reopened.Info.PageCount);
        Assert.True(new FileInfo(target).Length > 8 * 1024,
            $"Файл подозрительно мал ({new FileInfo(target).Length} Б) — изображение замылено сильнее цели.");
    }

    [Fact]
    public async Task Shared_Image_On_Two_Pages_Is_Recompressed_Once_For_Largest_Placement()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Одно изображение на двух страницах: крупное (300 DPI) и мелкое.
        var spec = NoisyPage(1200, 1200, 300);
        var source = Path.Combine(dir, "shared.pdf");
        await _pdfium.CreateImageDocumentAsync(new[] { spec, spec with { WidthPoints = 72, HeightPoints = 72 } },
            source, CancellationToken.None);

        var target = Path.Combine(dir, "shared-out.pdf");
        var stats = await _pdfium.RecompressImagesAsync(source, null, target, 150,
            75, EncodeJpeg, CancellationToken.None);

        // Группа одна: счётчик — изображения, не размещения.
        Assert.Equal(1, stats.Recompressed);
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(2, reopened.Info.PageCount);
    }

    [Fact]
    public async Task Small_LowDpi_Image_Is_Left_Untouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "noisy96.pdf");
        await _pdfium.CreateImageDocumentAsync(
            new[] { NoisyPage(120, 100, 96) }, source, CancellationToken.None);

        var target = Path.Combine(dir, "untouched.pdf");
        var stats = await _pdfium.RecompressImagesAsync(
            source, null, target, 150,
            75, EncodeJpeg, CancellationToken.None);

        // Разрешение ниже целевого, поток лёгкий — трогать нечего.
        Assert.Equal(0, stats.Recompressed);
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, reopened.Info.PageCount);
    }

    /// <summary>
    /// Разрешение может быть и в норме, а вес — нет: страница, вставленная как
    /// несжатый растр, весит сотни килобайт при 96 DPI. Такое перекодируется с
    /// запрошенным качеством, хотя уменьшать в размерах нечего.
    /// </summary>
    [Fact]
    public async Task Heavy_LowDpi_Image_Is_Recoded_At_The_Chosen_Quality()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "heavy96.pdf");
        await _pdfium.CreateImageDocumentAsync(
            new[] { NoisyPage(400, 500, 96) }, source, CancellationToken.None);
        var before = new FileInfo(source).Length;

        var target = Path.Combine(dir, "recoded.pdf");
        var stats = await _pdfium.RecompressImagesAsync(
            source, null, target, 150,
            75, EncodeJpeg, CancellationToken.None);

        Assert.Equal(1, stats.Recompressed);
        var after = new FileInfo(target).Length;
        Assert.True(after < before / 2, $"Тяжёлый растр обязан ужаться: {before} → {after}.");

        // Размер в пикселях не тронут — уменьшать было нечего.
        await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, reopened.Info.PageCount);
    }

    /// <summary>
    /// И обратный случай: если перекодирование НЕ делает картинку легче,
    /// исходный поток остаётся на месте. Пересжатие ради потери качества —
    /// не оптимизация.
    /// </summary>
    [Fact]
    public async Task Already_Compact_Image_Is_Not_Replaced_With_A_Bigger_One()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // Картинка выглядит шумом, но байты в ней повторяются каждые 256
        // значений: Flate ужимает 1 МБ до считаных килобайт, а вот JPEG
        // уменьшенной копии столько не стоит — заменять нечем.
        var tricky = new byte[600 * 600 * 3];
        uint seed = 777;
        for (var i = 0; i < tricky.Length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            tricky[i] = (byte)seed; // младший байт LCG: период 256
        }
        var images = FlateImageObject(5, 600, 600, tricky, "");
        var path = WriteRawImagePdf(dir, "tricky.pdf", images,
            "q 144 0 0 144 50 50 cm /Im1 Do Q", "/Im1 5 0 R");
        var before = new FileInfo(path).Length;

        var target = Path.Combine(dir, "tricky-out.pdf");
        var stats = await _pdfium.RecompressImagesAsync(
            path, null, target, 150, 75, EncodeJpeg, CancellationToken.None);

        Assert.Equal(0, stats.Recompressed);
        Assert.True(new FileInfo(target).Length <= before * 1.1,
            $"Файл не должен вырасти от «оптимизации»: {before} → {new FileInfo(target).Length}.");
    }

}
