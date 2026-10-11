using System.Windows;
using System.Windows.Controls;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Printing;

namespace NexusPdf.App.Desktop.Views;

public sealed record ExportImagesRequest(bool Jpeg, double Dpi, IReadOnlyList<int>? Pages, int JpegQuality);

public partial class ExportImagesDialog : Window
{
    private ExportImagesRequest? _result;
    private int _pageCount = 1;
    private int _currentPage;

    private ExportImagesDialog() => InitializeComponent();

    public static ExportImagesRequest? Show(Window? owner, int pageCount, int currentPage,
        IReadOnlyList<int>? selectedPages = null)
    {
        var dialog = new ExportImagesDialog
        {
            _pageCount = Math.Max(1, pageCount),
            _currentPage = Math.Clamp(currentPage, 0, Math.Max(0, pageCount - 1)),
        };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        if (selectedPages is { Count: > 0 })
        {
            dialog.RangeBox.Text = string.Join(",", selectedPages.Distinct().Order().Select(i => i + 1));
            dialog.ScopeRange.IsChecked = true;
        }
        dialog.ValidateRange();
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<int>? pages = null;
        if (ScopeCurrent.IsChecked == true) pages = new[] { _currentPage };
        else if (ScopeRange.IsChecked == true)
        {
            var parsed = PageRangeParser.Parse(RangeBox.Text, _pageCount);
            if (!parsed.IsValid) { ValidateRange(); return; }
            pages = parsed.Indices.Distinct().ToArray();
        }
        var dpiText = (DpiCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "150";
        _result = new ExportImagesRequest(
            FormatJpeg.IsChecked == true,
            double.TryParse(dpiText, out var dpi) ? dpi : 150,
            pages,
            int.TryParse((QualityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var quality) ? quality : 90);
        DialogResult = true;
    }

    private void OnRangeChanged(object sender, TextChangedEventArgs e) => ValidateRange();
    private void OnScopeChanged(object sender, RoutedEventArgs e) => ValidateRange();

    private void ValidateRange()
    {
        if (RangeBox == null || RangeHint == null || ExportButton == null) return;
        var ranged = ScopeRange.IsChecked == true;
        var parsed = PageRangeParser.Parse(RangeBox.Text, _pageCount);
        ExportButton.IsEnabled = !ranged || parsed.IsValid;
        RangeHint.Text = !ranged ? Loc.F("ExportRangeHint", _pageCount)
            : parsed.Error ?? Loc.F("ExportRangeChosen", parsed.Indices.Distinct().Count(), parsed.Normalized);
    }
}
