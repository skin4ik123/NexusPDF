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
                _services.SaveSettings();
                SyncRecent();
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
        if (ActiveDocument is not { } doc || selection is null || selection.Count == 0) return;
        var indices = selection.Cast<PageViewModel>().Select(p => p.LogicalIndex).OrderBy(i => i).ToArray();

        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + "-pages.pdf",
            DefaultExt = ".pdf",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;

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

    // ----- Вкладки и окна -----

    [RelayCommand]
    private async Task CloseTab(DocumentViewModel? doc)
    {
        doc ??= ActiveDocument;
        if (doc == null) return;

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
    }

    /// <summary>Попытка закрыть все вкладки окна (при закрытии окна). false — пользователь отменил.</summary>
    public async Task<bool> TryCloseAllAsync()
    {
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
