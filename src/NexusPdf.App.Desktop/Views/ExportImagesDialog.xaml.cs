using System.Windows;
using System.Windows.Controls;

namespace NexusPdf.App.Desktop.Views;

public sealed record ExportImagesRequest(bool Jpeg, double Dpi, bool CurrentOnly);

public partial class ExportImagesDialog : Window
{
    private ExportImagesRequest? _result;

    private ExportImagesDialog() => InitializeComponent();

    public static ExportImagesRequest? Show(Window? owner)
    {
        var dialog = new ExportImagesDialog();
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dpiText = (DpiCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "150";
        _result = new ExportImagesRequest(
            FormatJpeg.IsChecked == true,
            double.TryParse(dpiText, out var dpi) ? dpi : 150,
            ScopeCurrent.IsChecked == true);
        DialogResult = true;
    }
}
