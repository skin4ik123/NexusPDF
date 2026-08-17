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
    private readonly Services.AppServices _services;
    private readonly DocumentViewModel _document;
    private CancellationTokenSource? _cts;
    private bool _cancelRequested;

    /// <summary>Строка списка движков: идентификатор и подпись.</summary>
    private sealed record EngineChoice(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private OcrDialog(Services.AppServices services, DocumentViewModel document)
    {
        InitializeComponent();
        _services = services;
        _document = document;
        FillEngineChoices();
    }

    /// <param name="preferEditable">
    /// Заранее выбрать режим редактируемого текста. Нужен, когда сюда пришли
    /// из правки текста: человек уже сказал, что хочет править, и подсовывать
    /// ему поисковый режим по умолчанию значило бы завести его на второй круг.
    /// </param>
    public static void Run(Window? owner, Services.AppServices services, DocumentViewModel document,
        bool preferEditable = false)
    {
        var dialog = new OcrDialog(services, document);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        if (preferEditable)
            dialog.ModeEditable.IsChecked = true;
        dialog.ShowDialog();
    }

    /// <summary>
    /// В списке только то, что реально установлено: движок без моделей в
    /// выборе не показывается, чтобы не предлагать заведомо неработающее.
    /// </summary>
    private void FillEngineChoices()
    {
        var packs = NexusPdf.Ocr.Paddle.PaddleOcrEngine.InstalledPacks(AppContext.BaseDirectory);
        var engines = new List<EngineChoice>();
        if (packs.Count > 0)
            engines.Add(new EngineChoice("paddle", Loc.Get("OcrEnginePaddle")));
        using (var tesseract = new NexusPdf.Ocr.TesseractOcrEngine())
        {
            if (tesseract.IsAvailable)
                engines.Add(new EngineChoice("tesseract", Loc.Get("OcrEngineTesseract")));
        }

        EngineBox.ItemsSource = engines;
        PackBox.ItemsSource = packs;
        EngineBox.SelectedItem =
            engines.FirstOrDefault(e => e.Id == _services.Settings.OcrEngine) ?? engines.FirstOrDefault();
        PackBox.SelectedItem =
            packs.FirstOrDefault(p => p.Id == _services.Settings.OcrLanguagePack) ?? packs.FirstOrDefault();
    }

    private void OnPackChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => OnEngineChanged(sender, e);

    private void OnEngineChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PackBox == null || PackLabel == null || PackHint == null)
            return; // событие приходит и во время InitializeComponent
        // Языковые пакеты есть только у PaddleOCR: у Tesseract язык вшит в
        // установленные модели rus+eng.
        var isPaddle = (EngineBox.SelectedItem as EngineChoice)?.Id == "paddle";
        var visibility = isPaddle ? Visibility.Visible : Visibility.Collapsed;
        PackBox.Visibility = visibility;
        PackLabel.Visibility = visibility;
        PackHint.Text = isPaddle
            ? (PackBox.SelectedItem as NexusPdf.Ocr.Paddle.PaddleOcrEngine.LanguagePack)?.Languages ?? ""
            : Loc.Get("OcrTesseractLanguages");
    }

    /// <summary>
    /// Применяет выбор к общему движку приложения. Выбор запоминается: он же
    /// действует для распознавания из окна правки растра.
    /// </summary>
    private OcrService ApplyChoice()
    {
        var engineId = (EngineBox.SelectedItem as EngineChoice)?.Id ?? _services.Settings.OcrEngine;
        var packId = (PackBox.SelectedItem as NexusPdf.Ocr.Paddle.PaddleOcrEngine.LanguagePack)?.Id
                     ?? _services.Settings.OcrLanguagePack;
        _services.ApplyOcrSettings(engineId, packId);
        return _services.Ocr;
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
            // Движок пересобирается под выбор в списке, а не фиксируется на
            // старте: иначе смена языка требовала бы перезапуска программы.
            var service = ApplyChoice();
            Log.Information("Распознавание движком {Engine}", service.EngineName);
            var result = await service.RecognizeAsync(
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
