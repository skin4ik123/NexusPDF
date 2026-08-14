using System.Windows;
using System.Windows.Input;
using NexusPdf.App.Desktop.Services;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.Domain;

namespace NexusPdf.App.Desktop.Views;

public partial class PrintOptionsDialog : Window
{
    private readonly DocumentViewModel _doc;
    private PrintJob? _result;

    private PrintOptionsDialog(DocumentViewModel doc)
    {
        _doc = doc;
        InitializeComponent();
    }

    public static PrintJob? Show(Window? owner, DocumentViewModel doc)
    {
        var dialog = new PrintOptionsDialog(doc) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnRangeFocused(object sender, KeyboardFocusChangedEventArgs e) =>
        RangeRadio.IsChecked = true;

    private void OnNext(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<int> indices;
        if (CurrentRadio.IsChecked == true)
        {
            indices = new[] { Math.Clamp(_doc.CurrentPageNumber - 1, 0, _doc.PageCount - 1) };
        }
        else if (RangeRadio.IsChecked == true)
        {
            if (!PageRange.TryParse(RangeBox.Text, _doc.PageCount, out indices, out var error))
            {
                RangeError.Text = error;
                RangeError.Visibility = Visibility.Visible;
                return;
            }
        }
        else
        {
            indices = Enumerable.Range(0, _doc.PageCount).ToArray();
        }

        _result = new PrintJob(indices, FitToPage: FitRadio.IsChecked == true);
        DialogResult = true;
    }
}
