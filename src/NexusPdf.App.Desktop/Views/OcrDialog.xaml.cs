using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.Application;
using Serilog;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Распознавание текста: выбор объёма → прогресс с отменой → итоги.
/// Операция идёт прямо из диалога; каждая распознанная страница —
/// отдельная операция сессии (Ctrl+Z отменяет постранично).
/// </summary>
public partial class OcrDialog : Window
{
    private readonly OcrService _service;
    private readonly DocumentViewModel _document;
    private CancellationTokenSource? _cts;
    private bool _cancelRequested;

    private OcrDialog(OcrService service, DocumentViewModel document)
    {
        InitializeComponent();
        _service = service;
        _document = document;
    }

    public static void Run(Window? owner, OcrService service, DocumentViewModel document)
    {
        var dialog = new OcrDialog(service, document);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_document.PageCount == 0)
        {
            // Дегенеративный PDF с нулём страниц открывается без ошибки —
            // честный пустой итог вместо исключения из Math.Clamp.
            ShowResult(new OcrRunResult(0, 0, 0, 0, 0, false, null));
            return;
        }
        IReadOnlyList<int>? targets = null;
        if (ScopeCurrent.IsChecked == true)
            targets = new[] { Math.Clamp(_document.CurrentPageNumber - 1, 0, Math.Max(0, _document.PageCount - 1)) };
        _cancelRequested = false;
        CancelRunButton.IsEnabled = true;

        SetupPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = Loc.F("OcrProgressLabel", 0,
            targets?.Count ?? _document.PageCount, 0);

        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<OcrProgress>(p =>
            {
                Bar.Maximum = Math.Max(1, p.TotalPages);
                Bar.Value = p.PagesDone;
                ProgressLabel.Text = Loc.F("OcrProgressLabel", p.PagesDone, p.TotalPages, p.WordsSoFar);
            });
            var editable = ModeEditable.IsChecked == true;
            var result = await _service.RecognizeAsync(
                _document.Document, targets, progress, _cts.Token, editable);
            ShowResult(result, editable);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка распознавания текста");
            ResultLabel.Text = Loc.Get("OcrError") + " " + ex.Message;
            HintLabel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ShowResult(OcrRunResult result, bool editable = false)
    {
        // Итог перечисляет ВСЁ, что реально произошло: отмена и ошибка
        // середины прогона не скрывают уже применённые страницы.
        var lines = new List<string>();
        if (result.Cancelled)
            lines.Add(Loc.Get("OcrCancelled"));
        if (result.Error is { } error)
            lines.Add(Loc.Get("OcrError") + " " + error);
        if (result.PagesRecognized > 0)
            lines.Add(Loc.F("OcrResult", result.PagesRecognized, result.WordCount,
                Math.Round(result.MeanConfidence)));
        if (result.PagesSkippedWithText > 0)
            lines.Add(Loc.F("OcrResultSkipped", result.PagesSkippedWithText));
        if (result.PagesWithoutWords > 0)
            lines.Add(Loc.F("OcrResultNoWords", result.PagesWithoutWords));
        if (lines.Count == 0)
            lines.Add(Loc.Get("OcrResultNothing"));

        ResultLabel.Text = string.Join(Environment.NewLine, lines);
        // В режиме редактируемого текста подсказка другая: там главное — что
        // строку можно править кликом, а не что она стала искаться.
        HintLabel.Text = Loc.Get(editable ? "OcrEditableHint" : "OcrSaveHint");
        HintLabel.Visibility = result.PagesRecognized > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
    }

    private void OnCancelRun(object sender, RoutedEventArgs e)
    {
        _cancelRequested = true;
        CancelRunButton.IsEnabled = false;
        _cts?.Cancel();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cts == null)
            return; // распознавание не идёт — закрытие свободно
        if (!_cancelRequested)
        {
            // Первый крестик = отмена: дождёмся конца текущей страницы.
            e.Cancel = true;
            OnCancelRun(sender!, new RoutedEventArgs());
            return;
        }
        // Повторный крестик: нативный вызов Tesseract мог подвиснуть, а
        // прервать его нельзя — окно отпускаем. Новых слоёв уже не будет:
        // токен отменён, сервис проверяет его перед каждым применением.
    }
}
