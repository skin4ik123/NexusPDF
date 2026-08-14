using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using Serilog;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Пакетная обработка: одна операция (оптимизация/пароль/экспорт PNG) для
/// набора PDF-файлов, результаты — в отдельную папку под теми же именами.
/// Каждый файл получает свой статус; ошибки одного файла не прерывают
/// остальные.
/// </summary>
public partial class BatchDialog : Window
{
    private sealed partial class Row : ObservableObject
    {
        public required string Path { get; init; }
        public string Name => System.IO.Path.GetFileName(Path);

        [ObservableProperty]
        private string _status = Loc.Get("BatchStatusPending");
    }

    private readonly AppServices _services;
    private readonly ObservableCollection<Row> _rows = new();
    private CancellationTokenSource? _cts;

    private BatchDialog(AppServices services)
    {
        InitializeComponent();
        _services = services;
        FileList.ItemsSource = _rows;
    }

    public static void Run(Window? owner, AppServices services)
    {
        var dialog = new BatchDialog(services);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Loc.Get("PdfFilter"), Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
        {
            if (_rows.All(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
                _rows.Add(new Row { Path = path });
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var row in FileList.SelectedItems.Cast<Row>().ToList())
            _rows.Remove(row);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.Get("BatchOutput") };
        if (dialog.ShowDialog(this) == true)
            OutputBox.Text = dialog.FolderName;
    }

    private void OnOperationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordLabel == null) return; // событие при инициализации XAML
        var protect = OperationCombo.SelectedIndex == 1;
        PasswordLabel.Visibility = protect ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Visibility = protect ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        var outputDir = OutputBox.Text;
        if (_rows.Count == 0 || string.IsNullOrWhiteSpace(outputDir))
        {
            SummaryLabel.Text = Loc.Get("BatchNeedFiles");
            return;
        }
        var operation = OperationCombo.SelectedIndex;
        var password = PasswordBox.Password;
        if (operation == 1 && password.Length == 0)
        {
            SummaryLabel.Text = Loc.Get("BatchPasswordNeed");
            return;
        }
        if (operation is 0 or 1 && !_services.Qpdf.IsAvailable)
        {
            SummaryLabel.Text = _services.Qpdf.UnavailableReason;
            return;
        }

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var ok = 0;
        var failed = 0;
        try
        {
            Directory.CreateDirectory(outputDir);
            foreach (var row in _rows.ToList())
            {
                if (_cts.Token.IsCancellationRequested) break;
                row.Status = Loc.Get("BatchStatusWorking");
                try
                {
                    await ProcessAsync(row.Path, outputDir, operation, password, _cts.Token);
                    row.Status = Loc.Get("BatchStatusOk");
                    ok++;
                }
                catch (OperationCanceledException)
                {
                    row.Status = Loc.Get("BatchStatusPending");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Пакетная обработка: {Path}", row.Path);
                    row.Status = Loc.F("BatchStatusError", Shorten(ex.Message));
                    failed++;
                }
            }
            SummaryLabel.Text = Loc.F("BatchSummary", ok, failed);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private async Task ProcessAsync(string sourcePath, string outputDir, int operation, string password, CancellationToken ct)
    {
        var name = Path.GetFileName(sourcePath);
        var targetPath = Path.Combine(outputDir, name);
        if (operation is 0 or 1 &&
            string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfEngineException(Loc.Get("BatchSameFolder"));

        switch (operation)
        {
            case 0:
                await _services.Qpdf.OptimizeAsync(sourcePath, targetPath, linearize: true, ct);
                break;
            case 1:
                await _services.Qpdf.EncryptAsync(sourcePath, targetPath, password, null, ct);
                break;
            case 2:
            {
                OpenedDocument document;
                try
                {
                    document = await OpenedDocument.OpenAsync(_services.Engine, sourcePath, null, ct);
                }
                catch (PdfPasswordRequiredException)
                {
                    throw new PdfEngineException(Loc.Get("BatchProtectedInput"));
                }
                await using (document)
                {
                    var imagesDir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(name));
                    Directory.CreateDirectory(imagesDir);
                    var baseName = Path.GetFileNameWithoutExtension(name);
                    await _services.Convert.ExportImagesAsync(
                        document, null, 150,
                        async (image, pageIndex, token) =>
                        {
                            var file = Path.Combine(imagesDir, $"{baseName}-{pageIndex + 1:000}.png");
                            await File.WriteAllBytesAsync(file, ImageEncoder.Encode(image, jpeg: false, 150), token);
                        },
                        null, ct);
                }
                break;
            }
        }
    }

    private static string Shorten(string message) =>
        message.Length <= 60 ? message : message[..57] + "…";

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        AddButton.IsEnabled = !running;
        RemoveButton.IsEnabled = !running;
        OperationCombo.IsEnabled = !running;
        if (running)
            SummaryLabel.Text = "";
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Закрытие во время прогона = отмена после текущего файла.
        _cts?.Cancel();
    }
}
