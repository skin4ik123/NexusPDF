using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.Services;

public static class BitmapFactory
{
    /// <summary>Преобразует BGRA-растр движка в замороженный BitmapSource (можно использовать из любого потока).</summary>
    public static BitmapSource ToBitmapSource(RenderedPageImage image)
    {
        var bitmap = BitmapSource.Create(
            image.PixelWidth, image.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, image.Bgra, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
