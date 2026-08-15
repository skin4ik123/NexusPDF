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

    private PrintCenterDialog(DocumentViewModel document, AppServices services)
    {
        InitializeComponent();
        _document = document;
        _services = services;
        _model = new PrintCenterViewModel(document, services);
        DataContext = _model;
    }

    public static void Run(Window? owner, DocumentViewModel document, AppServices services)
    {
        var dialog = new PrintCenterDialog(document, services);
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

        IsEnabled = false;
        try
        {
            var result = await new PrintToFileService(_services.Engine)
                .SaveAsync(_document.Document, plan, dialog.FileName, 300, null, CancellationToken.None);

            InfoDialog.Show(this, Loc.Get("PrintSaveToFile"),
                Loc.F("PrintSavedToFile", result.SheetsWritten, Math.Round(result.EffectiveDpi)));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось сохранить печатную раскладку");
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_model.Plan is not { } plan || _model.SelectedPrinter == null) return;

        IsEnabled = false;
        try
        {
            var job = await _services.PrintJobs.SubmitAsync(
                _document.Document, plan, progress: null, CancellationToken.None);

            InfoDialog.Show(this, Loc.Get("Print"),
                Loc.F("PrintJobQueued", job.SheetsSent, plan.PrinterName));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка отправки задания печати");
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _model.Dispose();
}
