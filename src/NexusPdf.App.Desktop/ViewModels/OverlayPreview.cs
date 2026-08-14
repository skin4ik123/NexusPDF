using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Предпросмотр наложенного контента в режиме чтения. Координаты — в пунктах
/// страницы; масштабирование выполняет LayoutTransform слоя предпросмотра.
/// </summary>
public sealed class OverlayPreview
{
    private OverlayPreview() { }

    public bool IsText { get; private init; }
    public string Text { get; private init; } = "";
    public double FontSizePt { get; private init; }
    public Brush Fill { get; private init; } = Brushes.Black;
    public double XPt { get; private init; }
    public double YPt { get; private init; }
    public double WidthPt { get; private init; }
    public double HeightPt { get; private init; }
    /// <summary>Угол для WPF RotateTransform (по часовой; знак уже инвертирован).</summary>
    public double AngleDeg { get; private init; }
    public BitmapSource? Image { get; private init; }

    public static OverlayPreview? From(PageOverlay overlay)
    {
        switch (overlay)
        {
            case TextOverlay text:
            {
                var brush = new SolidColorBrush(Color.FromArgb(
                    (byte)(text.ColorArgb >> 24), (byte)(text.ColorArgb >> 16),
                    (byte)(text.ColorArgb >> 8), (byte)text.ColorArgb));
                brush.Freeze();
                return new OverlayPreview
                {
                    IsText = true,
                    Text = text.Text,
                    FontSizePt = text.FontSizePt,
                    Fill = brush,
                    XPt = text.XPt,
                    YPt = text.YPt,
                    AngleDeg = -text.RotationDegrees,
                };
            }
            case ImageOverlay image:
            {
                var bitmap = BitmapSource.Create(
                    image.PixelWidth, image.PixelHeight, 96, 96,
                    PixelFormats.Bgra32, null, image.Bgra, image.PixelWidth * 4);
                bitmap.Freeze();
                return new OverlayPreview
                {
                    Image = bitmap,
                    XPt = image.XPt,
                    YPt = image.YPt,
                    WidthPt = image.WidthPt,
                    HeightPt = image.HeightPt,
                };
            }
            default:
                return null;
        }
    }
}
