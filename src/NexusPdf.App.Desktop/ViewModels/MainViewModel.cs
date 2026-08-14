using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.App.Desktop.Views;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using Serilog;

namespace NexusPdf.App.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MainViewModel(AppServices services)
    {
        _services = services;
        RecentFiles = new ObservableCollection<string>(services.Settings.RecentFiles);
    }

    /// <summary>Окно, обслуживающее эту модель (для владения диалогами).</summary>
    public Window? OwnerWindow { get; set; }

    public ObservableCollection<DocumentViewModel> Documents { get; } = new();
    public ObservableCollection<string> RecentFiles { get; }

    public string EngineName => _services.Engine.EngineName;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public bool HasLastSession => _services.Settings.LastSessionFiles.Count > 0;

    /// <summary>Функции qpdf (пароль, оптимизация) видимы только при наличии движка.</summary>
    public bool HasPdfTools => _services.Tools.IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocuments))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty]
    private bool _showCrashRestoreBanner;

    public bool HasDocuments => Documents.Count > 0;

    public string WindowTitle => ActiveDocument is { } doc
        ? Loc.F("WindowTitle", doc.Title)
        : Loc.Get("AppName");

    // ----- Открытие -----

    [RelayCommand]
    private async Task Open()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Loc.Get("PdfFilter") + "|" + Loc.Get("AllFilter"),
            Multiselect = true,
        };
        if (dialog.ShowDialog(OwnerWindow) == true)
            await OpenFilesAsync(dialog.FileNames);
    }

    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            await OpenSingleAsync(path);
    }

    private async Task OpenSingleAsync(string path)
    {
        var existing = Documents.FirstOrDefault(d =>
            string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActiveDocument = existing;
            return;
        }

        string? password = null;
        var wrongAttempt = false;
        while (true)
        {
            try
            {
                var opened = await OpenedDocument.OpenAsync(_services.Engine, path, password, CancellationToken.None);
                var vm = new DocumentViewModel(opened, _services.Cache);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(DocumentViewModel.Title) or nameof(DocumentViewModel.IsDirty))
                        OnPropertyChanged(nameof(WindowTitle));
                };
                Documents.Add(vm);
                ActiveDocument = vm;
                OnPropertyChanged(nameof(HasDocuments));

                _services.Settings.TouchRecent(path);
                SyncRecent();
                UpdateSessionSnapshot(); // включает SaveSettings
                Log.Information("Открыт документ: {Path}, страниц: {Pages}", path, vm.PageCount);
                return;
            }
            catch (PdfPasswordRequiredException)
            {
                password = PasswordDialog.Show(OwnerWindow, Path.GetFileName(path), wrongAttempt);
                if (password == null)
                    return; // пользователь отказался
                wrongAttempt = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Не удалось открыть {Path}", path);
                ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                    Loc.F("ErrorOpenFile", Path.GetFileName(path)), ex.ToString());
                return;
            }
        }
    }

    [RelayCommand]
    private Task OpenRecent(string path) => OpenSingleAsync(path);

    [RelayCommand]
    private async Task RestoreSession()
    {
        ShowCrashRestoreBanner = false;
        await OpenFilesAsync(_services.Settings.LastSessionFiles.Where(File.Exists).ToList());
    }

    private void SyncRecent()
    {
        RecentFiles.Clear();
        foreach (var file in _services.Settings.RecentFiles)
            RecentFiles.Add(file);
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>
    /// Актуальный список открытых файлов пишется в настройки при каждом
    /// открытии/закрытии: только так восстановление после краха работает —
    /// при аварийном завершении OnExit не выполняется вовсе.
    /// </summary>
    public void UpdateSessionSnapshot()
    {
        _services.Settings.LastSessionFiles = WindowManager.CollectOpenFiles().ToList();
        _services.SaveSettings();
    }

    /// <summary>Снимок для «восстановить прошлую сессию» перед закрытием последнего окна.</summary>
    public void SnapshotBeforeExit()
    {
        var files = WindowManager.CollectOpenFiles().ToList();
        if (files.Count > 0)
        {
            _services.Settings.LastSessionFiles = files;
            _services.SaveSettings();
        }
    }

    private bool IsFileOpenElsewhere(DocumentViewModel current, string targetPath)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        return WindowManager.AllViewModels()
            .SelectMany(vm => vm.Documents)
            .Where(d => !ReferenceEquals(d, current))
            .SelectMany(d => d.Document.Handles.Values)
            .Any(h => string.Equals(Path.GetFullPath(h.FilePath), fullTarget, StringComparison.OrdinalIgnoreCase));
    }

    private bool RejectIfTargetOpenElsewhere(DocumentViewModel doc, string targetPath)
    {
        if (!IsFileOpenElsewhere(doc, targetPath)) return false;
        ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
            Loc.Get("FileOpenElsewhere"), targetPath);
        return true;
    }

    // ----- Сохранение -----

    [RelayCommand]
    private async Task Save()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.FilePath is not { } path)
        {
            await SaveAs();
            return;
        }
        await SaveCoreAsync(doc, path);
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (ActiveDocument is not { } doc) return;
        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = doc.Title,
            DefaultExt = ".pdf",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        await SaveCoreAsync(doc, dialog.FileName);
    }

    private async Task SaveCoreAsync(DocumentViewModel doc, string targetPath)
    {
        if (doc.IsBusy) return; // идёт печать/сохранение — не трогаем документ
        if (RejectIfTargetOpenElsewhere(doc, targetPath)) return;
        doc.IsBusy = true;
        doc.StatusText = Loc.Get("SavingStatus");
        try
        {
            await _services.SaveService.SaveAsAsync(
                doc.Document, targetPath, _services.Settings.KeepBackupOnSave, CancellationToken.None);
            doc.StatusText = Loc.F("SavedStatus", Path.GetFileName(targetPath));
            _services.Settings.TouchRecent(targetPath);
            _services.SaveSettings();
            SyncRecent();
            OnPropertyChanged(nameof(WindowTitle));
            Log.Information("Сохранено: {Path}", targetPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка сохранения {Path}", targetPath);
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorSaveFile", Path.GetFileName(targetPath)), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExtractSelected(IList? selection)
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || selection is null || selection.Count == 0) return;
        var indices = selection.Cast<PageViewModel>().Select(p => p.LogicalIndex).OrderBy(i => i).ToArray();

        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + "-pages.pdf",
            DefaultExt = ".pdf",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        if (RejectIfTargetOpenElsewhere(doc, dialog.FileName)) return;

        doc.IsBusy = true;
        try
        {
            await _services.SaveService.ExtractAsync(doc.Document, indices, dialog.FileName, CancellationToken.None);
            doc.StatusText = Loc.F("ExtractDone", Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка извлечения страниц");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorSaveFile", Path.GetFileName(dialog.FileName)), ex.ToString());
        }
        finally
        {
            doc.IsBusy = false;
        }
    }

    // ----- Оформление: колонтитулы, водяной знак, текст, изображения, подпись -----

    [RelayCommand]
    private void ShowHeaderFooter()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var options = HeaderFooterDialog.Show(OwnerWindow, doc.PageCount);
        if (options == null) return;
        doc.Document.Session.Apply(PageDecorator.BuildHeaderFooter(doc.Document, options));
        doc.StatusText = Loc.Get("DecorApplied");
    }

    [RelayCommand]
    private void ShowWatermark()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var options = WatermarkDialog.Show(OwnerWindow, doc.PageCount);
        if (options == null) return;
        doc.Document.Session.Apply(PageDecorator.BuildWatermark(doc.Document, options));
        doc.StatusText = Loc.Get("DecorApplied");
    }

    [RelayCommand]
    private void AddTextOverlay()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var result = AddTextDialog.Show(OwnerWindow);
        if (result == null) return;
        doc.BeginPlacement((_, xPt, yPt) =>
            new NexusPdf.Pdf.Abstractions.TextOverlay(
                result.Text, xPt, yPt, result.FontSizePt, result.ColorArgb, 0));
    }

    [RelayCommand]
    private void InsertImageOverlay()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = Loc.Get("ImageFilter") };
        if (dialog.ShowDialog(OwnerWindow) != true) return;

        LoadedImage image;
        try
        {
            image = ImageLoader.FromFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorOpenFile", Path.GetFileName(dialog.FileName)), ex.ToString());
            return;
        }

        var widthPercent = ImagePlaceDialog.Show(OwnerWindow, ImageLoader.Preview(image));
        if (widthPercent == null) return;
        BeginImagePlacement(doc, image, widthPercent.Value);
    }

    [RelayCommand]
    private void InsertSignature()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var pick = SignatureLibraryDialog.Show(OwnerWindow, _services.Signatures);
        if (pick == null) return;
        BeginImagePlacement(doc, pick.Image, pick.WidthPercent);
    }

    private static void BeginImagePlacement(DocumentViewModel doc, LoadedImage image, double widthPercent)
    {
        doc.BeginPlacement((page, xPt, yPt) =>
        {
            var widthPt = page.SizePt.WidthPoints * widthPercent / 100.0;
            var heightPt = widthPt * image.Aspect;
            return new NexusPdf.Pdf.Abstractions.ImageOverlay(
                image.Bgra, image.PixelWidth, image.PixelHeight,
                xPt - widthPt / 2, yPt - heightPt / 2, widthPt, heightPt);
        });
    }

    // ----- Печать и инструменты qpdf -----

    [RelayCommand]
    private async Task PrintActive()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || OwnerWindow is null) return;
        await _services.Print.PrintInteractiveAsync(doc, OwnerWindow);
    }

    [RelayCommand]
    private async Task ProtectWithPassword()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || !_services.Tools.IsAvailable) return;

        var password = PasswordSetDialog.Show(OwnerWindow);
        if (password == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + "-protected.pdf",
            DefaultExt = ".pdf",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        if (RejectIfTargetOpenElsewhere(doc, dialog.FileName)) return;

        doc.IsBusy = true;
        doc.StatusText = Loc.Get("SavingStatus");
        try
        {
            await _services.Tools.ProtectCopyAsync(doc.Document, dialog.FileName, password, null, CancellationToken.None);
            doc.StatusText = Loc.F("ProtectDone", Path.GetFileName(dialog.FileName));
            Log.Information("Создана защищённая копия: {Path}", dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка создания защищённой копии");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorSaveFile", Path.GetFileName(dialog.FileName)), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OptimizeCopy()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || !_services.Tools.IsAvailable) return;

        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + "-optimized.pdf",
            DefaultExt = ".pdf",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        if (RejectIfTargetOpenElsewhere(doc, dialog.FileName)) return;

        doc.IsBusy = true;
        doc.StatusText = Loc.Get("OptimizingStatus");
        try
        {
            var result = await _services.Tools.OptimizeCopyAsync(doc.Document, dialog.FileName, CancellationToken.None);
            doc.StatusText = Loc.F("OptimizeDone",
                FormatBytes(result.BytesBefore), FormatBytes(result.BytesAfter));
            Log.Information("Оптимизирована копия: {Path} ({Before} → {After})",
                dialog.FileName, result.BytesBefore, result.BytesAfter);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка оптимизации");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorSaveFile", Path.GetFileName(dialog.FileName)), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} МБ",
        >= 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes} Б",
    };

    // ----- Вкладки и окна -----

    [RelayCommand]
    private async Task CloseTab(DocumentViewModel? doc)
    {
        doc ??= ActiveDocument;
        if (doc == null) return;
        if (doc.IsBusy) return; // нельзя закрывать документ под печатью/сохранением

        if (doc.IsDirty)
        {
            var choice = UnsavedChangesDialog.ShowForSingle(OwnerWindow, doc);
            if (choice == UnsavedChangesResult.Cancel) return;
            if (choice == UnsavedChangesResult.Save)
            {
                await SaveCoreAsync(doc, doc.FilePath ?? throw new InvalidOperationException());
                if (doc.IsDirty) return; // сохранение не удалось — не закрываем
            }
        }

        Documents.Remove(doc);
        ActiveDocument = Documents.LastOrDefault();
        OnPropertyChanged(nameof(HasDocuments));
        await doc.DisposeAsync();
        UpdateSessionSnapshot();
    }

    /// <summary>Попытка закрыть все вкладки окна (при закрытии окна). false — пользователь отменил.</summary>
    public async Task<bool> TryCloseAllAsync()
    {
        if (Documents.Any(d => d.IsBusy))
            return false; // печать или сохранение ещё идут

        var dirty = Documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count > 0)
        {
            var (result, toSave) = UnsavedChangesDialog.ShowForMany(OwnerWindow, dirty);
            if (result == UnsavedChangesResult.Cancel)
                return false;
            if (result == UnsavedChangesResult.Save)
            {
                foreach (var doc in toSave)
                {
                    if (doc.FilePath is { } path)
                        await SaveCoreAsync(doc, path);
                    if (doc.IsDirty)
                        return false; // сохранение сорвалось — отменяем закрытие
                }
            }
        }

        foreach (var doc in Documents.ToList())
        {
            Documents.Remove(doc);
            await doc.DisposeAsync();
        }
        return true;
    }

    [RelayCommand]
    private void NewWindow() => WindowManager.OpenWindow(_services, null);

    [RelayCommand]
    private void DetachTab(DocumentViewModel? doc)
    {
        doc ??= ActiveDocument;
        if (doc == null || Documents.Count == 0) return;
        Documents.Remove(doc);
        ActiveDocument = Documents.LastOrDefault();
        OnPropertyChanged(nameof(HasDocuments));
        WindowManager.OpenWindow(_services, doc);
    }

    // ----- Проксирование команд активного документа (горячие клавиши окна) -----

    [RelayCommand] private void UndoActive() => ActiveDocument?.UndoCommand.Execute(null);
    [RelayCommand] private void RedoActive() => ActiveDocument?.RedoCommand.Execute(null);
    [RelayCommand] private void ZoomInActive() => ActiveDocument?.ZoomInCommand.Execute(null);
    [RelayCommand] private void ZoomOutActive() => ActiveDocument?.ZoomOutCommand.Execute(null);
    [RelayCommand] private void ZoomActualActive() => ActiveDocument?.ZoomActualCommand.Execute(null);
    [RelayCommand] private void ToggleFindActive() => ActiveDocument?.ToggleFindCommand.Execute(null);
    [RelayCommand] private void FitWidthActive() => ActiveDocument?.FitWidthCommand.Execute(null);
    [RelayCommand] private void FitPageActive() => ActiveDocument?.FitPageCommand.Execute(null);

    [RelayCommand]
    private void NextTab()
    {
        if (Documents.Count < 2 || ActiveDocument == null) return;
        var index = Documents.IndexOf(ActiveDocument);
        ActiveDocument = Documents[(index + 1) % Documents.Count];
    }

    [RelayCommand]
    private void PreviousTab()
    {
        if (Documents.Count < 2 || ActiveDocument == null) return;
        var index = Documents.IndexOf(ActiveDocument);
        ActiveDocument = Documents[(index - 1 + Documents.Count) % Documents.Count];
    }

    [RelayCommand]
    private void ToggleOrganize()
    {
        if (ActiveDocument is { } doc)
            doc.IsOrganizeMode = !doc.IsOrganizeMode;
    }

    [RelayCommand]
    private async Task CloseActiveTab() => await CloseTab(null);

    // ----- Настройки -----

    [RelayCommand]
    private void SetTheme(string theme)
    {
        _services.Settings.Theme = theme;
        _services.SaveSettings();
        ThemeManager.Apply(theme);
    }

    [RelayCommand]
    private void SetLanguage(string language)
    {
        if (_services.Settings.Language == language) return;
        _services.Settings.Language = language;
        _services.SaveSettings();
        MessageBox.Show(Loc.Get("LanguageRestartNote"), Loc.Get("AppName"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void About()
    {
        MessageBox.Show(Loc.Get("AboutText"), Loc.Get("About"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
