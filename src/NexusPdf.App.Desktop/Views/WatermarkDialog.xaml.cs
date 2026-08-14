using System.Windows;
using NexusPdf.Application;
using NexusPdf.Domain;

namespace NexusPdf.App.Desktop.Views;

public partial class WatermarkDialog : Window
{
    private readonly int _pageCount;
    private WatermarkOptions? _result;

    private WatermarkDialog(int pageCount)
    {
        _pageCount = pageCount;
        InitializeComponent();
    }

    public static WatermarkOptions? Show(Window? owner, int pageCount)
    {
        var dialog = new WatermarkDialog(pageCount) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Visibility = Visibility.Collapsed;
        var text = TextBox1.Text.Trim();
        if (text.Length == 0)
        {
            ErrorLabel.Text = Localization.Loc.Get("WmTextEmpty");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        IReadOnlyList<int> indices;
        if (string.IsNullOrWhiteSpace(RangeBox.Text))
        {
            indices = Enumerable.Range(0, _pageCount).ToArray();
        }
        else if (!PageRange.TryParse(RangeBox.Text, _pageCount, out indices, out var error))
        {
            ErrorLabel.Text = error ?? "";
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        _result = new WatermarkOptions(
            text,
            SizeSlider.Value,
            OpacitySlider.Value / 100.0,
            DiagonalCheck.IsChecked == true,
            indices);
        DialogResult = true;
    }
}
