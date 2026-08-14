using System.Windows;
using System.Windows.Media.Imaging;

namespace NexusPdf.App.Desktop.Views;

public partial class ImagePlaceDialog : Window
{
    private double? _result;

    private ImagePlaceDialog(BitmapSource preview, double defaultWidthPercent)
    {
        InitializeComponent();
        PreviewImage.Source = preview;
        WidthSlider.Value = defaultWidthPercent;
    }

    /// <summary>Возвращает ширину в процентах ширины страницы или null при отмене.</summary>
    public static double? Show(Window? owner, BitmapSource preview, double defaultWidthPercent = 35)
    {
        var dialog = new ImagePlaceDialog(preview, defaultWidthPercent) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        _result = WidthSlider.Value;
        DialogResult = true;
    }
}
