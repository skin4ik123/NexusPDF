using System.Windows;

namespace NexusPdf.App.Desktop.Views;

public sealed record CompressImagesRequest(double Dpi, int Quality);

public partial class CompressImagesDialog : Window
{
    private CompressImagesRequest? _result;

    private CompressImagesDialog() => InitializeComponent();

    public static CompressImagesRequest? Show(Window? owner)
    {
        var dialog = new CompressImagesDialog();
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnCompress(object sender, RoutedEventArgs e)
    {
        var dpi = DpiCombo.SelectedIndex switch { 1 => 96.0, 2 => 72.0, _ => 150.0 };
        var quality = QualityCombo.SelectedIndex switch { 1 => 85, 2 => 60, _ => 75 };
        _result = new CompressImagesRequest(dpi, quality);
        DialogResult = true;
    }
}
