using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Application;
using NexusPdf.Infrastructure;
using NexusPdf.Printing;
using NexusPdf.Printing.Windows;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Строка списка файлов пакета.</summary>
public sealed partial class BatchFileRow : ObservableObject
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    private string _status = "";
}

/// <summary>
/// Пакетная печать: один профиль применяется к набору файлов.
/// Ошибка одного документа не прерывает остальные — итог виден по каждой
/// строке, а не одним «не получилось» в конце.
/// </summary>
public partial class BatchPrintDialog : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<BatchFileRow> _files = new();
    private readonly WindowsPrinterService _printers = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    private BatchPrintDialog(AppServices services)
    {
        InitializeComponent();
        _services = services;
        FileList.ItemsSource = _files;

        ProfileBox.ItemsSource = new PrintProfileStore().LoadAll();
        ProfileBox.SelectedIndex = 0;

        var printers = _printers.Discover();
        PrinterBox.ItemsSource = printers;
        PrinterBox.SelectedItem = printers.FirstOrDefault(p => p.IsDefault) ?? printers.FirstOrDefault();

        UpdateStatus();
    }

    public static void Run(Window? owner, AppServices services)
    {
        var dialog = new BatchPrintDialog(services);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("BpAdd"),
            Filter = Loc.Get("PdfFilter"),
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (var path in dialog.FileNames)
        {
            // Повторное добавление того же файла напечатало бы его дважды
            // молча — это почти всегда промах мышью, а не намерение.
            if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _files.Add(new BatchFileRow { Path = path, Status = Loc.Get("BpWaiting") });
        }
        UpdateStatus();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var row in FileList.SelectedItems.Cast<BatchFileRow>().ToList())
            _files.Remove(row);
        UpdateStatus();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running || _files.Count == 0) return;
        if (ProfileBox.SelectedItem is not PrintProfile profile) return;
        if (PrinterBox.SelectedItem is not PrinterCapabilities printer) return;

        var paper = printer.PaperSizes.FirstOrDefault(p => p.Name == "A4")
                    ?? printer.PaperSizes.FirstOrDefault();
        if (paper == null)
        {
            ErrorDialog.Show(this, Loc.Get("BpTitle"), Loc.Get("BpNoPaper"), "");
            return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        Bar.Visibility = Visibility.Visible;
        Bar.Maximum = _files.Count;
        Bar.Value = 0;

        foreach (var row in _files) row.Status = Loc.Get("BpWaiting");

        try
        {
            var items = _files.Select(f => new BatchPrintItem(f.Path)).ToList();
            var progress = new Progress<BatchPrintProgress>(p =>
            {
                Bar.Value = p.Done;
                StatusText.Text = p.CurrentFile.Length > 0
                    ? Loc.F("BpProgress", p.Done + 1, p.Total, p.CurrentFile)
                    : "";
            });

            var result = await new BatchPrintService(_services.Engine).RunAsync(
                items, profile, paper, printer,
                async (document, plan, ct) =>
                {
                    var job = await _services.PrintJobs.SubmitAsync(document, plan, null, ct);
                    return job.SheetsSent;
                },
                progress, _cts.Token);

            for (var i = 0; i < result.Outcomes.Count && i < _files.Count; i++)
            {
                var outcome = result.Outcomes[i];
                _files[i].Status = outcome.Succeeded
                    ? Loc.F("BpDoneRow", outcome.Sheets)
                    : Loc.F("BpFailedRow", outcome.Error ?? "");
            }

            StatusText.Text = Loc.F("BpSummary", result.Succeeded, result.Failed, result.TotalSheets);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.Get("BpStopped");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка пакетной печати");
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            StopButton.Visibility = Visibility.Collapsed;
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = Loc.Get("PrintCancelling");
    }

    private void UpdateStatus() =>
        StatusText.Text = _files.Count == 0
            ? Loc.Get("BpEmpty")
            : Loc.F("BpCount", _files.Count);

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Пакет в работе — окно не бросаем: половина файлов уже могла уйти.
        if (_running) { e.Cancel = true; return; }
        _printers.Dispose();
    }
}
