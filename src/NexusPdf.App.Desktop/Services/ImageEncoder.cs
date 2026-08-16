using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.Services;

/// <summary>
/// Кодеки для конвертации: BGRA-растр страницы → PNG/JPEG и файл
/// изображения → страница PDF (размер по DPI из метаданных файла).
/// </summary>
public static class ImageEncoder
{
    /// <summary>BGRA-растр → PNG без потерь с записью DPI (для правки во внешнем редакторе).</summary>
    public static byte[] EncodePng(byte[] bgra, int width, int height, double dpi)
    {
        var source = BitmapSource.Create(
            width, height, dpi, dpi, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Картинка для вставки в документ Word: формат выбирается по содержимому.
    ///
    /// Фотографию и скан жмём JPEG — без потерь они весили бы в десятки раз
    /// больше без всякой пользы для глаза. Схему, чертёж и снимок экрана JPEG,
    /// наоборот, портит ореолами и часто РАЗДУВАЕТ, поэтому они идут PNG.
    /// </summary>
    public static NexusPdf.Export.EncodedImage EncodeForDocument(byte[] bgra, int width, int height)
    {
        var analysis = ImageCodecChooser.Analyze(bgra, width, height, quality: 82);
        var lossless = analysis.Content == ImageContent.Graphics;

        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        BitmapEncoder encoder = lossless
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = analysis.Jpeg.Quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new NexusPdf.Export.EncodedImage(
            stream.ToArray(), lossless ? "image/png" : "image/jpeg");
    }

    /// <summary>Замороженный BitmapSource из BGRA-растра (для предпросмотра).</summary>
    public static BitmapSource ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Файл изображения → BGRA-растр (возврат из внешнего редактора).</summary>
    public static (byte[] Bgra, int PixelWidth, int PixelHeight) DecodeBgra(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frame = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * (long)converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }

    /// <summary>BGRA-растр → JPEG с заданным качеством (для пересжатия изображений документа).</summary>
    public static byte[] EncodeJpeg(byte[] bgra, int width, int height, int quality)
    {
        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// BGRA-растр → JPEG по выбранному способу. Серый вариант выбрасывает две
    /// цветовые плоскости: для скана это примерно втрое меньший файл при том же
    /// виде, и ради него весь выбор кодека и затевался.
    /// </summary>
    public static byte[] EncodeChosen(byte[] bgra, int width, int height, ImageEncodingChoice choice)
    {
        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        BitmapSource frame = source;
        if (choice.IsGray)
        {
            var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            frame = gray;
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = choice.Quality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] Encode(RenderedPageImage image, bool jpeg, double dpi)
    {
        var source = BitmapSource.Create(
            image.PixelWidth, image.PixelHeight, dpi, dpi,
            PixelFormats.Bgra32, null, image.Bgra, image.Stride);
        BitmapEncoder encoder = jpeg
            ? new JpegBitmapEncoder { QualityLevel = 90 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Файл изображения → страница будущего PDF. Размер страницы берётся из
    /// DPI метаданных (запасной вариант 96); гигантские фото ужимаются до
    /// 24 мегапикселей — как и при вставке изображения в документ.
    /// </summary>
    public static ImagePageSpec DecodeAsPageSpec(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frame = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var dpiX = frame.DpiX > 1 ? frame.DpiX : 96;
        var dpiY = frame.DpiY > 1 ? frame.DpiY : 96;
        var widthPoints = frame.PixelWidth / dpiX * 72.0;
        var heightPoints = frame.PixelHeight / dpiY * 72.0;

        BitmapSource decoded = frame;
        var totalPixels = (double)frame.PixelWidth * frame.PixelHeight;
        const double maxPixels = 24_000_000;
        if (totalPixels > maxPixels)
        {
            var k = Math.Sqrt(maxPixels / totalPixels);
            var scaled = new TransformedBitmap(frame, new ScaleTransform(k, k));
            scaled.Freeze();
            decoded = scaled;
        }

        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * (long)converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new ImagePageSpec(pixels, converted.PixelWidth, converted.PixelHeight, widthPoints, heightPoints);
    }
}
