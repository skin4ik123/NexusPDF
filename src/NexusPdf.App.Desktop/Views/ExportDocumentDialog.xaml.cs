using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Printing;

namespace NexusPdf.App.Desktop.Views;

/// <param name="Pages">Выбранные страницы (нуль-базные) или null — все.</param>
public sealed record ExportDocumentRequest(
    IReadOnlyList<int>? Pages,
    bool KeepLinks,
    bool KeepImages,
    bool KeepComments,
    bool DetectTables,
    bool RecognizeScans);

/// <summary>
/// Настройки экспорта в Word и Excel.
///
/// Раньше обе команды всегда брали весь документ с настройками по умолчанию:
/// на документе в триста страниц это не работа, а наказание. Здесь выбирается
/// объём и то, что переносить.
///
/// Про распознавание сканов окно говорит прямо: страница без текстового слоя
/// без него выгрузится пустой.
/// </summary>
public partial class ExportDocumentDialog : Window
{
    private ExportDocumentRequest? _result;
    private int _pageCount = 1;
    private int _currentPage;
    private IReadOnlyList<int>? _range;

    private ExportDocumentDialog() => InitializeComponent();

    /// <param name="forWord">
    /// Картинки и примечания есть только у Word: в книге Excel им негде жить,
    /// и показывать выключатели, которые ничего не делают, — обман.
    /// </param>
    public static ExportDocumentRequest? Show(
        Window? owner, bool forWord, int pageCount, int currentPage,
        bool ocrAvailable, string? ocrUnavailableReason)
    {
        var dialog = new ExportDocumentDialog
        {
            _pageCount = Math.Max(1, pageCount),
            _currentPage = Math.Clamp(currentPage, 0, Math.Max(0, pageCount - 1)),
        };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;

        dialog.Title = Loc.Get(forWord ? "ExportWordTitle" : "ExportExcelTitle");
        dialog.IntroText.Text = Loc.Get(forWord ? "ExportDocIntroWord" : "ExportDocIntroExcel");
        dialog.KeepImages.Visibility = forWord ? Visibility.Visible : Visibility.Collapsed;
        dialog.KeepComments.Visibility = forWord ? Visibility.Visible : Visibility.Collapsed;
        dialog.RangeHint.Text = Loc.F("ExportRangeHint", dialog._pageCount);

        if (!ocrAvailable)
        {
            dialog.RecognizeScans.IsChecked = false;
            dialog.RecognizeScans.IsEnabled = false;
            dialog.ScanHint.Text = ocrUnavailableReason ?? Loc.Get("ExportScansUnavailable");
        }
        else
        {
            dialog.ScanHint.Text = Loc.Get("ExportScansHint");
        }

        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>
    /// Диапазон проверяется по мере ввода: узнать об ошибке в момент нажатия
    /// «Экспортировать» — значит потерять уже выбранное имя файла.
    /// </summary>
    private void OnRangeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (RangeHint == null || ExportButton == null) return;

        if (string.IsNullOrWhiteSpace(RangeBox.Text))
        {
            _range = null;
            RangeHint.Text = Loc.F("ExportRangeHint", _pageCount);
            ExportButton.IsEnabled = true;
            return;
        }

        var parsed = PageRangeParser.Parse(RangeBox.Text, _pageCount);
        if (parsed.Error != null)
        {
            _range = null;
            RangeHint.Text = parsed.Error;
            ExportButton.IsEnabled = ScopeRange.IsChecked != true;
            return;
        }

        _range = parsed.Indices;
        RangeHint.Text = Loc.F("ExportRangeChosen", parsed.Indices.Count, parsed.Normalized);
        ExportButton.IsEnabled = true;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<int>? pages = null;
        if (ScopeCurrent.IsChecked == true) pages = new[] { _currentPage };
        else if (ScopeRange.IsChecked == true) pages = _range;

        if (ScopeRange.IsChecked == true && (pages == null || pages.Count == 0))
        {
            RangeHint.Text = Loc.Get("ExportRangeEmpty");
            return;
        }

        _result = new ExportDocumentRequest(
            pages,
            KeepLinks.IsChecked == true,
            KeepImages.IsChecked == true,
            KeepComments.IsChecked == true,
            DetectTables.IsChecked == true,
            RecognizeScans.IsChecked == true);
        DialogResult = true;
    }
}
