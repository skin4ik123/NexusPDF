using System.Globalization;
using System.Windows;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.Views;

public sealed record AddTextResult(string Text, double FontSizePt, uint ColorArgb);

public partial class AddTextDialog : Window
{
    private AddTextResult? _result;

    private AddTextDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TextBox1.Focus();
    }

    public static AddTextResult? Show(Window? owner)
    {
        var dialog = new AddTextDialog { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Visibility = Visibility.Collapsed;
        var text = TextBox1.Text.Trim();
        if (text.Length == 0)
        {
            ErrorLabel.Text = Loc.Get("AtTextEmpty");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }
        if (!double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) &&
            !double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out size) ||
            size is < 4 or > 144)
        {
            ErrorLabel.Text = Loc.Get("FontSizeInvalid");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        var color = RedRadio.IsChecked == true ? 0xFFD3282Fu
            : BlueRadio.IsChecked == true ? 0xFF2563EBu
            : 0xFF1B1C20u;

        _result = new AddTextResult(text, size, color);
        DialogResult = true;
    }
}
