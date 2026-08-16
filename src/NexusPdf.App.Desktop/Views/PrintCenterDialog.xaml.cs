using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.Application;
using NexusPdf.Printing;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Центр печати. Окно ничего не считает само: вся геометрия живёт в
/// <see cref="PrintCenterViewModel"/> и в ядре печати, а отсюда только
/// показывается и отправляется.
/// </summary>
public partial class PrintCenterDialog : Window
{
    private readonly PrintCenterViewModel _model;
    private readonly DocumentViewModel _document;
    private readonly AppServices _services;

    private PrintCenterDialog(
        DocumentViewModel document, AppServices services, IReadOnlyList<int>? preselectedPages)
    {
        InitializeComponent();
        _document = document;
        _services = services;
        _model = new PrintCenterViewModel(document, services, preselectedPages);
        DataContext = _model;
    }

    /// <param name="preselectedPages">
    /// Логические номера страниц (с нуля), выбранные до открытия окна: печать
    /// из контекстного меню миниатюр сразу показывает именно их.
    /// </param>
    public static void Run(
        Window? owner, DocumentViewModel document, AppServices services,
        IReadOnlyList<int>? preselectedPages = null)
    {
        var dialog = new PrintCenterDialog(document, services, preselectedPages);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    /// <summary>
    /// Горячие клавиши окна. Enter намеренно НЕ отправляет задание: кнопка
    /// «Печать» не назначена IsDefault, чтобы случайное нажатие в поле ввода
    /// не отправило на принтер сотню листов.
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 || (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control))
        {
            _model.RefreshPrintersCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown)
        {
            if (_model.SelectedSheetIndex < _model.Sheets.Count - 1) _model.SelectedSheetIndex++;
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp)
        {
            if (_model.SelectedSheetIndex > 0) _model.SelectedSheetIndex--;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Сводка комментариев отдельным файлом. Исходный документ не меняется —
    /// поэтому сводка и делается отдельным PDF, а не вставкой страниц.
    /// </summary>
    private async void OnCommentSummary(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("PrintCommentSummary"),
            Filter = Loc.Get("PdfFilter"),
            FileName = Path.GetFileNameWithoutExtension(_document.Title) + "-comments.pdf",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var ct = _model.BeginSubmit(Loc.Get("PrintCommentSummary"));
        try
        {
            var result = await new CommentSummaryService(_services.Engine).BuildAsync(
                _document.Document, dialog.FileName, new CommentSummarySettings(),
                _document.Title, ct);

            InfoDialog.Show(this, Loc.Get("PrintCommentSummary"),
                Loc.F("PrintCommentSummaryDone", result.CommentCount, result.PageCount));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось построить сводку комментариев");
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            _model.EndSubmit();
        }
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Ask(this,
            Loc.Get("PrintProfileSave"), Loc.Get("PrintProfileName"),
            _model.SelectedProfile is { IsBuiltIn: false } p ? p.Name : "");
        if (string.IsNullOrWhiteSpace(name)) return;
        _model.SaveProfile(name.Trim());
    }

    private async void OnSaveToFile(object sender, RoutedEventArgs e)
    {
        if (_model.Plan is not { } plan) return;

        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("PrintSaveToFile"),
            Filter = Loc.Get("PdfFilter"),
            FileName = Path.GetFileNameWithoutExtension(_document.Title) + "-print.pdf",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var ct = _model.BeginSubmit(Loc.Get("PrintSaveToFile"));
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => _model.ReportSubmit(p.Done, p.Total));
            var result = await new PrintToFileService(_services.Engine)
                .SaveAsync(_document.Document, plan, dialog.FileName, 300, progress, ct);

            InfoDialog.Show(this, Loc.Get("PrintSaveToFile"),
                Loc.F("PrintSavedToFile", result.SheetsWritten, Math.Round(result.EffectiveDpi)));
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Остановка — не ошибка: окно остаётся открытым, чтобы можно было
            // поправить настройки и попробовать снова.
            InfoDialog.Show(this, Loc.Get("PrintSaveToFile"), Loc.F("PrintCancelled", 0));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось сохранить печатную раскладку");
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            _model.EndSubmit();
        }
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_model.Plan is not { } plan || _model.SelectedPrinter == null) return;

        // Ручной дуплекс идёт своим путём: два задания с остановкой между ними.
        if (plan.Duplex == DuplexMode.Manual)
        {
            ManualDuplexDialog.Run(this, _document, _services, plan);
            return;
        }

        var ct = _model.BeginSubmit(Loc.Get("Printing"));
        try
        {
            var progress = new Progress<Services.Printing.PrintProgress>(
                p => _model.ReportSubmit(p.SheetsDone, p.SheetsTotal));
            var job = await _services.PrintJobs.SubmitAsync(_document.Document, plan, progress, ct);
            _services.PrintQueue.Track(job.PrinterName, job.JobIds, _document.Title);

            InfoDialog.Show(this, Loc.Get("Print"),
                Loc.F("PrintJobQueued", job.SheetsSent, plan.PrinterName));
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Часть листов уже могла уйти в очередь — говорим об этом прямо,
            // а не делаем вид, что ничего не произошло.
            InfoDialog.Show(this, Loc.Get("Print"), Loc.F("PrintCancelled", 0));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка отправки задания печати");
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            _model.EndSubmit();
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _model.Dispose();
}
