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
using NexusPdf.Export;
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
        Panels = NexusPdf.Ux.PanelLayout.FromSetting(services.Settings.Panels);
        LoadPanelWidths();
    }

    /// <summary>Окно, обслуживающее эту модель (для владения диалогами).</summary>
    public Window? OwnerWindow { get; set; }

    private Services.Ux.UxCommandHub? _ux;

    /// <summary>
    /// Единая точка исполнения команд: контекстные меню, палитра и горячие
    /// клавиши ходят сюда, а не дублируют вызовы у себя.
    /// </summary>
    public Services.Ux.UxCommandHub Ux => _ux ??= new Services.Ux.UxCommandHub(this, _services);

    // ----- Видимость панелей -----

    private NexusPdf.Ux.PanelLayout? _savedPanels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuickPanel))]
    [NotifyPropertyChangedFor(nameof(ShowToolRail))]
    [NotifyPropertyChangedFor(nameof(ShowSidePanel))]
    [NotifyPropertyChangedFor(nameof(ShowToolsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowPropertyPanel))]
    [NotifyPropertyChangedFor(nameof(ShowStatusBar))]
    private NexusPdf.Ux.PanelLayout _panels = NexusPdf.Ux.PanelLayout.Default;

    public bool ShowQuickPanel => Panels.QuickPanel;
    public bool ShowToolRail => Panels.ToolRail;
    public bool ShowSidePanel => Panels.SidePanel;
    public bool ShowToolsPanel => Panels.Tools;
    public bool ShowPropertyPanel => Panels.Properties;
    public bool ShowStatusBar => Panels.StatusBar;

    // ----- Ширина панелей -----

    /// <summary>
    /// Границы ширины панели. Уже 180 — в неё не помещается ни один список,
    /// шире 640 — панель начинает съедать документ, ради которого программа и
    /// открыта.
    /// </summary>
    private const double MinPanelWidth = 180;
    private const double MaxPanelWidth = 640;

    [ObservableProperty] private double _sidePanelWidth = 176;
    [ObservableProperty] private double _toolsPanelWidth = 270;
    [ObservableProperty] private double _commentsPanelWidth = 270;
    [ObservableProperty] private double _propertiesPanelWidth = 250;

    public double PanelWidth(string? name) => name switch
    {
        "SidePanel" => SidePanelWidth,
        "Tools" => ToolsPanelWidth,
        "Comments" => CommentsPanelWidth,
        "Properties" => PropertiesPanelWidth,
        _ => 0,
    };

    /// <summary>
    /// Новая ширина панели по сдвигу мыши от начала перетаскивания. Знак
    /// зависит от стороны: боковая панель растёт вправо, правые — влево.
    /// </summary>
    public void SetPanelWidth(string? name, double startWidth, double offset)
    {
        switch (name)
        {
            case "SidePanel":
                SidePanelWidth = Clamp(startWidth + offset);
                break;
            case "Tools":
                ToolsPanelWidth = Clamp(startWidth - offset);
                break;
            case "Comments":
                CommentsPanelWidth = Clamp(startWidth - offset);
                break;
            case "Properties":
                PropertiesPanelWidth = Clamp(startWidth - offset);
                break;
            default:
                return;
        }
        _services.Settings.PanelWidths =
            $"{SidePanelWidth:F0},{ToolsPanelWidth:F0},{CommentsPanelWidth:F0},{PropertiesPanelWidth:F0}";
    }

    /// <summary>Ширины сохраняются по окончании перетаскивания, а не на каждый пиксель.</summary>
    public void SavePanelWidths() => _services.SaveSettings();

    private static double Clamp(double value) => Math.Clamp(value, MinPanelWidth, MaxPanelWidth);

    private void LoadPanelWidths()
    {
        var parts = (_services.Settings.PanelWidths ?? "").Split(',');
        if (parts.Length != 4) return;
        if (double.TryParse(parts[0], out var side)) SidePanelWidth = Clamp(side);
        if (double.TryParse(parts[1], out var tools)) ToolsPanelWidth = Clamp(tools);
        if (double.TryParse(parts[2], out var comments)) CommentsPanelWidth = Clamp(comments);
        if (double.TryParse(parts[3], out var props)) PropertiesPanelWidth = Clamp(props);
    }

    private Services.Ux.ToolsPanel? _tools;

    /// <summary>Панель инструментов: всё, что умеет программа, видно списком.</summary>
    public Services.Ux.ToolsPanel Tools => _tools ??= new Services.Ux.ToolsPanel(
        Ux, _services.Settings.ToolsLayout,
        layout =>
        {
            _services.Settings.ToolsLayout = layout;
            _services.SaveSettings();
        },
        _services.Settings.RecentCommands,
        recent =>
        {
            _services.Settings.RecentCommands = recent.ToList();
            _services.SaveSettings();
        },
        _services.Settings.ToolsExpandedGroups,
        expanded =>
        {
            _services.Settings.ToolsExpandedGroups = expanded;
            _services.SaveSettings();
        });

    /// <summary>
    /// Выполнить команду реестра по её идентификатору. Нужна кнопкам панелей:
    /// они не должны знать про обработчики, иначе панель и меню разъедутся.
    /// </summary>
    [RelayCommand]
    private void RunUx(string? commandId)
    {
        if (string.IsNullOrEmpty(commandId)) return;
        Ux.Invoke(commandId, new Services.Ux.UxTarget
        {
            Context = Ux.Snapshot(),
            Document = Ux.ActiveDocument,
        });
    }

    /// <summary>
    /// Перенос страниц в ДРУГОЙ открытый документ: пользователь тащит их из
    /// режима систематизации на вкладку соседа.
    ///
    /// Страницы копируются, а не изымаются: перетаскивание между документами
    /// чаще означает «возьми это и туда», а не «перенеси и убери отсюда», и
    /// потеря страниц в исходнике была бы неприятным сюрпризом. Удалить их
    /// можно тем же выделением, которое осталось на месте.
    /// </summary>
    /// <param name="insertIndex">
    /// Куда поставить. null — в конец: так работает бросок на саму вкладку,
    /// где места ещё не видно.
    /// </param>
    public async Task DropPagesOnDocumentAsync(
        DocumentViewModel target, DocumentViewModel source, IReadOnlyList<int> logicalIndices,
        int? insertIndex = null)
    {
        if (ReferenceEquals(target, source) || logicalIndices.Count == 0) return;
        if (target.IsBusy || source.IsBusy) return;

        target.IsBusy = true;
        try
        {
            var at = Math.Clamp(
                insertIndex ?? target.Document.Session.Model.Pages.Count,
                0, target.Document.Session.Model.Pages.Count);
            var inserted = await target.Document.InsertPagesFromAsync(
                _services.Engine, source.Document, logicalIndices, at, CancellationToken.None);
            ActiveDocument = target;
            target.StatusText = Loc.F("DropPagesDone", inserted, source.Title);
            Log.Information("Перенос страниц: {Count} из {Source} в {Target}",
                inserted, source.Title, target.Title);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка переноса страниц между документами");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"), Loc.Get("DropPagesFailed"), ex.ToString());
        }
        finally
        {
            target.IsBusy = false;
        }
    }

    /// <summary>
    /// То же самое, но через меню: «Отправить в другой документ…».
    ///
    /// Перетаскивание на вкладку удобно, но требует и точного движения мышью,
    /// и открытого организатора у обоих документов. Меню работает всегда и,
    /// в отличие от броска на вкладку, позволяет назвать место сразу.
    /// </summary>
    [RelayCommand]
    private async Task SendPagesToDocument(IReadOnlyList<int>? logicalIndices)
    {
        if (ActiveDocument is not { } source || logicalIndices is not { Count: > 0 }) return;

        var targets = Documents.Where(d => !ReferenceEquals(d, source)).ToList();
        if (targets.Count == 0)
        {
            source.StatusText = Loc.Get("UxNeedsSecondDocument");
            return;
        }

        var request = SendPagesDialog.Show(OwnerWindow, targets, logicalIndices.Count);
        if (request == null) return;

        await DropPagesOnDocumentAsync(request.Target, source, logicalIndices, request.InsertIndex);
    }

    /// <summary>
    /// Вставка файлов, брошенных из Проводника, в режим систематизации.
    /// PDF отдают свои страницы, картинки становятся страницами; всё
    /// добавляется на ту позицию, где отпустили мышь, одной операцией — одно
    /// «Отменить» возвращает как было.
    /// </summary>
    public async Task InsertDroppedFilesAsync(
        DocumentViewModel doc, IReadOnlyList<string> files, int insertIndex)
    {
        if (doc.IsBusy || files.Count == 0) return;

        doc.IsBusy = true;
        var ct = doc.Busy.Start(Loc.Get("DropFilesStatus"), canCancel: true, determinate: true);
        var progress = new Progress<(int Done, int Total)>(p =>
            doc.Busy.Report(p.Total > 0 ? (double)p.Done / p.Total : 0,
                Loc.F("DropFilesProgress", p.Done, p.Total)));
        try
        {
            var result = await doc.Document.InsertFilesAsync(
                _services.Engine, files, insertIndex, ImageEncoder.DecodeAsPageSpec,
                NexusPdf.Infrastructure.AppPaths.DroppedFilesFolder, progress, ct,
                ConvertOfficeToPdfAsync);

            doc.StatusText = result.PagesAdded > 0
                ? Loc.F("DropFilesDone", result.PagesAdded, result.FilesUsed)
                : Loc.Get("DropFilesNothing");
            if (result.Skipped.Count > 0)
            {
                // Пропущенное не прячем: молчание здесь читается как «всё
                // получилось», а половина файлов осталась за бортом.
                var details = string.Join("\n", result.Skipped.Select(x =>
                    $"{Path.GetFileName(x.File)} — {x.Reason}"));
                ErrorDialog.Show(OwnerWindow, Loc.Get("DropFilesSkippedTitle"),
                    Loc.F("DropFilesSkipped", result.Skipped.Count), details);
            }
            Log.Information("Вставка файлов: страниц {Pages}, файлов {Files}, пропущено {Skipped}",
                result.PagesAdded, result.FilesUsed, result.Skipped.Count);
        }
        catch (OperationCanceledException)
        {
            doc.StatusText = Loc.Get("BusyCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка вставки перетащенных файлов");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"), Loc.Get("DropFilesFailed"), ex.ToString());
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    /// <summary>
    /// Документ Office → временный PDF рядом с прочими перетащенными файлами.
    /// Экспортом самого Office: ссылки, оглавление по заголовкам и теги
    /// структуры остаются живыми, чего печать в PDF-принтер не умеет.
    /// </summary>
    private async Task<string> ConvertOfficeToPdfAsync(string source, string tempFolder, CancellationToken ct)
    {
        if (!NexusPdf.Office.OfficeToPdfConverter.IsOfficeFile(source))
            throw new NotSupportedException(Loc.Get("OfficeNotSupported"));
        if (!NexusPdf.Office.OfficeToPdfConverter.CanConvert(source))
            throw new InvalidOperationException(
                NexusPdf.Office.OfficeToPdfConverter.UnavailableReason(source));

        Directory.CreateDirectory(tempFolder);
        var target = Path.Combine(tempFolder,
            $"{Path.GetFileNameWithoutExtension(source)}-{Guid.NewGuid():N}.pdf");
        var result = await _services.Office.ConvertAsync(source, target, ct);
        Log.Information("Office → PDF: {Source} через {App}", Path.GetFileName(source), result.Application);
        return result.TargetPath;
    }

    /// <summary>Отметить выполненную команду в разделе «Недавние».</summary>
    public void NoteCommandUsed(string commandId) => Tools.NoteUsed(commandId);

    /// <summary>Вернуть расположение инструментов к исходному.</summary>
    [RelayCommand]
    private void ResetToolsLayout()
    {
        Tools.Reset();
        if (ActiveDocument is { } doc)
            doc.StatusText = Loc.Get("PanelToolsReset");
    }

    /// <summary>Скрыть панель её собственной кнопкой «заехать».</summary>
    [RelayCommand]
    private void HidePanel(string? name)
    {
        if (Enum.TryParse<NexusPdf.Ux.UiPanel>(name, out var panel))
            TogglePanel(panel);
    }

    /// <summary>Показать панель значком с правого края.</summary>
    [RelayCommand]
    private void ShowPanel(string? name)
    {
        if (Enum.TryParse<NexusPdf.Ux.UiPanel>(name, out var panel))
            TogglePanel(panel);
    }

    /// <summary>Скрыть или показать одну панель.</summary>
    public void TogglePanel(NexusPdf.Ux.UiPanel panel)
    {
        // Панель комментариев принадлежит документу — у неё своё состояние.
        if (panel == NexusPdf.Ux.UiPanel.Comments)
        {
            if (ActiveDocument is { } commentsDoc)
                commentsDoc.IsCommentsVisible = !commentsDoc.IsCommentsVisible;
            Panels = Panels.With(panel, ActiveDocument?.IsCommentsVisible ?? false);
            SavePanels();
            return;
        }

        Panels = Panels.Toggle(panel);
        SavePanels();
    }

    /// <summary>
    /// «Только страница» и обратно. Возврат восстанавливает ТУ раскладку,
    /// которая была до скрытия, а не набор по умолчанию: иначе пользователь
    /// теряет свои настройки каждый раз, когда захотел увидеть страницу целиком.
    /// </summary>
    [RelayCommand]
    private void TogglePageOnly()
    {
        var (layout, saved) = Panels.TogglePageOnly(_savedPanels);
        _savedPanels = saved;
        Panels = layout;
        if (ActiveDocument is { } doc)
        {
            doc.IsCommentsVisible = layout.Comments;
            doc.StatusText = Loc.Get(layout.IsPageOnly ? "PanelOnlyPageOn" : "PanelOnlyPageOff");
        }
        SavePanels();
    }

    private void SavePanels()
    {
        _services.Settings.Panels = Panels.ToSetting();
        _services.SaveSettings();
    }

    public string CurrentWorkspace => _services.Settings.Workspace;
    public string CurrentDensity => _services.Settings.UiDensity;
    public string CurrentTheme => _services.Settings.Theme;

    private Services.Ux.QuickPanel? _quickPanel;

    /// <summary>Быстрая панель: состав задаёт пользователь, содержимое — реестр команд.</summary>
    public Services.Ux.QuickPanel QuickPanel
    {
        get
        {
            if (_quickPanel == null)
            {
                _quickPanel = new Services.Ux.QuickPanel(Ux);
                // Кнопки, добавленные в панель после того, как её настраивали,
                // доливаются один раз — иначе новая команда не появилась бы
                // никогда, а сброс к умолчанию стёр бы настройку человека.
                var (ids, generation) = NexusPdf.Ux.QuickPanelLayout.Upgrade(
                    _services.Settings.QuickCommands, _services.Settings.QuickCommandsGeneration);
                _quickPanel.Load(ids);
                if (generation != _services.Settings.QuickCommandsGeneration)
                {
                    _services.Settings.QuickCommandsGeneration = generation;
                    if (_services.Settings.QuickCommands.Count > 0)
                        _services.Settings.QuickCommands = _quickPanel.Save();
                    _services.SaveSettings();
                }
            }
            return _quickPanel;
        }
    }

    /// <summary>Показывать ли подписи рядом со значками быстрой панели.</summary>
    public bool ShowQuickPanelLabels
    {
        get => _services.Settings.QuickPanelLabels;
        set
        {
            if (_services.Settings.QuickPanelLabels == value) return;
            _services.Settings.QuickPanelLabels = value;
            _services.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Настройка состава быстрой панели.</summary>
    [RelayCommand]
    private void ConfigureQuickPanel()
    {
        var result = QuickPanelDialog.Configure(OwnerWindow, QuickPanel, ShowQuickPanelLabels);
        if (result == null) return;

        QuickPanel.Load(result.Commands);
        ShowQuickPanelLabels = result.ShowLabels;
        _services.Settings.QuickCommands = QuickPanel.Save();
        // Настроил вручную — значит панель дотянута до нынешнего поколения, и
        // убранное сейчас обновление обратно не вернёт.
        _services.Settings.QuickCommandsGeneration = NexusPdf.Ux.QuickPanelLayout.Generation;
        _services.SaveSettings();
    }

    public ObservableCollection<DocumentViewModel> Documents { get; } = new();
    public ObservableCollection<string> RecentFiles { get; }

    public string EngineName => _services.Engine.EngineName;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public bool HasLastSession => _services.Settings.LastSessionFiles.Count > 0;

    /// <summary>Функции qpdf (пароль, оптимизация) видимы только при наличии движка.</summary>
    public bool HasPdfTools => _services.Tools.IsAvailable;

    /// <summary>OCR видим только при наличии языковых моделей tessdata.</summary>
    public bool HasOcr => _services.Ocr.IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocuments))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty]
    private bool _showCrashRestoreBanner;

    public bool HasDocuments => Documents.Count > 0;

    /// <summary>
    /// Сколько файлов открывается прямо сейчас. Пока их открывают, стартовый
    /// экран не показывается: приглашать открыть файл в тот момент, когда файл
    /// уже открывается, — значит мигать пользователю ненужным экраном.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpeningFiles))]
    [NotifyPropertyChangedFor(nameof(ShowStartScreen))]
    private int _pendingOpens;

    /// <summary>
    /// Показывать ли «открываю…». Как только первый документ готов, надпись
    /// убирается, даже если следом открываются ещё файлы: показывать заставку
    /// поверх готового документа — значит прятать то, ради чего программу и
    /// запустили.
    /// </summary>
    public bool IsOpeningFiles => PendingOpens > 0 && !HasDocuments;

    /// <summary>Стартовый экран — только когда открывать действительно нечего.</summary>
    public bool ShowStartScreen => !HasDocuments && PendingOpens == 0;

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
        var list = paths as IReadOnlyList<string> ?? paths.ToList();
        if (list.Count == 0) return;

        // Место могло быть занято заранее — при создании окна под файлы из
        // проводника. Тогда занимать его второй раз не нужно.
        PendingOpens = Math.Max(PendingOpens, list.Count);
        try
        {
            foreach (var path in list)
                await OpenSingleAsync(path);
        }
        finally
        {
            PendingOpens = 0;
        }
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
                // Страницы, принесённые из другого документа и брошенные между
                // карточками: место указано человеком, туда и ставим.
                vm.PagesDroppedFromOtherDocument += (_, drop) =>
                    _ = DropPagesOnDocumentAsync(vm, drop.Source, drop.Indices, drop.InsertIndex);
                Documents.Add(vm);
                ActiveDocument = vm;
                OnPropertyChanged(nameof(HasDocuments));
        OnPropertyChanged(nameof(ShowStartScreen));
        OnPropertyChanged(nameof(IsOpeningFiles));
                _ = vm.DetectFormsAsync();       // кнопка «Формы» появится, если есть AcroForm
                _ = vm.LoadSignaturesAsync();    // значок статуса подписей в статус-баре
                _ = vm.CheckActiveContentAsync(); // предупреждение о JS/вложениях/Launch
                _ = vm.LoadPermissionsAsync();   // запрет печати обязан быть виден до попытки печатать
                RestoreWorkspace();              // панели те же, что в прошлый раз

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
        doc.CancelPlacement(); // курсор-прицел не должен жить во время сохранения
        doc.IsBusy = true;
        doc.StatusText = Loc.Get("SavingStatus");
        try
        {
            var savedDirect = SaveService.CanSaveDirect(doc.Document);
            var hadRedactions = doc.Document.Session.Model.Pages
                .Any(p => p.OverlayList.Any(o => o is NexusPdf.Pdf.Abstractions.RedactionDraft));
            await _services.SaveService.SaveAsAsync(
                doc.Document, targetPath, _services.Settings.KeepBackupOnSave, CancellationToken.None);
            var hadForms = doc.HasAcroForm;
            doc.ResetFormStateAfterSave();
            _ = doc.LoadSignaturesAsync();
            // Перекомпоновка не переносит AcroForm: поля становятся статикой.
            doc.StatusText = hadRedactions
                ? Loc.Get("RedactionApplied")
                : hadForms && !savedDirect
                    ? Loc.Get("FormFlattenedWarning")
                    : Loc.F("SavedStatus", Path.GetFileName(targetPath));
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
            doc.Busy.Finish();
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
            doc.Busy.Finish();
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

    // ----- Комментарии и аннотации -----

    [RelayCommand]
    private void AddNote()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var note = NoteDialog.Show(OwnerWindow);
        if (note == null) return;
        doc.BeginPlacement((_, xPt, yPt) =>
            new NexusPdf.Pdf.Abstractions.NoteAnnotationDraft(xPt, yPt, note.Contents, note.Author));
    }

    [RelayCommand]
    private void AddHighlight()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        // Текст выделен — размечается именно он, по строкам и настоящей
        // аннотацией Highlight. Растягивать рамку поверх собственного
        // выделения пользователю незачем.
        if (doc.MarkupSelection(NexusPdf.Pdf.Abstractions.TextMarkupKind.Highlight)) return;
        doc.BeginRectPlacement((_, rect) =>
            new NexusPdf.Pdf.Abstractions.ShapeAnnotationDraft(
                rect.X, rect.Y, rect.Width, rect.Height,
                StrokeArgb: 0x00000000, FillArgb: 0x66FDE047, BorderWidthPt: 0,
                IsEllipse: false, Contents: "", Author: Environment.UserName));
    }

    [RelayCommand]
    private void AddRectShape()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        doc.BeginRectPlacement((_, rect) =>
            new NexusPdf.Pdf.Abstractions.ShapeAnnotationDraft(
                rect.X, rect.Y, rect.Width, rect.Height,
                StrokeArgb: 0xFFDC2626, FillArgb: 0x00000000, BorderWidthPt: 2,
                IsEllipse: false, Contents: "", Author: Environment.UserName));
    }

    [RelayCommand]
    private void AddEllipseShape()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        doc.BeginRectPlacement((_, rect) =>
            new NexusPdf.Pdf.Abstractions.ShapeAnnotationDraft(
                rect.X, rect.Y, rect.Width, rect.Height,
                StrokeArgb: 0xFFDC2626, FillArgb: 0x00000000, BorderWidthPt: 2,
                IsEllipse: true, Contents: "", Author: Environment.UserName));
    }

    [RelayCommand]
    private void AddRedaction()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        // Выделенный текст вымарывается сразу по своим строкам: попадать
        // рамкой в строку мышью — лишний шанс промахнуться мимо секретного.
        if (doc.RedactSelection()) return;
        doc.BeginRectPlacement((_, rect) =>
            new NexusPdf.Pdf.Abstractions.RedactionDraft(
                rect.X, rect.Y, rect.Width, rect.Height));
        // После BeginRectPlacement: он ставит общий PlaceRectHint, а
        // предупреждение об УНИЧТОЖЕНИИ содержимого должно его перекрыть.
        doc.StatusText = Loc.Get("RedactHint");
    }

    [RelayCommand]
    private async Task ToggleCommentsActive()
    {
        if (ActiveDocument is { } doc)
            await doc.ToggleCommentsCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ToggleFormModeActive()
    {
        if (ActiveDocument is { } doc)
            await doc.ToggleFormModeCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void ShowSignatures()
    {
        if (ActiveDocument is { HasSignatures: true } doc)
            SignaturesDialog.Show(OwnerWindow, doc.Signatures);
    }

    [RelayCommand]
    private async Task SignWithCertificate()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || !_services.Tools.IsAvailable) return;

        // Фоновая инспекция подписей на больших файлах идёт секунды —
        // отказ «уже подписан» не должен зависеть от гонки с ней.
        doc.IsBusy = true;
        try { await doc.SignaturesLoaded; }
        finally { doc.IsBusy = false; }
        if (doc.HasSignatures)
        {
            // Наш конвейер нормализует файл перед подписью — существующие
            // подписи при этом были бы разрушены. Честно отказываем.
            // (SignCopyAsync дополнительно перепроверяет исходный файл сам.)
            ErrorDialog.Show(OwnerWindow, Loc.Get("SignTitle"),
                Loc.Get("SignAlreadySigned"), "");
            return;
        }

        var request = SignDialog.Show(OwnerWindow);
        if (request == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + "-signed.pdf",
            DefaultExt = ".pdf",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        if (RejectIfTargetOpenElsewhere(doc, dialog.FileName)) return;

        doc.IsBusy = true;
        doc.StatusText = Loc.Get("SigningStatus");
        try
        {
            await _services.Tools.SignCopyAsync(doc.Document, dialog.FileName,
                request.Certificate, request.Reason, request.Location,
                request.VisibleStamp, CancellationToken.None);
            doc.StatusText = Loc.F("SignDone", Path.GetFileName(dialog.FileName));
            Log.Information("Создана подписанная копия: {Path}", dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка подписания");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorSaveFile", Path.GetFileName(dialog.FileName)), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    [RelayCommand]
    private void RecognizeText()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        if (!_services.Ocr.IsAvailable)
        {
            // Движок мог отвалиться уже после старта (нет VC++ runtime,
            // антивирус заблокировал dll): вместо «мёртвого» пункта меню —
            // честная причина, и пункт скрывается.
            ErrorDialog.Show(OwnerWindow, Loc.Get("OcrTitle"),
                _services.Ocr.UnavailableReason ?? Loc.Get("OcrError"), "");
            OnPropertyChanged(nameof(HasOcr));
            return;
        }
        doc.CancelPlacement();
        doc.IsBusy = true; // клики по страницам во время распознавания игнорируются
        try
        {
            OcrDialog.Run(OwnerWindow, _services, doc);
        }
        finally
        {
            doc.IsBusy = false;
            if (!_services.Ocr.IsAvailable)
                OnPropertyChanged(nameof(HasOcr)); // движок упал во время прогона
        }
    }

    /// <summary>
    /// Разбор открытого документа для «умного» режима. Ошибка здесь не должна
    /// мешать сжатию: просто вернём «неизвестно» и возьмём настройки вёрстки.
    /// </summary>
    private async Task<NexusPdf.Ux.DocumentImageProfile> ReadImageProfileAsync(DocumentViewModel doc)
    {
        try
        {
            var summary = await doc.Document.PrimaryHandle.GetImageSummaryAsync(
                NexusPdf.Ux.DocumentImageProfile.SampleLimit, CancellationToken.None);
            return new NexusPdf.Ux.DocumentImageProfile(
                doc.Document.PrimaryHandle.Info.PageCount, summary.Images, summary.TextLength,
                summary.SampledPages, summary.AverageImageDpi);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось разобрать документ перед сжатием");
            return NexusPdf.Ux.DocumentImageProfile.Unknown;
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB"
            : (bytes / 1024.0).ToString("0") + " KB";

    /// <summary>
    /// Единое окно подготовки документа: качество страниц, вес изображений и
    /// структура файла.
    ///
    /// Раньше это были три команды, и каждая просила «сохранить как»: чтобы
    /// почистить скан и сжать его, документ приходилось делать дважды и
    /// оставлять на диске лишний файл. Здесь всё выбирается сразу, конвейер
    /// сам выдерживает порядок, а результат ложится в ОТКРЫТЫЙ документ —
    /// сохранение остаётся обычным и происходит тогда, когда решит пользователь.
    /// </summary>
    [RelayCommand]
    private async Task OptimizeDocument()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;

        // Предпросмотр строится на ТЕКУЩЕЙ странице: наклон и фон у разных
        // страниц разные, и показывать чужую было бы обманом.
        ScanPreviewPage? preview = null;
        try
        {
            var index = Math.Clamp(doc.CurrentPageNumber - 1, 0, Math.Max(0, doc.PageCount - 1));
            var size = doc.Document.GetLogicalPageSize(index);
            var scale = Math.Min(900.0 / Math.Max(1, size.WidthPoints), 1200.0 / Math.Max(1, size.HeightPoints));
            var width = Math.Max(64, (int)(size.WidthPoints * scale));
            var height = Math.Max(64, (int)(size.HeightPoints * scale));
            var image = await doc.Document.RenderLogicalPageAsync(index, width, height, CancellationToken.None);
            preview = new ScanPreviewPage(image.Bgra, image.PixelWidth, image.PixelHeight, index + 1);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось отрисовать страницу для предпросмотра обработки");
        }

        // Разбор документа ДО окна: «умный» режим сжатия и подсказки должны
        // говорить об этом файле, а не вообще.
        var profile = await ReadImageProfileAsync(doc);
        var plan = OptimizeDocumentDialog.Show(
            OwnerWindow, preview, doc.PageCount, profile, _services.Tools.IsAvailable);
        if (plan == null) return;

        doc.IsBusy = true;
        var pages = Math.Max(1, doc.PageCount);
        var ct = doc.Busy.Start(Loc.F("OptimizeRunning", Loc.Get("OptimizeStagePreparing")),
            canCancel: true, determinate: true);
        var progress = new Progress<ProcessingProgress>(p =>
        {
            var stage = Loc.Get(p.Stage switch
            {
                ProcessingStage.Enhancing => "OptimizeStageEnhancing",
                ProcessingStage.Compressing => "OptimizeStageCompressing",
                ProcessingStage.Optimizing => "OptimizeStageOptimizing",
                ProcessingStage.Applying => "OptimizeStageApplying",
                _ => "OptimizeStagePreparing",
            });
            var share = p.Total > 0 ? (double)p.Done / p.Total : 0;
            doc.Busy.Report(share, Loc.F("OptimizeRunning", stage));
            doc.StatusText = Loc.F("OptimizeRunning", stage);
        });

        try
        {
            var result = await _services.Tools.ProcessInPlaceAsync(
                doc.Document, plan, NexusPdf.Infrastructure.AppPaths.ProcessedFolder,
                ImageEncoder.EncodeChosen, progress, ct);

            // Документ переехал на обработанный источник: список страниц,
            // миниатюры и растровый кэш смотрят на прежний файл и обязаны
            // пересобраться.
            doc.ReloadAfterProcessing();

            doc.StatusText = result.BytesAfter < result.BytesBefore
                ? Loc.F("OptimizeDocDone", FormatSize(result.BytesBefore), FormatSize(result.BytesAfter))
                : Loc.F("OptimizeDoneNoGain", FormatSize(result.BytesAfter));
            Log.Information(
                "Обработка документа: {Before} → {After} байт, выровнено {Straight}, пятен {Speckles}, картинок пересжато {Images}, структура {Structure}",
                result.BytesBefore, result.BytesAfter, result.Enhance.PagesStraightened,
                result.Enhance.SpecklesRemoved, result.Recompressed, result.StructureOptimized);
        }
        catch (OperationCanceledException)
        {
            doc.StatusText = Loc.Get("BusyCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка обработки документа");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"), Loc.Get("OptimizeFailed"), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    /// <summary>Правка внешнего редактора доступна, только если редактор реально найден.</summary>
    public bool HasImageEditor => ExternalImageEditor.IsEditorAvailable();

    [RelayCommand]
    private async Task EditPageInPaint()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || doc.PageCount == 0) return;
        if (!ExternalImageEditor.IsEditorAvailable())
        {
            ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), Loc.Get("PaintNoEditor"), "");
            return;
        }

        var request = PaintEditDialog.Show(OwnerWindow, wholePage: true, _services.Ocr.IsAvailable);
        if (request == null) return;

        var pageIndex = Math.Clamp(doc.CurrentPageNumber - 1, 0, doc.PageCount - 1);
        var page = doc.Pages[pageIndex];
        doc.IsBusy = true;
        doc.StatusText = Loc.Get("PaintExporting");
        ExternalEditWorkspace? workspace = null;
        ExternalImageEditor? editor = null;
        try
        {
            // 1. Экспорт ТОЛЬКО содержимого страницы: аннотации и поля форм
            //    остаются в документе и не должны попасть в картинку дважды.
            var size = doc.Document.GetLogicalPageSize(pageIndex);
            var scale = request.Dpi / 72.0;
            var width = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
            var height = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));
            var image = await doc.Document.RenderLogicalPageContentOnlyAsync(
                pageIndex, width, height, CancellationToken.None);
            var bgra = request.Grayscale ? ToGrayscale(image.Bgra) : image.Bgra;

            workspace = ExternalEditWorkspace.Create(
                Path.GetFileNameWithoutExtension(doc.Title) + $"-p{pageIndex + 1}");
            await File.WriteAllBytesAsync(workspace.ImagePath,
                ImageEncoder.EncodePng(bgra, image.PixelWidth, image.PixelHeight, request.Dpi));

            var before = ImageEncoder.ToBitmap(bgra, image.PixelWidth, image.PixelHeight);

            // 2. Запуск редактора: путь не задан жёстко, ожидания закрытия нет.
            editor = new ExternalImageEditor(workspace.ImagePath);
            if (!editor.Launch())
            {
                ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), Loc.Get("PaintNoEditor"), "");
                return;
            }

            doc.StatusText = Loc.Get("PaintWaitWaiting");
            var edited = PaintWaitDialog.Run(OwnerWindow, editor, workspace.ImagePath, before);
            if (edited == null)
            {
                doc.StatusText = Loc.Get("PaintCancelled");
                return;
            }

            // 3. Импорт: заменяется ТОЛЬКО визуальное содержимое страницы.
            var imported = ImageEncoder.DecodeBgra(edited);
            doc.Document.Session.Apply(new NexusPdf.Domain.AddOverlayOperation(pageIndex,
                new NexusPdf.Pdf.Abstractions.PageRasterReplacement(
                    imported.Bgra, imported.PixelWidth, imported.PixelHeight)));
            var dpiScale = OwnerWindow != null
                ? System.Windows.Media.VisualTreeHelper.GetDpi(OwnerWindow).DpiScaleX
                : 1.0;
            page.ForceRefresh(dpiScale);
            doc.StatusText = Loc.Get("PaintImported");
            Log.Information("Страница {Page} заменена правкой из внешнего редактора", pageIndex + 1);

            // 4. По желанию — распознать текст на новой картинке.
            if (request.RunOcrAfter && _services.Ocr.IsAvailable)
            {
                doc.StatusText = Loc.Get("OcrTitle");
                var result = await _services.Ocr.RecognizeAsync(
                    doc.Document, new[] { pageIndex }, null, CancellationToken.None);
                doc.StatusText = result.PagesRecognized > 0
                    ? Loc.F("PaintImportedWithOcr", result.WordCount)
                    : Loc.Get("PaintImported");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка правки страницы во внешнем редакторе");
            ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), ex.Message, ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            editor?.Dispose();
            workspace?.Dispose(); // временное изображение удаляется всегда
            doc.IsBusy = false;
        }
    }

    /// <summary>
    /// Слои документа. Видимость записывается в копию файла: движок отрисовки
    /// читает конфигурацию слоёв при открытии, поэтому переключение прямо в
    /// текущей вкладке потребовало бы переоткрытия документа и потери истории
    /// правок — вместо этого честно предлагается сохранить копию.
    /// </summary>
    [RelayCommand]
    private async Task ShowLayers()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        if (doc.FilePath is not { } path)
        {
            doc.StatusText = Loc.Get("LayersNeedSavedFile");
            return;
        }
        if (!_services.Layers.IsAvailable)
        {
            ErrorDialog.Show(OwnerWindow, Loc.Get("LayersTitle"), Loc.Get("QpdfMissing"), "");
            return;
        }

        try
        {
            doc.IsBusy = true;
            var layers = await _services.Layers.GetLayersAsync(
                path, doc.Document.Password, CancellationToken.None);
            doc.IsBusy = false;
            if (layers.Count == 0)
            {
                doc.StatusText = Loc.Get("LayersNone");
                return;
            }

            var choice = LayersDialog.Choose(OwnerWindow, layers);
            if (choice == null)
            {
                doc.StatusText = Loc.Get("Ready");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.Get("LayersSaveCopy"),
                Filter = Loc.Get("PdfFilter"),
                FileName = Path.GetFileNameWithoutExtension(path) + "-layers.pdf",
            };
            if (dialog.ShowDialog(OwnerWindow) != true)
            {
                doc.StatusText = Loc.Get("Ready");
                return;
            }

            doc.IsBusy = true;
            doc.StatusText = Loc.Get("LayersApplying");
            await _services.Layers.SetLayerVisibilityAsync(
                path, doc.Document.Password, choice, dialog.FileName, CancellationToken.None);
            doc.StatusText = Loc.F("LayersSaved", Path.GetFileName(dialog.FileName));
            Log.Information("Копия со слоями сохранена: {Path}", dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка работы со слоями документа");
            ErrorDialog.Show(OwnerWindow, Loc.Get("LayersTitle"), ex.Message, ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    /// <summary>Список вложенных файлов. Открывать их программа не умеет — только сохранять.</summary>
    [RelayCommand]
    private async Task ShowAttachments()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        try
        {
            var attachments = await doc.Document.PrimaryHandle
                .GetAttachmentsAsync(CancellationToken.None);
            if (attachments.Count == 0)
            {
                doc.StatusText = Loc.Get("AttachmentsNone");
                return;
            }
            AttachmentsDialog.Show(OwnerWindow, attachments,
                index => doc.Document.PrimaryHandle.ReadAttachmentAsync(index, CancellationToken.None));
            doc.StatusText = Loc.Get("Ready");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка чтения вложений документа");
            ErrorDialog.Show(OwnerWindow, Loc.Get("AttachmentsTitle"), ex.Message, ex.ToString());
        }
    }

    /// <summary>Правка существующей строки: клик по тексту открывает его в диалоге.</summary>
    [RelayCommand]
    private void EditExistingText()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || doc.PageCount == 0) return;
        doc.BeginPlacement((page, x, y) =>
        {
            _ = EditExistingTextCoreAsync(doc, page, x, y);
            return null;
        });
        doc.StatusText = Loc.Get("TextEditHint");
    }

    private async Task EditExistingTextCoreAsync(
        DocumentViewModel doc, PageViewModel page, double xPt, double yPt)
    {
        try
        {
            // Сначала — распознанные строки: они лежат в правках страницы,
            // попадание считается на месте, без обращения к движку. Иначе
            // «редактируемый текст» пришлось бы сперва сохранять.
            if (TryEditRecognizedLine(doc, page, xPt, yPt))
                return;

            var handle = doc.Document.Handles[page.PageRef.SourceId];
            var target = await handle.GetTextObjectAtAsync(
                page.PageRef.SourcePageIndex, page.PageRef.RotationOffset, xPt, yPt,
                CancellationToken.None);
            if (target == null)
            {
                doc.StatusText = Loc.Get("TextEditNotFound");
                return;
            }

            var edited = TextEditDialog.Edit(OwnerWindow, target.Text, target.FontName,
                target.IsEmbeddedFont, target.FontSizePt,
                text => handle.CanFontRenderTextAsync(
                    page.PageRef.SourcePageIndex, target.ObjectIndex, text, CancellationToken.None));
            if (edited == null || edited == target.Text)
            {
                doc.StatusText = Loc.Get("Ready");
                return;
            }

            doc.Document.Session.Apply(new NexusPdf.Domain.AddOverlayOperation(page.LogicalIndex,
                new NexusPdf.Pdf.Abstractions.TextObjectReplacement(target.ObjectIndex, edited)));
            var dpiScale = OwnerWindow != null
                ? System.Windows.Media.VisualTreeHelper.GetDpi(OwnerWindow).DpiScaleX
                : 1.0;
            page.ForceRefresh(dpiScale);
            doc.StatusText = Loc.Get("TextEditDone");
            Log.Information("Текст объекта {Index} на странице {Page} изменён",
                target.ObjectIndex, page.LogicalIndex + 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка правки текста страницы");
            ErrorDialog.Show(OwnerWindow, Loc.Get("TextEditTitle"), ex.Message, ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
    }

    /// <summary>
    /// Правка строки, полученной распознаванием в режиме редактируемого текста.
    /// Возвращает true, если строка под курсором нашлась и была обработана.
    /// </summary>
    private bool TryEditRecognizedLine(
        DocumentViewModel doc, PageViewModel page, double xPt, double yPt)
    {
        var overlays = doc.Document.Session.Model.Pages[page.LogicalIndex].OverlayList;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            if (overlays[i] is not NexusPdf.Pdf.Abstractions.OcrEditableTextOverlay layer)
                continue;

            var mapped = NexusPdf.Pdf.Abstractions.OverlayDisplayMapper.ToFrame(
                layer, page.PageRef.RotationOffset,
                page.SizePt.WidthPoints, page.SizePt.HeightPoints).Overlay
                as NexusPdf.Pdf.Abstractions.OcrEditableTextOverlay;
            if (mapped == null)
                continue;

            for (var j = 0; j < mapped.Lines.Count; j++)
            {
                var line = mapped.Lines[j];
                if (xPt < line.XPt || xPt > line.XPt + line.WidthPt ||
                    yPt < line.YPt || yPt > line.YPt + line.HeightPt)
                    continue;

                var edited = TextEditDialog.Edit(OwnerWindow, line.Text,
                    Loc.Get("OcrRecognizedLine"), false, line.HeightPt,
                    _ => Task.FromResult(true)); // системный шрифт рисует всё, что введут
                if (edited == null || edited == line.Text)
                {
                    doc.StatusText = Loc.Get("Ready");
                    return true;
                }

                var lines = layer.Lines.ToList();
                lines[j] = lines[j] with { Text = edited };
                doc.Document.Session.Apply(new NexusPdf.Domain.ReplaceOverlayOperation(
                    page.LogicalIndex, layer, layer with { Lines = lines }));
                doc.StatusText = Loc.Get("TextEditDone");
                Log.Information("Изменена распознанная строка {Line} на странице {Page}",
                    j + 1, page.LogicalIndex + 1);
                return true;
            }
        }
        return false;
    }

    // ----- Рельс инструментов (левая колонка) -----

    /// <summary>
    /// Группы инструментов рельса. Панель сверху показывает настройки только
    /// ВЫБРАННОЙ группы: раньше все двадцать кнопок и палитра рисования жили
    /// в одной строке и не помещались.
    /// </summary>
    public enum ToolGroup { None, Pages, Comment, Edit, Forms, Protect }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolGroup))]
    [NotifyPropertyChangedFor(nameof(IsGroupPages))]
    [NotifyPropertyChangedFor(nameof(IsGroupComment))]
    [NotifyPropertyChangedFor(nameof(IsGroupEdit))]
    [NotifyPropertyChangedFor(nameof(IsGroupForms))]
    [NotifyPropertyChangedFor(nameof(IsGroupProtect))]
    private ToolGroup _activeToolGroup = ToolGroup.None;

    public bool HasToolGroup => ActiveToolGroup != ToolGroup.None;
    public bool IsGroupPages => ActiveToolGroup == ToolGroup.Pages;
    public bool IsGroupComment => ActiveToolGroup == ToolGroup.Comment;
    public bool IsGroupEdit => ActiveToolGroup == ToolGroup.Edit;
    public bool IsGroupForms => ActiveToolGroup == ToolGroup.Forms;
    public bool IsGroupProtect => ActiveToolGroup == ToolGroup.Protect;

    [RelayCommand]
    private void SelectToolGroup(string? name)
    {
        if (!Enum.TryParse<ToolGroup>(name, out var group))
            return;
        ActiveToolGroup = ActiveToolGroup == group ? ToolGroup.None : group;

        if (ActiveDocument is not { } doc) return;
        // «Страницы» — это режим просмотра, а не набор кнопок, поэтому рельс
        // переключает его напрямую.
        doc.IsOrganizeMode = ActiveToolGroup == ToolGroup.Pages;
        if (ActiveToolGroup != ToolGroup.Comment)
            doc.SelectDrawTool(DocumentViewModel.DrawTool.None);
    }

    // ----- Профили рабочих пространств -----

    /// <summary>
    /// Профиль — набор состояний интерфейса под задачу: читать, править и
    /// рецензировать удобно с разными открытыми панелями, и переключать их по
    /// одной каждый раз не должно быть работой.
    /// </summary>
    [RelayCommand]
    private void ApplyWorkspace(string? id) => ApplyWorkspace(id, remember: true, changeZoom: true);

    /// <summary>
    /// Восстановление рабочего пространства на открытом документе. Масштаб при
    /// этом НЕ трогается: его выбирает начальная подгонка страницы, и
    /// перебивать её на каждом открытии — значит спорить с пользователем.
    /// </summary>
    private void RestoreWorkspace() =>
        ApplyWorkspace(_services.Settings.Workspace, remember: false, changeZoom: false);

    private void ApplyWorkspace(string? id, bool remember, bool changeZoom)
    {
        var profile = NexusPdf.Ux.WorkspaceProfile.ById(id);
        if (remember)
        {
            _services.Settings.Workspace = profile.Id;
            _services.SaveSettings();
        }

        ActiveToolGroup = profile.Rail switch
        {
            NexusPdf.Ux.ToolRail.Pages => ToolGroup.Pages,
            NexusPdf.Ux.ToolRail.Comment => ToolGroup.Comment,
            NexusPdf.Ux.ToolRail.Edit => ToolGroup.Edit,
            NexusPdf.Ux.ToolRail.Forms => ToolGroup.Forms,
            NexusPdf.Ux.ToolRail.Protect => ToolGroup.Protect,
            _ => ToolGroup.None,
        };

        if (ActiveDocument is not { } doc) return;

        doc.IsOrganizeMode = profile.Organize;
        doc.IsCommentsVisible = profile.CommentsPanel;
        // Оглавление показывается только если оно у документа есть: пустая
        // вкладка вместо миниатюр была бы хуже, чем миниатюры.
        doc.IsOutlineVisible = profile.Outline && doc.HasBookmarks;
        if (profile.Rail != NexusPdf.Ux.ToolRail.Comment)
            doc.SelectDrawTool(DocumentViewModel.DrawTool.None);

        if (!changeZoom)
            return;
        if (!profile.Organize)
        {
            if (profile.FitWholePage)
                doc.FitPageCommand.Execute(null);
            else
                doc.FitWidthCommand.Execute(null);
        }
        doc.StatusText = Loc.F("UxWorkspaceApplied", Loc.Get(profile.TitleKey));
    }

    // ----- Рисование от руки -----

    /// <summary>Палитра рисования: контрастные цвета, различимые и на белом, и на скане.</summary>
    public sealed record DrawSwatch(string Name, string Argb)
    {
        public System.Windows.Media.Brush Brush =>
            new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString("#" + Argb[2..])!);
    }

    public IReadOnlyList<DrawSwatch> DrawPalette { get; } = new[]
    {
        new DrawSwatch("Красный", "FFE02424"),
        new DrawSwatch("Синий", "FF2563EB"),
        new DrawSwatch("Зелёный", "FF16A34A"),
        new DrawSwatch("Оранжевый", "FFF59E0B"),
        new DrawSwatch("Чёрный", "FF111827"),
    };

    [RelayCommand]
    private void DrawPencil() => ActiveDocument?.SelectDrawTool(DocumentViewModel.DrawTool.Pencil);

    [RelayCommand]
    private void DrawLine() => ActiveDocument?.SelectDrawTool(DocumentViewModel.DrawTool.Line);

    [RelayCommand]
    private void DrawArrow() => ActiveDocument?.SelectDrawTool(DocumentViewModel.DrawTool.Arrow);

    /// <summary>Цвет линии задаётся строкой #AARRGGBB из палитры в разметке.</summary>
    [RelayCommand]
    private void SetDrawColor(string? argb)
    {
        if (ActiveDocument is not { } doc || argb == null) return;
        if (uint.TryParse(argb.TrimStart('#'),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            doc.DrawColorArgb = value;
    }

    [RelayCommand]
    private void SetDrawWidth(string? widthPt)
    {
        if (ActiveDocument is not { } doc) return;
        if (double.TryParse(widthPt, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0)
            doc.DrawWidthPt = value;
    }

    [RelayCommand]
    private void EditImageInPaint()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || doc.PageCount == 0) return;
        if (!ExternalImageEditor.IsEditorAvailable())
        {
            ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), Loc.Get("PaintNoEditor"), "");
            return;
        }

        // Диалога нет намеренно: картинка берётся в своём разрешении, поэтому
        // выбирать DPI нечего, а страница не растрируется — выбирать способ
        // возврата тоже не нужно.
        doc.BeginPlacement((page, x, y) =>
        {
            _ = EditImageCoreAsync(doc, page, x, y);
            return null;
        });
        doc.StatusText = Loc.Get("PaintImageHint");
    }

    private async Task EditImageCoreAsync(DocumentViewModel doc, PageViewModel page, double xPt, double yPt)
    {
        ExternalEditWorkspace? workspace = null;
        ExternalImageEditor? editor = null;
        try
        {
            var handle = doc.Document.Handles[page.PageRef.SourceId];
            var target = await handle.GetImageObjectAtAsync(
                page.PageRef.SourcePageIndex, page.PageRef.RotationOffset, xPt, yPt,
                CancellationToken.None);
            if (target == null)
            {
                doc.StatusText = Loc.Get("PaintImageNotFound");
                return;
            }

            doc.IsBusy = true;
            doc.StatusText = Loc.Get("PaintExporting");

            // Экспорт в натуральном разрешении картинки: ни увеличения, ни
            // потери деталей при выходе в редактор не происходит.
            workspace = ExternalEditWorkspace.Create(
                Path.GetFileNameWithoutExtension(doc.Title) + $"-p{page.LogicalIndex + 1}-image");
            await File.WriteAllBytesAsync(workspace.ImagePath,
                ImageEncoder.EncodePng(target.Bgra, target.PixelWidth, target.PixelHeight, 96));

            editor = new ExternalImageEditor(workspace.ImagePath);
            if (!editor.Launch())
            {
                ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), Loc.Get("PaintNoEditor"), "");
                return;
            }

            doc.StatusText = Loc.Get("PaintWaitWaiting");
            var edited = PaintWaitDialog.Run(OwnerWindow, editor, workspace.ImagePath,
                ImageEncoder.ToBitmap(target.Bgra, target.PixelWidth, target.PixelHeight));
            if (edited == null)
            {
                doc.StatusText = Loc.Get("PaintCancelled");
                return;
            }

            // Возврат: подменяется растр САМОГО объекта, поэтому его место,
            // масштаб, поворот и обрезка сохраняются, а страница остаётся
            // текстовой. Размер картинки в пикселях может отличаться от
            // исходного — PDF растянет её по прежней рамке.
            var imported = ImageEncoder.DecodeBgra(edited);
            doc.Document.Session.Apply(new NexusPdf.Domain.AddOverlayOperation(page.LogicalIndex,
                new NexusPdf.Pdf.Abstractions.ImageObjectReplacement(
                    target.ObjectIndex, imported.Bgra, imported.PixelWidth, imported.PixelHeight)));
            var dpiScale = OwnerWindow != null
                ? System.Windows.Media.VisualTreeHelper.GetDpi(OwnerWindow).DpiScaleX
                : 1.0;
            page.ForceRefresh(dpiScale);
            doc.StatusText = Loc.Get("PaintImageDone");
            Log.Information("Изображение {Index} страницы {Page} заменено правкой из редактора",
                target.ObjectIndex, page.LogicalIndex + 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка правки изображения во внешнем редакторе");
            ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), ex.Message, ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            editor?.Dispose();
            workspace?.Dispose();
            doc.IsBusy = false;
        }
    }

    [RelayCommand]
    private void EditRegionInPaint()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy || doc.PageCount == 0) return;
        if (!ExternalImageEditor.IsEditorAvailable())
        {
            ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), Loc.Get("PaintNoEditor"), "");
            return;
        }
        var request = PaintEditDialog.Show(OwnerWindow, wholePage: false, _services.Ocr.IsAvailable);
        if (request == null) return;

        // Жест выбора рамки: обработка начнётся после отпускания мыши, поэтому
        // фабрика ничего не создаёт (возвращает null) и запускает конвейер сама.
        doc.BeginRectPlacement((page, rect) =>
        {
            if (rect.Width >= 8 && rect.Height >= 8)
                _ = EditRegionCoreAsync(doc, page, rect, request);
            return null;
        });
        doc.StatusText = Loc.Get("PaintRegionHint");
    }

    private async Task EditRegionCoreAsync(
        DocumentViewModel doc, PageViewModel page, Rect regionPt, PaintEditRequest request)
    {
        doc.IsBusy = true;
        doc.StatusText = Loc.Get("PaintExporting");
        ExternalEditWorkspace? workspace = null;
        ExternalImageEditor? editor = null;
        try
        {
            var pageIndex = page.LogicalIndex;
            var size = doc.Document.GetLogicalPageSize(pageIndex);
            var scale = request.Dpi / 72.0;
            var pageWidth = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
            var pageHeight = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));
            var full = await doc.Document.RenderLogicalPageContentOnlyAsync(
                pageIndex, pageWidth, pageHeight, CancellationToken.None);

            // Вырезаем область в пикселях растра.
            var x0 = Math.Clamp((int)Math.Floor(regionPt.X * scale), 0, pageWidth - 1);
            var y0 = Math.Clamp((int)Math.Floor(regionPt.Y * scale), 0, pageHeight - 1);
            var x1 = Math.Clamp((int)Math.Ceiling((regionPt.X + regionPt.Width) * scale), x0 + 1, pageWidth);
            var y1 = Math.Clamp((int)Math.Ceiling((regionPt.Y + regionPt.Height) * scale), y0 + 1, pageHeight);
            var cropWidth = x1 - x0;
            var cropHeight = y1 - y0;
            var crop = new byte[cropWidth * cropHeight * 4];
            for (var y = 0; y < cropHeight; y++)
            {
                Buffer.BlockCopy(full.Bgra, (y0 + y) * full.Stride + x0 * 4,
                    crop, y * cropWidth * 4, cropWidth * 4);
            }
            if (request.Grayscale)
                crop = ToGrayscale(crop);

            workspace = ExternalEditWorkspace.Create(
                Path.GetFileNameWithoutExtension(doc.Title) + $"-p{pageIndex + 1}-region");
            await File.WriteAllBytesAsync(workspace.ImagePath,
                ImageEncoder.EncodePng(crop, cropWidth, cropHeight, request.Dpi));

            editor = new ExternalImageEditor(workspace.ImagePath);
            if (!editor.Launch())
            {
                ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), Loc.Get("PaintNoEditor"), "");
                return;
            }

            var edited = PaintWaitDialog.Run(OwnerWindow, editor, workspace.ImagePath,
                ImageEncoder.ToBitmap(crop, cropWidth, cropHeight));
            if (edited == null)
            {
                doc.StatusText = Loc.Get("PaintCancelled");
                return;
            }

            var imported = ImageEncoder.DecodeBgra(edited);
            if (request.RegionMode == RegionReturnMode.Overlay)
            {
                // Картинка ложится поверх; прежнее содержимое области остаётся
                // в файле — об этом честно сказано в диалоге и в статусе.
                doc.Document.Session.Apply(new NexusPdf.Domain.AddOverlayOperation(pageIndex,
                    new NexusPdf.Pdf.Abstractions.ImageOverlay(
                        imported.Bgra, imported.PixelWidth, imported.PixelHeight,
                        regionPt.X, regionPt.Y, regionPt.Width, regionPt.Height)));
                doc.StatusText = Loc.Get("PaintRegionOverlayDone");
            }
            else
            {
                // Уничтожение: правленый фрагмент вклеивается в растр всей
                // страницы, и страница заменяется этим растром целиком —
                // прежнее содержимое под областью физически исчезает.
                var composed = (byte[])full.Bgra.Clone();
                var scaleX = (double)imported.PixelWidth / cropWidth;
                var scaleY = (double)imported.PixelHeight / cropHeight;
                for (var y = 0; y < cropHeight; y++)
                {
                    var srcY = Math.Min(imported.PixelHeight - 1, (int)(y * scaleY));
                    for (var x = 0; x < cropWidth; x++)
                    {
                        var srcX = Math.Min(imported.PixelWidth - 1, (int)(x * scaleX));
                        var src = (srcY * imported.PixelWidth + srcX) * 4;
                        var dst = (y0 + y) * full.Stride + (x0 + x) * 4;
                        composed[dst] = imported.Bgra[src];
                        composed[dst + 1] = imported.Bgra[src + 1];
                        composed[dst + 2] = imported.Bgra[src + 2];
                        composed[dst + 3] = 0xFF;
                    }
                }
                doc.Document.Session.Apply(new NexusPdf.Domain.AddOverlayOperation(pageIndex,
                    new NexusPdf.Pdf.Abstractions.PageRasterReplacement(composed, pageWidth, pageHeight)));
                doc.StatusText = Loc.Get("PaintRegionDestroyDone");
            }

            var dpiScale = OwnerWindow != null
                ? System.Windows.Media.VisualTreeHelper.GetDpi(OwnerWindow).DpiScaleX
                : 1.0;
            page.ForceRefresh(dpiScale);
            Log.Information("Область страницы {Page} обновлена правкой из редактора (режим {Mode})",
                pageIndex + 1, request.RegionMode);

            if (request.RunOcrAfter && _services.Ocr.IsAvailable &&
                request.RegionMode == RegionReturnMode.DestroyOriginal)
            {
                var result = await _services.Ocr.RecognizeAsync(
                    doc.Document, new[] { pageIndex }, null, CancellationToken.None);
                if (result.PagesRecognized > 0)
                    doc.StatusText = Loc.F("PaintImportedWithOcr", result.WordCount);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка правки области во внешнем редакторе");
            ErrorDialog.Show(OwnerWindow, Loc.Get("PaintEditTitle"), ex.Message, ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            editor?.Dispose();
            workspace?.Dispose();
            doc.IsBusy = false;
        }
    }

    private static byte[] ToGrayscale(byte[] bgra)
    {
        var result = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            // Коэффициенты яркости BT.601 — привычная «серая» печать.
            var gray = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
            result[i] = gray;
            result[i + 1] = gray;
            result[i + 2] = gray;
            result[i + 3] = bgra[i + 3];
        }
        return result;
    }

    // ----- Конвертация и пакетная обработка -----

    private bool _convertBusy; // глобальные операции без открытого документа

    [RelayCommand]
    private async Task ExportImages()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var request = ExportImagesDialog.Show(OwnerWindow);
        if (request == null) return;
        var folder = new OpenFolderDialog { Title = Loc.Get("ExportImagesPickFolder") };
        if (folder.ShowDialog(OwnerWindow) != true) return;

        IReadOnlyList<int>? targets = request.CurrentOnly && doc.PageCount > 0
            ? new[] { Math.Clamp(doc.CurrentPageNumber - 1, 0, doc.PageCount - 1) }
            : null;
        doc.IsBusy = true;
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(doc.Title);
            var extension = request.Jpeg ? "jpg" : "png";
            // Существующие файлы не перезаписываются молча: занятые имена
            // получают суффикс « (2)» — как в пакетной обработке.
            var prefix = baseName;
            var n = 2;
            while (Directory.EnumerateFiles(folder.FolderName, $"{prefix}-*.{extension}").Any())
                prefix = $"{baseName} ({n++})";
            var token = doc.Busy.Start(Loc.Get("ExportImagesTitle"), canCancel: true, determinate: true);
            var count = await _services.Convert.ExportImagesAsync(
                doc.Document, targets, request.Dpi,
                async (image, pageIndex, effectiveDpi, ct) =>
                {
                    var path = Path.Combine(folder.FolderName, $"{prefix}-{pageIndex + 1:000}.{extension}");
                    await File.WriteAllBytesAsync(path, ImageEncoder.Encode(image, request.Jpeg, effectiveDpi), ct);
                },
                new Progress<(int Done, int Total)>(p =>
                {
                    doc.Busy.Report(p.Total > 0 ? (double)p.Done / p.Total : 0,
                        Loc.F("BusyPageOf", p.Done, p.Total));
                    doc.StatusText = Loc.F("ExportImagesProgress", p.Done, p.Total);
                }),
                token);
            doc.StatusText = Loc.F("ExportImagesDone", count, folder.FolderName);
            Log.Information("Экспортировано {Count} страниц в {Folder}", count, folder.FolderName);
        }
        catch (OperationCanceledException)
        {
            doc.StatusText = Loc.Get("BusyCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка экспорта изображений");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"), Loc.Get("ExportImagesTitle"), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    /// <summary>
    /// Что сказать про страницы без текстового слоя.
    ///
    /// Молчать нельзя: пустой лист в книге и пустая страница в документе
    /// выглядят как «в PDF ничего не было», а на деле это непрочитанный скан.
    /// </summary>
    private static string ScanNote(int scanned, int recognized)
    {
        if (scanned == 0) return string.Empty;
        return recognized > 0
            ? " — " + Loc.F("ExportScansRecognized", recognized, scanned)
            : " — " + Loc.F("ExportScansSkipped", scanned);
    }

    /// <summary>
    /// Документ → документ Word.
    ///
    /// Пользователю честно говорится, что разметка ВОССТАНОВЛЕНА: в PDF нет ни
    /// абзацев, ни таблиц, и результат всегда компромисс. Обещать «как в
    /// оригинале» — значит подставить того, кто отправит файл дальше не глядя.
    /// </summary>
    [RelayCommand]
    private async Task ExportWord()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var request = ExportDocumentDialog.Show(
            OwnerWindow, forWord: true, doc.PageCount, doc.CurrentPageNumber - 1,
            _services.Ocr.IsAvailable, _services.Ocr.UnavailableReason);
        if (request == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("DocxFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + ".docx",
            DefaultExt = ".docx",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;

        doc.IsBusy = true;
        try
        {
            var token = doc.Busy.Start(Loc.Get("ExportWordTitle"), canCancel: true, determinate: true);
            var summary = await _services.Convert.ExportToWordAsync(
                doc.Document, dialog.FileName, request.Pages,
                // Картинки жмутся кодеками Windows: фотография в PNG без
                // потерь весила бы в десятки раз больше без всякой пользы.
                new WordExportOptions(
                    KeepLinks: request.KeepLinks,
                    KeepImages: request.KeepImages,
                    KeepComments: request.KeepComments,
                    Encode: ImageEncoder.EncodeForDocument),
                new PageAnalysisOptions(
                    DetectWhitespaceTables: request.DetectTables,
                    RecognizeScans: request.RecognizeScans),
                new Progress<(int Done, int Total)>(p =>
                {
                    doc.Busy.Report(p.Total > 0 ? (double)p.Done / p.Total : 0,
                        Loc.F("ExportWordProgress", p.Done, p.Total));
                }),
                token);

            doc.StatusText = Loc.F("ExportWordDone", summary.Pages, summary.Paragraphs,
                summary.Tables, summary.Images, summary.Links, summary.Comments) +
                " — " + Loc.Get("ExportWordHint") +
                ScanNote(summary.ScannedPages, summary.RecognizedPages);
            Log.Information("Экспорт в Word: {File}, страниц {Pages}", dialog.FileName, summary.Pages);
        }
        catch (OperationCanceledException)
        {
            doc.StatusText = Loc.Get("BusyCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка экспорта в Word");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"), Loc.Get("ExportWordTitle"), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    /// <summary>
    /// Таблицы документа → книга Excel.
    ///
    /// Итог показывается разбором: сколько таблиц взято по нарисованным
    /// границам, а сколько восстановлено по расположению текста. Второе —
    /// догадка, и пользователь должен об этом знать, а не выяснять потом.
    /// </summary>
    [RelayCommand]
    private async Task ExportExcel()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var request = ExportDocumentDialog.Show(
            OwnerWindow, forWord: false, doc.PageCount, doc.CurrentPageNumber - 1,
            _services.Ocr.IsAvailable, _services.Ocr.UnavailableReason);
        if (request == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("XlsxFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + ".xlsx",
            DefaultExt = ".xlsx",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;

        doc.IsBusy = true;
        try
        {
            var token = doc.Busy.Start(Loc.Get("ExportExcelTitle"), canCancel: true, determinate: true);
            var summary = await _services.Convert.ExportToExcelAsync(
                doc.Document, dialog.FileName, request.Pages,
                // Запятая как десятичный разделитель — по языку интерфейса:
                // однозначно определить «1,234» из самой строки невозможно.
                new ExcelExportOptions(
                    DecimalIsComma: Loc.CurrentLanguage != "en",
                    KeepLinks: request.KeepLinks),
                new PageAnalysisOptions(
                    DetectWhitespaceTables: request.DetectTables,
                    RecognizeScans: request.RecognizeScans),
                new Progress<(int Done, int Total)>(p =>
                {
                    doc.Busy.Report(p.Total > 0 ? (double)p.Done / p.Total : 0,
                        Loc.F("ExportExcelProgress", p.Done, p.Total));
                }),
                token);

            doc.StatusText = Loc.F("ExportExcelDone", summary.Sheets, summary.Tables,
                summary.RulingTables, summary.GuessedTables, summary.Links);
            if (summary.GuessedTables > 0)
                doc.StatusText += " — " + Loc.F("ExportExcelGuessHint", summary.GuessedTables);
            doc.StatusText += ScanNote(summary.ScannedPages, summary.RecognizedPages);
            Log.Information("Экспорт в Excel: {File}, таблиц {Tables}", dialog.FileName, summary.Tables);
        }
        catch (OperationCanceledException)
        {
            doc.StatusText = Loc.Get("BusyCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка экспорта в Excel");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"), Loc.Get("ExportExcelTitle"), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExtractText()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("TxtFilter"),
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + ".txt",
            DefaultExt = ".txt",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        doc.IsBusy = true;
        try
        {
            var text = await _services.Convert.ExtractTextAsync(
                doc.Document, doc.Busy.Start(Loc.Get("ExtractTextMenu")));
            await File.WriteAllTextAsync(dialog.FileName, text, System.Text.Encoding.UTF8);
            doc.StatusText = Loc.F("ExtractTextDone", Path.GetFileName(dialog.FileName));
        }
        catch (OperationCanceledException)
        {
            doc.StatusText = Loc.Get("BusyCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка извлечения текста");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorSaveFile", Path.GetFileName(dialog.FileName)), ex.ToString());
            doc.StatusText = Loc.Get("Ready");
        }
        finally
        {
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateFromImages()
    {
        if (_convertBusy) return;
        var open = new OpenFileDialog { Filter = Loc.Get("ImageFilter"), Multiselect = true };
        if (open.ShowDialog(OwnerWindow) != true || open.FileNames.Length == 0) return;
        var save = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = Loc.Get("FromImagesDefaultName"),
            DefaultExt = ".pdf",
        };
        if (save.ShowDialog(OwnerWindow) != true) return;
        if (RejectIfTargetOpenAnywhere(save.FileName)) return;

        _convertBusy = true;
        try
        {
            // Декодирование пачки фото — работа на секунды: не на UI-потоке.
            var files = open.FileNames;
            var specs = await Task.Run(() => files.Select(ImageEncoder.DecodeAsPageSpec).ToList());
            await _services.Convert.CreateFromImagesAsync(specs, save.FileName, CancellationToken.None);
            Log.Information("Создан PDF из {Count} изображений: {Path}", specs.Count, save.FileName);
            await OpenFilesAsync(new[] { save.FileName });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка сборки PDF из изображений");
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"),
                Loc.F("ErrorSaveFile", Path.GetFileName(save.FileName)), ex.ToString());
        }
        finally
        {
            _convertBusy = false;
        }
    }

    [RelayCommand]
    private async Task MergePdfs()
    {
        if (_convertBusy) return;
        var open = new OpenFileDialog { Filter = Loc.Get("PdfFilter"), Multiselect = true };
        if (open.ShowDialog(OwnerWindow) != true) return;
        if (open.FileNames.Length < 2)
        {
            ErrorDialog.Show(OwnerWindow, Loc.Get("MergeMenu"), Loc.Get("MergeNeedTwo"), "");
            return;
        }
        var save = new SaveFileDialog
        {
            Filter = Loc.Get("PdfFilter"),
            FileName = Loc.Get("MergeDefaultName"),
            DefaultExt = ".pdf",
        };
        if (save.ShowDialog(OwnerWindow) != true) return;
        if (RejectIfTargetOpenAnywhere(save.FileName)) return;

        _convertBusy = true;
        try
        {
            var pages = await _services.Convert.MergeAsync(open.FileNames, save.FileName, CancellationToken.None);
            Log.Information("Объединено {Files} файлов ({Pages} страниц): {Path}",
                open.FileNames.Length, pages, save.FileName);
            await OpenFilesAsync(new[] { save.FileName });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка объединения PDF");
            ErrorDialog.Show(OwnerWindow, Loc.Get("MergeMenu"),
                Loc.F("ErrorSaveFile", Path.GetFileName(save.FileName)), ex.ToString());
        }
        finally
        {
            _convertBusy = false;
        }
    }

    [RelayCommand]
    private void ShowBatch() => BatchDialog.Run(OwnerWindow, _services);

    [RelayCommand]
    private void ShowBatchPrint() => BatchPrintDialog.Run(OwnerWindow, _services);

    [RelayCommand]
    private void CompareDocuments()
    {
        if (ActiveDocument is { IsBusy: true }) return; // идёт сохранение: файл на диске в переходном состоянии
        CompareDialog.Run(OwnerWindow, _services.Engine, ActiveDocument?.FilePath);
    }

    [RelayCommand]
    private async Task ShowProperties()
    {
        if (ActiveDocument is { IsBusy: false } doc)
            await DocPropertiesDialog.ShowAsync(OwnerWindow, doc);
    }

    /// <summary>Цель записи не должна быть открыта ни в одной вкладке НИ ОДНОГО окна приложения.</summary>
    private bool RejectIfTargetOpenAnywhere(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        var isOpen = WindowManager.AllViewModels()
            .SelectMany(vm => vm.Documents)
            .SelectMany(d => d.Document.Handles.Values)
            .Any(h => string.Equals(Path.GetFullPath(h.FilePath), full, StringComparison.OrdinalIgnoreCase));
        if (isOpen)
            ErrorDialog.Show(OwnerWindow, Loc.Get("ErrorTitle"), Loc.Get("ConvertTargetIsOpen"), "");
        return isOpen;
    }

    // ----- Печать и инструменты qpdf -----

    [RelayCommand]
    private void PrintActive()
    {
        if (ActiveDocument is not { } doc || doc.IsBusy) return;
        // Собственный центр печати вместо системного диалога: предпросмотр,
        // раскладка и возможности принтера живут там.
        PrintCenterDialog.Run(OwnerWindow, doc, _services);
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
            doc.Busy.Finish();
            doc.IsBusy = false;
        }
    }


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
        OnPropertyChanged(nameof(ShowStartScreen));
        OnPropertyChanged(nameof(IsOpeningFiles));
        await doc.DisposeAsync();
        UpdateSessionSnapshot();
    }

    /// <summary>Попытка закрыть все вкладки окна (при закрытии окна). false — пользователь отменил.</summary>
    public async Task<bool> TryCloseAllAsync()
    {
        if (Documents.Any(d => d.IsBusy) || _convertBusy)
            return false; // печать, сохранение или конвертация ещё идут

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
        OnPropertyChanged(nameof(ShowStartScreen));
        OnPropertyChanged(nameof(IsOpeningFiles));
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

    /// <summary>
    /// Плотность интерфейса. Применяется немедленно: смотреть на новые размеры
    /// после перезапуска — не способ выбрать, что удобнее.
    /// </summary>
    [RelayCommand]
    private void SetDensity(string density)
    {
        _services.Settings.UiDensity = density;
        _services.SaveSettings();
        Services.Ux.TouchInputWatcher.SetSetting(density);
        if (ActiveDocument is { } doc)
            doc.StatusText = Loc.Get("UxDensityApplied");
    }

    [RelayCommand]
    private void About()
    {
        AboutDialog.Show(OwnerWindow, _services.Ocr.EngineName);
    }
}
