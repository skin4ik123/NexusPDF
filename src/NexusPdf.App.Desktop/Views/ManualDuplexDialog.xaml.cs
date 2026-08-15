using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.Printing;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Мастер ручной двусторонней печати для принтеров без дуплекса.
///
/// Разделён на два прохода с обязательной остановкой между ними: пользователь
/// должен успеть забрать и перевернуть стопку. Второе задание НЕ отправляется
/// автоматически — иначе обороты напечатались бы на чистой бумаге.
/// </summary>
public partial class ManualDuplexDialog : Window
{
    private enum Stage { AskFacing, TurnStack, Done }

    private readonly DocumentViewModel _document;
    private readonly AppServices _services;
    private readonly PrintJobPlan _plan;

    private Stage _stage = Stage.AskFacing;
    private OutputFacing _facing = OutputFacing.FaceDown;
    private bool _busy;

    private ManualDuplexDialog(DocumentViewModel document, AppServices services, PrintJobPlan plan)
    {
        InitializeComponent();
        _document = document;
        _services = services;
        _plan = plan;
        ShowStage();
    }

    public static void Run(Window? owner, DocumentViewModel document, AppServices services, PrintJobPlan plan)
    {
        var dialog = new ManualDuplexDialog(document, services, plan);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private void ShowStage()
    {
        switch (_stage)
        {
            case Stage.AskFacing:
                StepTitle.Text = Loc.Get("MdStep1Title");
                StepIntro.Text = Loc.F("MdStep1Intro",
                    ManualDuplex.FirstPass(_plan).Sheets.Count, _plan.SheetCount);
                FacingPanel.Visibility = Visibility.Visible;
                StepsPanel.Visibility = Visibility.Collapsed;
                ActionButton.Content = Loc.Get("MdPrintFirst");
                break;

            case Stage.TurnStack:
                StepTitle.Text = Loc.Get("MdStep2Title");
                StepIntro.Text = Loc.Get("MdStep2Intro");
                FacingPanel.Visibility = Visibility.Collapsed;
                StepsPanel.Visibility = Visibility.Visible;

                var explanation = ManualDuplex.Explain(_facing, _plan.Duplex);
                EdgeHint.Text = explanation.EdgeHint;
                StepsList.ItemsSource = explanation.Steps;
                ActionButton.Content = Loc.Get("MdPrintSecond");
                break;

            case Stage.Done:
                StepTitle.Text = Loc.Get("MdDoneTitle");
                StepIntro.Text = Loc.Get("MdDoneIntro");
                FacingPanel.Visibility = Visibility.Collapsed;
                StepsPanel.Visibility = Visibility.Collapsed;
                ActionButton.Content = Loc.Get("OK");
                CancelButton.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_stage == Stage.Done)
        {
            DialogResult = true;
            return;
        }

        _facing = FaceUpRadio.IsChecked == true ? OutputFacing.FaceUp : OutputFacing.FaceDown;

        var pass = _stage == Stage.AskFacing
            ? ManualDuplex.FirstPass(_plan)
            : ManualDuplex.SecondPass(_plan, _facing);

        _busy = true;
        ActionButton.IsEnabled = false;
        StatusText.Text = Loc.Get("Printing");
        try
        {
            var progress = new Progress<Services.Printing.PrintProgress>(
                p => StatusText.Text = Loc.F("PrintSubmitProgress", p.SheetsDone, p.SheetsTotal));
            var job = await _services.PrintJobs.SubmitAsync(
                _document.Document, pass, progress, CancellationToken.None);

            StatusText.Text = Loc.F("PrintJobQueued", job.SheetsSent, pass.PrinterName);

            // Второй проход не запускается сам: между проходами обязательна
            // остановка, иначе обороты лягут на чистую бумагу.
            _stage = _stage == Stage.AskFacing && ManualDuplex.HasSecondPass(_plan)
                ? Stage.TurnStack
                : Stage.Done;
            ShowStage();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка ручной двусторонней печати");
            StatusText.Text = "";
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            _busy = false;
            ActionButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Отмена после первой стороны — законный сценарий: половина уже
        // напечатана, и говорить об этом надо прямо.
        if (_stage == Stage.TurnStack)
            ErrorDialog.Show(this, Loc.Get("MdTitle"), Loc.Get("MdAbandoned"), "");
        DialogResult = false;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) e.Cancel = true; // идёт отправка — окно не бросаем
    }
}
