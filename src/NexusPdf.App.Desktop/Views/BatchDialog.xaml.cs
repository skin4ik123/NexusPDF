using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Application;
using NexusPdf.Export;
using NexusPdf.Pdf.Abstractions;
using Serilog;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Пакетная обработка: одна операция для набора PDF-файлов, результаты — в
/// отдельную папку под теми же именами. Каждый файл получает свой статус;
/// ошибки одного файла не прерывают остальные.
///
/// Операции те же, что и поодиночке: оптимизация, пароль, картинки, сжатие,
/// улучшение сканов, распознавание, Word и Excel. Ради этого пакетная
/// обработка и нужна — сорок счетов в Excel одним заходом, а не по одному.
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
        if (operation == 5 && !_services.Ocr.IsAvailable)
        {
            SummaryLabel.Text = _services.Ocr.UnavailableReason ?? Loc.Get("OcrError");
            return;
        }

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var ok = 0;
        var failed = 0;
        // Уже занятые имена результатов: одноимённые входы из разных папок
        // и файлы прошлых прогонов получают суффикс « (2)», а не затираются.
        var takenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(outputDir);

            // Файлы обрабатываются по нескольку разом: они друг от друга не
            // зависят, а сорок счетов по очереди — это как раз то ожидание,
            // ради избавления от которого пакетную обработку и открывают.
            // Сколько именно — решает машина: на одноядерной будет ровно как
            // раньше, по одному.
            var workers = NexusPdf.Export.ParallelWork.Workers(items: _rows.Count);
            var status = new Progress<(Row Row, string Text)>(update => update.Row.Status = update.Text);

            await Parallel.ForEachAsync(
                _rows.ToList(),
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = CancellationToken.None },
                async (row, _) =>
                {
                    // Отмена — строго между файлами: обрыв посреди записи
                    // оставил бы обрубок вместо результата.
                    if (_cts.Token.IsCancellationRequested) return;
                    ((IProgress<(Row, string)>)status).Report((row, Loc.Get("BatchStatusWorking")));
                    try
                    {
                        await ProcessAsync(row.Path, outputDir, operation, password, takenNames, _cts.Token);
                        ((IProgress<(Row, string)>)status).Report((row, Loc.Get("BatchStatusOk")));
                        Interlocked.Increment(ref ok);
                    }
                    catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
                    {
                        ((IProgress<(Row, string)>)status).Report((row, Loc.Get("BatchStatusPending")));
                    }
                    catch (OperationCanceledException)
                    {
                        // Не отмена пользователя, а 5-минутный таймаут qpdf.
                        ((IProgress<(Row, string)>)status).Report(
                            (row, Loc.F("BatchStatusError", Loc.Get("BatchTimeout"))));
                        Interlocked.Increment(ref failed);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Пакетная обработка: {Path}", row.Path);
                        ((IProgress<(Row, string)>)status).Report(
                            (row, Loc.F("BatchStatusError", Shorten(ex.Message))));
                        Interlocked.Increment(ref failed);
                    }
                });

            SummaryLabel.Text = Loc.F("BatchSummary", ok, failed);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    /// <summary>
    /// Свободное имя в папке результатов: занятые (набором или диском) получают
    /// « (2)», « (3)»…
    ///
    /// Файлы обрабатываются одновременно, поэтому выбор имени защищён замком:
    /// два потока, одновременно проверившие «свободно», записали бы результат
    /// в один и тот же файл, и один из них пропал бы бесследно.
    /// </summary>
    private static readonly object NameGate = new();

    private static string UniqueTarget(string outputDir, string fileName, HashSet<string> takenNames, bool directory = false)
    {
        lock (NameGate)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var candidate = fileName;
            var n = 2;
            while (takenNames.Contains(candidate) ||
                   (directory ? Directory.Exists(Path.Combine(outputDir, candidate))
                              : File.Exists(Path.Combine(outputDir, candidate))))
                candidate = $"{stem} ({n++}){extension}";
            takenNames.Add(candidate);
            return Path.Combine(outputDir, candidate);
        }
    }

    private async Task ProcessAsync(
        string sourcePath, string outputDir, int operation, string password,
        HashSet<string> takenNames, CancellationToken ct)
    {
        var name = Path.GetFileName(sourcePath);

        switch (operation)
        {
            case 0 or 1:
            {
                // Запароленный вход qpdf не возьмёт — честный отказ заранее.
                try
                {
                    await using var probe = await _services.Engine.OpenAsync(sourcePath, null, CancellationToken.None);
                }
                catch (PdfPasswordRequiredException)
                {
                    throw new PdfEngineException(Loc.Get("BatchProtectedInput"));
                }

                var targetPath = UniqueTarget(outputDir, name, takenNames);
                // qpdf пишет во временный файл: сбой или таймаут не оставляют
                // в папке результатов обрубок под финальным именем.
                var tmp = targetPath + ".nexustmp-qpdf";
                try
                {
                    if (operation == 0)
                        await _services.Qpdf.OptimizeAsync(sourcePath, tmp, linearize: true, CancellationToken.None);
                    else
                        await _services.Qpdf.EncryptAsync(sourcePath, tmp, password, null, CancellationToken.None);
                    File.Move(tmp, targetPath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* лучшая попытка */ }
                }
                break;
            }
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
                    var baseName = Path.GetFileNameWithoutExtension(name);
                    var imagesDir = UniqueTarget(outputDir, baseName, takenNames, directory: true);
                    Directory.CreateDirectory(imagesDir);
                    await _services.Convert.ExportImagesAsync(
                        document, null, 150,
                        async (image, pageIndex, effectiveDpi, token) =>
                        {
                            var file = Path.Combine(imagesDir, $"{baseName}-{pageIndex + 1:000}.png");
                            await File.WriteAllBytesAsync(file,
                                ImageEncoder.Encode(image, jpeg: false, effectiveDpi), token);
                        },
                        null, ct);
                }
                break;
            }
            default:
                await ProcessDocumentAsync(sourcePath, outputDir, operation, name, takenNames, ct);
                break;
        }
    }

    /// <summary>
    /// Операции, которым нужен открытый документ: сжатие, улучшение сканов,
    /// распознавание и экспорт в Word и Excel.
    ///
    /// Распознавание меняет сам документ, поэтому результат сохраняется КОПИЕЙ
    /// в папку результатов: пакетная обработка не имеет права переписывать
    /// исходники.
    /// </summary>
    private async Task ProcessDocumentAsync(
        string sourcePath, string outputDir, int operation, string name,
        HashSet<string> takenNames, CancellationToken ct)
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
            var stem = Path.GetFileNameWithoutExtension(name);
            switch (operation)
            {
                case 3:
                {
                    // Умный режим сам решает по содержимому, скан перед ним
                    // или вёрстка: у скана запас по разрешению больше.
                    var summary = await document.PrimaryHandle.GetImageSummaryAsync(
                        NexusPdf.Ux.DocumentImageProfile.SampleLimit, ct);
                    var profile = new NexusPdf.Ux.DocumentImageProfile(
                        document.PrimaryHandle.Info.PageCount, summary.Images,
                        summary.TextLength, summary.SampledPages, summary.AverageImageDpi);
                    var preset = NexusPdf.Ux.CompressionPresets.Resolve(
                        NexusPdf.Ux.CompressionPresetKind.Smart, profile);
                    await _services.Tools.CompressImagesCopyAsync(
                        document, UniqueTarget(outputDir, name, takenNames),
                        preset.Dpi, preset.Quality, ImageEncoder.EncodeChosen, ct,
                        preset.StructureOnly);
                    break;
                }
                case 4:
                    await _services.Tools.EnhanceScansCopyAsync(
                        document, UniqueTarget(outputDir, name, takenNames),
                        new ScanEnhanceOptions(), null, ct);
                    break;
                case 5:
                    await _services.Ocr.RecognizeAsync(document, null, null, ct);
                    await _services.SaveService.SaveCopyAsync(
                        document, UniqueTarget(outputDir, name, takenNames), ct);
                    break;
                case 6:
                    await _services.Convert.ExportToWordAsync(
                        document, UniqueTarget(outputDir, stem + ".docx", takenNames), null,
                        new WordExportOptions(Encode: ImageEncoder.EncodeForDocument),
                        new PageAnalysisOptions(RecognizeScans: _services.Ocr.IsAvailable),
                        null, ct);
                    break;
                case 7:
                    await _services.Convert.ExportToExcelAsync(
                        document, UniqueTarget(outputDir, stem + ".xlsx", takenNames), null,
                        new ExcelExportOptions(DecimalIsComma: Loc.CurrentLanguage != "en"),
                        new PageAnalysisOptions(RecognizeScans: _services.Ocr.IsAvailable),
                        null, ct);
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
