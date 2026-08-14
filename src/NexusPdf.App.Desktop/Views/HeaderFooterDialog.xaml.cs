using System.Globalization;
using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Application;
using NexusPdf.Domain;

namespace NexusPdf.App.Desktop.Views;

public partial class HeaderFooterDialog : Window
{
    private readonly int _pageCount;
    private HeaderFooterOptions? _result;

    private HeaderFooterDialog(int pageCount)
    {
        _pageCount = pageCount;
        InitializeComponent();
    }

    public static HeaderFooterOptions? Show(Window? owner, int pageCount)
    {
        var dialog = new HeaderFooterDialog(pageCount) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Visibility = Visibility.Collapsed;

        if (TemplateBox.Text.Trim().Length == 0)
        {
            ShowError(Loc.Get("AtTextEmpty"));
            return;
        }
        if (!double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) &&
            !double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out size) ||
            !(size is >= 4 and <= 72)) // отрицание отсекает и NaN
        {
            ShowError(Loc.Get("FontSizeInvalid"));
            return;
        }

        IReadOnlyList<int> indices;
        if (string.IsNullOrWhiteSpace(RangeBox.Text))
        {
            indices = Enumerable.Range(0, _pageCount).ToArray();
        }
        else if (!PageRange.TryParse(RangeBox.Text, _pageCount, out indices, out var error))
        {
            ShowError(error ?? "");
            return;
        }

        _result = new HeaderFooterOptions(
            TemplateBox.Text,
            TopRadio.IsChecked == true ? DecorPosition.Top : DecorPosition.Bottom,
            LeftRadio.IsChecked == true ? DecorAlignment.Left
                : RightRadio.IsChecked == true ? DecorAlignment.Right
                : DecorAlignment.Center,
            size,
            indices,
            SkipFirstCheck.IsChecked == true);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }
}
