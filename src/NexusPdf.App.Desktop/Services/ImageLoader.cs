using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NexusPdf.App.Desktop.Services;

public sealed record LoadedImage(byte[] Bgra, int PixelWidth, int PixelHeight)
{
    public double Aspect => PixelHeight / (double)Math.Max(1, PixelWidth);
}

/// <summary>Декодирование пользовательских изображений (PNG/JPEG/BMP/TIFF) в BGRA32 и обратно в превью.</summary>
public static class ImageLoader
{
    public static LoadedImage FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    public static LoadedImage FromBytes(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes);
        var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new LoadedImage(pixels, converted.PixelWidth, converted.PixelHeight);
    }

    public static BitmapSource Preview(LoadedImage image)
    {
        var bitmap = BitmapSource.Create(
            image.PixelWidth, image.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, image.Bgra, image.PixelWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
