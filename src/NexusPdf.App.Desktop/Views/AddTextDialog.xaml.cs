using System.Globalization;
using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.Views;

public sealed record AddTextResult(
    string Text, double FontSizePt, uint ColorArgb,
    string FontFamily, bool Bold, bool Italic);

public partial class AddTextDialog : Window
{
    // Выбор шрифта запоминается на время сеанса: подписывая десяток страниц
    // подряд, одну и ту же гарнитуру не выбирают заново каждый раз.
    private static string _lastFamily = PdfFontCatalog.DefaultFamily;
    private static bool _lastBold;
    private static bool _lastItalic;
    private static double _lastSize = 14;

    private AddTextResult? _result;

    private AddTextDialog()
    {
        InitializeComponent();

        var families = PdfFontCatalog.AvailableFamilies();
        FontBox.ItemsSource = families;
        var index = families.ToList().IndexOf(_lastFamily);
        FontBox.SelectedIndex = index >= 0 ? index : 0;
        BoldToggle.IsChecked = _lastBold;
        ItalicToggle.IsChecked = _lastItalic;
        SizeBox.Text = _lastSize.ToString(CultureInfo.CurrentCulture);

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
            !(size is >= 4 and <= 144)) // отрицание отсекает и NaN
        {
            ErrorLabel.Text = Loc.Get("FontSizeInvalid");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        var color = RedRadio.IsChecked == true ? 0xFFD3282Fu
            : BlueRadio.IsChecked == true ? 0xFF2563EBu
            : 0xFF1B1C20u;

        var family = FontBox.SelectedItem as string ?? PdfFontCatalog.DefaultFamily;
        var bold = BoldToggle.IsChecked == true;
        var italic = ItalicToggle.IsChecked == true;

        (_lastFamily, _lastBold, _lastItalic, _lastSize) = (family, bold, italic, size);

        _result = new AddTextResult(text, size, color, family, bold, italic);
        DialogResult = true;
    }
}
