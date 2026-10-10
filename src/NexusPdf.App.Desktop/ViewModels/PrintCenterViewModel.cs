using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Application;
using NexusPdf.Infrastructure;
using NexusPdf.Printing;
using NexusPdf.Printing.Windows;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>Строка списка листов слева.</summary>
public sealed partial class SheetThumbViewModel : ObservableObject
{
    public required int Index { get; init; }
    public required string Caption { get; init; }
    public required bool IsFront { get; init; }
    public required bool HasWarning { get; init; }

    [ObservableProperty]
    private ImageSource? _image;
}

/// <summary>
/// Центр печати. Держит настройки, пересчитывает <see cref="PrintJobPlan"/> при
/// любом изменении и отдаёт предпросмотр ИЗ ЭТОГО ЖЕ плана — отдельной логики
/// для картинки в окне не существует.
/// </summary>
public sealed partial class PrintCenterViewModel : ObservableObject, IDisposable
{
    private readonly DocumentViewModel _document;
    private readonly AppServices _services;
    private readonly WindowsPrinterService _printers = new();
    private readonly PrinterCapsCache _capsCache;
    private readonly PrintLayoutEngine _engine = new();
    private readonly CancellationTokenSource _probeCts = new();

    private CancellationTokenSource? _previewCts;
    private bool _suspendRecalc;
    private bool _disposed;

    /// <summary>
    /// Стандартные форматы — на время, пока принтер не сообщил свои, и для
    /// принтера, который не ответил вовсе. Пустой список бумаги не давал ни
    /// выбрать формат, ни сохранить раскладку в файл.
    /// </summary>
    private static readonly IReadOnlyList<PaperSizeOption> StandardPapers = new[]
    {
        new PaperSizeOption("A4", new SizePt(595.28, 841.89), "9"),
        new PaperSizeOption("A3", new SizePt(841.89, 1190.55), "8"),
        new PaperSizeOption("A5", new SizePt(419.53, 595.28), "11"),
        new PaperSizeOption("Letter", new SizePt(612, 792), "1"),
        new PaperSizeOption("Legal", new SizePt(612, 1008), "5"),
    };

    private static readonly SizePt A4Size = new(595.28, 841.89);
    private static readonly SizePt LetterSize = new(612, 792);

    // Состояние опроса — по имени принтера, один раз на принтер за время окна.
    // Раньше опрос запускался из обработчика выбора принтера, а применение его
    // результата снова выбирало принтер: у очереди без форматов бумаги это
    // замыкалось в бесконечную рекурсию и роняло всю программу.
    private readonly HashSet<string> _probeStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _probing = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _capsKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notResponding = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noPaperList = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _marginsRequested = new(StringComparer.OrdinalIgnoreCase);
    private bool _listLoaded;

    public PrintCenterViewModel(
        DocumentViewModel document, AppServices services,
        IReadOnlyList<int>? preselectedPageIndices = null)
    {
        _document = document;
        _services = services;
        _capsCache = new PrinterCapsCache(
            System.IO.Path.Combine(AppPaths.Root, "printer-caps.json"));

        _suspendRecalc = true;
        LoadProfiles();
        CurrentPageNumber = document.CurrentPageNumber;
        // Печать пришла из выбора страниц — объём сразу тот, который выбрал
        // пользователь. Заставлять его повторять выбор в диалоге незачем.
        if (preselectedPageIndices is { Count: > 0 })
        {
            ExplicitIndices = preselectedPageIndices.Distinct().OrderBy(i => i).ToList();
            Scope = PageScope.Selected;
        }
        // Стандартная бумага сразу: окно открывается с рабочим списком форматов
        // ещё до того, как придут принтеры.
        SyncPaperList(StandardPapers, defaultPaperCode: null, reselect: true);
        _suspendRecalc = false;

        // Конструктор принтеры НЕ опрашивает — в этом вся идея. Окно
        // открывается мгновенно с предпросмотром по A4, а принтеры приходят
        // из InitializeAsync после показа: опрос драйвера на Windows 10 с
        // сетевой очередью занимал до минуты и держал окно «не открывшимся».
        Recalculate();
        _ = LoadPermissionsAsync();
        _ = LoadDocumentFactsAsync();
    }

    /// <summary>Обнаружение принтеров после показа окна.</summary>
    public Task InitializeAsync() => LoadPrinterListAsync(refresh: false);

    /// <summary>
    /// Список принтеров: имена — мгновенно из диспетчера, возможности — только
    /// у выбранного и строго в фоне. Обновление списка правит его на месте:
    /// уже прочитанные принтеры сохраняют свои возможности, выбранный остаётся
    /// выбранным, а настройки окна не трогаются.
    /// </summary>
    private async Task LoadPrinterListAsync(bool refresh)
    {
        IReadOnlyList<PrinterSummary> names;
        try
        {
            names = await Task.Run(_printers.DiscoverNames);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Список принтеров не получен");
            names = Array.Empty<PrinterSummary>();
        }
        if (_disposed) return;
        _listLoaded = true;

        var current = _selectedPrinter?.PrinterName;
        var keep = new HashSet<string>(names.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
        for (var i = Printers.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Printers[i].PrinterName))
                Printers.RemoveAt(i);
        }
        for (var i = 0; i < names.Count; i++)
        {
            var summary = names[i];
            var at = IndexOfPrinter(summary.Name);
            if (at < 0)
            {
                Printers.Insert(i, WindowsPrinterService.Skeleton(summary));
                continue;
            }
            if (at != i)
                Printers.Move(at, i);
            if (Printers[i].IsDefault != summary.IsDefault)
                Printers[i] = Printers[i] with { IsDefault = summary.IsDefault };
        }

        // Выбор: тот же принтер, что был; иначе — тот, на котором печатали в
        // прошлый раз; иначе — принтер Windows по умолчанию.
        var target = (current != null ? FindPrinter(current) : null)
                     ?? FindPrinter(_services.Settings.LastPrinterName)
                     ?? Printers.FirstOrDefault(p => p.IsDefault)
                     ?? Printers.FirstOrDefault();

        if (refresh && target != null)
        {
            // «Обновить» — это просьба переспросить принтер (его могли включить
            // или заправить), а не повод сбросить выбранные настройки.
            _probeStarted.Clear();
            _notResponding.Remove(target.PrinterName);
        }

        if (target == null)
        {
            ApplySelectedPrinter(null);
        }
        else if (!ReferenceEquals(target, _selectedPrinter))
        {
            ApplySelectedPrinter(target);
        }
        else
        {
            // Тот же объект: список правился вокруг него, выпадающему списку
            // надо заново показать выбор.
            OnPropertyChanged(nameof(SelectedPrinter));
            StartProbe(target.PrinterName);
        }
        RaisePrinterState();
    }

    private int IndexOfPrinter(string name)
    {
        for (var i = 0; i < Printers.Count; i++)
        {
            if (string.Equals(Printers[i].PrinterName, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private PrinterCapabilities? FindPrinter(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var index = IndexOfPrinter(name);
        return index >= 0 ? Printers[index] : null;
    }

    private void StartProbe(string printerName)
    {
        if (_disposed || !_probeStarted.Add(printerName)) return;
        _ = ProbeAsync(printerName);
    }

    /// <summary>
    /// Возможности принтера: сперва из дискового кэша (мгновенно), затем
    /// свежие от драйвера. Через несколько секунд без ответа окно честно
    /// говорит «не отвечает» и работает дальше на том, что есть, но поздний
    /// ответ всё равно принимается — медленная сетевая очередь получает свои
    /// форматы, а не остаётся на стандартных до конца.
    /// </summary>
    private async Task ProbeAsync(string name)
    {
        // Никогда не внутри обработчика выбора: применение результата само
        // меняет выбранную строку, и вложенный вызов был источником рекурсии.
        await Task.Yield();
        if (_disposed) return;

        var cached = _capsCache.TryGet(name);
        if (cached != null && !_capsKnown.Contains(name) && FindPrinter(name) is { } row)
        {
            _capsKnown.Add(name);
            ReplacePrinter(cached with
            {
                IsDefault = row.IsDefault,
                State = row.State,
                Connection = row.Connection == PrinterConnection.Unknown ? cached.Connection : row.Connection,
            });
        }

        _probing.Add(name);
        RaisePrinterState();

        PrinterCapabilities? fresh;
        try
        {
            var work = _printers.ReadInBackground(name);
            var first = await Task.WhenAny(work, Task.Delay(WindowsPrinterService.DefaultTimeout, _probeCts.Token));
            if (_disposed) return;
            if (first != work)
            {
                _probing.Remove(name);
                _notResponding.Add(name);
                RaisePrinterState();
                var late = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(60), _probeCts.Token));
                if (_disposed || late != work) return;
            }
            fresh = await work;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Опрос принтера {Printer} не удался", name);
            fresh = null;
        }
        finally
        {
            _probing.Remove(name);
        }
        if (_disposed) return;

        var current = FindPrinter(name);
        if (current == null)
        {
            RaisePrinterState();
            return; // принтер исчез из списка, пока его спрашивали
        }

        if (fresh == null)
        {
            // Очередь не открылась вовсе — принтер числится, но недоступен.
            _notResponding.Add(name);
            if (!_capsKnown.Contains(name))
                ReplacePrinter(current with { State = PrinterState.Offline });
        }
        else if (fresh.PaperSizes.Count == 0)
        {
            // Драйвер ответил, но форматов не дал: это сбой чтения, а не
            // «принтер без бумаги». Состояние берём свежее, возможности — те,
            // что были; в кэш такой ответ не пишется.
            _notResponding.Remove(name);
            _noPaperList.Add(name);
            ReplacePrinter(current with { State = fresh.State, IsDefault = fresh.IsDefault });
        }
        else
        {
            _notResponding.Remove(name);
            _noPaperList.Remove(name);

            // Поля бумаги драйвер в общем ответе не отдаёт — они дочитываются
            // лениво по формату. Уже прочитанные (в этом окне или в кэше при
            // том же драйвере) переносятся, а не теряются при каждом опросе.
            var margins = new Dictionary<string, MarginsPt>();
            if (cached != null && string.Equals(cached.DriverName, fresh.DriverName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pair in cached.HardMarginsByPaper)
                    margins[pair.Key] = pair.Value;
            }
            foreach (var pair in current.HardMarginsByPaper)
                margins[pair.Key] = pair.Value;
            foreach (var key in margins.Keys.ToList())
            {
                if (!fresh.PaperSizes.Any(p => p.Name == key))
                    margins.Remove(key);
            }

            var complete = fresh with { HardMarginsByPaper = margins };
            _capsKnown.Add(name);
            _capsCache.Store(complete);
            ReplacePrinter(complete);
        }

        RaisePrinterState();
        if (string.Equals(_selectedPrinter?.PrinterName, name, StringComparison.OrdinalIgnoreCase) &&
            SelectedPaper != null)
            _ = EnsureMarginsAsync(SelectedPaper);
    }

    /// <summary>
    /// Заменяет строку списка новыми возможностями. Выбор сохраняется, а список
    /// бумаги перестраивается только если форматы действительно другие.
    /// </summary>
    private void ReplacePrinter(PrinterCapabilities caps)
    {
        var index = IndexOfPrinter(caps.PrinterName);
        var isSelected = string.Equals(_selectedPrinter?.PrinterName, caps.PrinterName, StringComparison.OrdinalIgnoreCase);
        // Замена выбранной строки заставляет выпадающий список записать в
        // выбор пустоту — свойство её отбрасывает (см. SelectedPrinter).
        if (index >= 0) Printers[index] = caps;
        else Printers.Add(caps);
        if (isSelected)
            ApplySelectedPrinter(caps);
    }

    /// <summary>
    /// Страницы, выбранные до открытия центра печати. Пустой список — обычная
    /// печать; тогда объём «Выбранные» не показывается вовсе, чтобы не
    /// предлагать заведомо пустой вариант.
    /// </summary>
    public IReadOnlyList<int> ExplicitIndices { get; } = Array.Empty<int>();

    public bool HasExplicitSelection => ExplicitIndices.Count > 0;

    public string ExplicitSelectionCaption => Loc.F("PrintSelectedScope", ExplicitIndices.Count);

    /// <summary>
    /// Разрешения документа. Читаются асинхронно и пересчитывают план: запрет
    /// печати обязан блокировать кнопку, а не всплывать после отправки.
    /// </summary>
    private PrintPermissions _permissions = PrintPermissions.Unrestricted;

    private async Task LoadPermissionsAsync()
    {
        try
        {
            var flags = await _document.Document.PrimaryHandle
                .GetPermissionsAsync(CancellationToken.None);
            _permissions = PrintPermissions.FromFlags(flags);
        }
        catch (Exception ex)
        {
            // Не смогли прочитать — считаем, что ограничений нет: молча
            // запрещать печать обычного документа хуже, чем не заметить запрет.
            Serilog.Log.Warning(ex, "Не удалось прочитать разрешения документа");
            _permissions = PrintPermissions.Unrestricted;
        }
        Recalculate();
    }

    // ----- Принтер -----

    public ObservableCollection<PrinterCapabilities> Printers { get; } = new();

    private PrinterCapabilities? _selectedPrinter;

    public PrinterCapabilities? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            // Пустое значение пользователь выбрать не может: это всегда
            // побочный эффект перестройки списка — выпадающий список пишет
            // null, когда выбранную строку заменяют или переставляют.
            if (value == null) return;
            ApplySelectedPrinter(value);
        }
    }

    private void ApplySelectedPrinter(PrinterCapabilities? value)
    {
        if (ReferenceEquals(_selectedPrinter, value)) return;
        var switched = !string.Equals(_selectedPrinter?.PrinterName, value?.PrinterName, StringComparison.OrdinalIgnoreCase);
        _selectedPrinter = value;
        OnPropertyChanged(nameof(SelectedPrinter));

        if (value != null)
        {
            _suspendRecalc = true;
            try
            {
                SyncPaperList(value.PaperSizes.Count > 0 ? value.PaperSizes : StandardPapers,
                    value.DefaultPaperDriverValue, reselect: switched);
                FitChoicesToPrinter(value);
            }
            finally
            {
                _suspendRecalc = false;
            }
        }

        RaisePrinterState();
        Recalculate();

        if (value != null)
        {
            StartProbe(value.PrinterName);
            if (!switched && SelectedPaper != null)
                _ = EnsureMarginsAsync(SelectedPaper);
        }
    }

    // ----- Бумага -----

    public ObservableCollection<PaperSizeOption> PaperSizes { get; } = new();

    private PaperSizeOption? _selectedPaper;

    /// <summary>Идёт перестройка списка бумаги: записи выпадающего списка — не выбор человека.</summary>
    private bool _rebuildingPapers;

    /// <summary>
    /// Бумага, которую выбрал человек (или профиль). Её ищут на каждом новом
    /// принтере: раньше любое обновление сведений о принтере очищало список,
    /// выпадающий список записывал в выбор пустоту, и выбранный A3 тут же
    /// «отскакивал» на A4.
    /// </summary>
    private PaperSizeOption? _wantedPaper;
    private string _wantedPaperName = "";

    public PaperSizeOption? SelectedPaper
    {
        get => _selectedPaper;
        set
        {
            if (_rebuildingPapers || value == null) return;
            if (Equals(_selectedPaper, value)) return;
            _wantedPaper = value;
            _wantedPaperName = "";
            PaperNote = null;
            SetPaper(value);
        }
    }

    private void SetPaper(PaperSizeOption? value, bool forceNotify = false)
    {
        var changed = !Equals(_selectedPaper, value);
        _selectedPaper = value;
        if (changed || forceNotify)
            OnPropertyChanged(nameof(SelectedPaper));
        if (!changed) return;
        Recalculate();
        if (value != null)
            _ = EnsureMarginsAsync(value);
    }

    /// <summary>Пояснение под выбором бумаги: почему стоит не то, что выбирали.</summary>
    [ObservableProperty] private string? _paperNote;

    /// <summary>
    /// Список бумаги под принтер. Перестраивается, только если форматы другие;
    /// выбор сохраняется, а когда его нет на этом принтере, берётся бумага
    /// самого принтера — и окно говорит об этом, а не меняет молча.
    /// </summary>
    private void SyncPaperList(IReadOnlyList<PaperSizeOption> papers, string? defaultPaperCode, bool reselect)
    {
        var listChanged = !PaperSizes.SequenceEqual(papers);
        if (listChanged)
        {
            _rebuildingPapers = true;
            try
            {
                PaperSizes.Clear();
                foreach (var paper in papers)
                    PaperSizes.Add(paper);
            }
            finally
            {
                _rebuildingPapers = false;
            }
        }

        if (!listChanged && !reselect && _selectedPaper != null && PaperSizes.Contains(_selectedPaper))
            return;

        PaperSizeOption? pick = null;
        string? missing = null;
        if (_wantedPaper != null)
        {
            pick = FindSamePaper(papers, _wantedPaper);
            if (pick == null) missing = _wantedPaper.Name;
        }
        else if (_wantedPaperName.Length > 0)
        {
            pick = papers.FirstOrDefault(p => string.Equals(p.Name, _wantedPaperName, StringComparison.OrdinalIgnoreCase));
            if (pick == null) missing = _wantedPaperName;
        }

        pick ??= (defaultPaperCode != null ? papers.FirstOrDefault(p => p.DriverValue == defaultPaperCode) : null)
                 ?? FindBySize(papers, A4Size)
                 ?? FindBySize(papers, LetterSize)
                 ?? papers.FirstOrDefault();

        PaperNote = missing != null && pick != null && SelectedPrinter != null &&
                    _capsKnown.Contains(SelectedPrinter.PrinterName)
            ? Loc.F("PrintPaperMissing", missing, pick.Name)
            : _noPaperList.Contains(SelectedPrinter?.PrinterName ?? "") ? Loc.Get("PrintPaperStandardOnly") : null;

        // После перестройки выпадающий список выбор потерял: уведомление нужно
        // даже тогда, когда значение то же самое.
        SetPaper(pick, forceNotify: listChanged);
    }

    /// <summary>Тот же формат на другом принтере: по имени, стандартному коду или размеру.</summary>
    private static PaperSizeOption? FindSamePaper(IReadOnlyList<PaperSizeOption> papers, PaperSizeOption wanted) =>
        papers.FirstOrDefault(p => string.Equals(p.Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
        ?? (wanted.DriverValue is { } code && short.TryParse(code, out var dm) && dm is > 0 and < 256
            ? papers.FirstOrDefault(p => p.DriverValue == code)
            : null)
        ?? FindBySize(papers, wanted.SizePt);

    /// <summary>По физическому размеру: имена форматов у драйверов разные («A4», «A4 210 x 297 мм»).</summary>
    private static PaperSizeOption? FindBySize(IReadOnlyList<PaperSizeOption> papers, SizePt size) =>
        papers.FirstOrDefault(p => JobSplitter.SamePaper(p.SizePt, size));

    /// <summary>
    /// Непечатаемые поля выбранного формата. Дочитываются лениво — одно
    /// обращение к драйверу на формат, а не все форматы при открытии окна:
    /// именно этот «опрос всего заранее» и держал окно печати закрытым.
    /// </summary>
    private async Task EnsureMarginsAsync(PaperSizeOption paper)
    {
        var printer = SelectedPrinter;
        if (_disposed || printer == null || !_capsKnown.Contains(printer.PrinterName)) return;
        if (printer.HardMarginsByPaper.ContainsKey(paper.Name)) return;
        if (!printer.PaperSizes.Contains(paper)) return;
        if (!_marginsRequested.Add(printer.PrinterName + "\n" + paper.Name)) return;

        var margins = await _printers.ReadHardMarginsAsync(
            printer.PrinterName, paper, WindowsPrinterService.DefaultTimeout, _probeCts.Token);
        if (_disposed || margins == null) return;
        if (FindPrinter(printer.PrinterName) is not { } current) return;

        var updated = new Dictionary<string, MarginsPt>(current.HardMarginsByPaper)
        {
            [paper.Name] = margins.Value,
        };
        var enriched = current with { HardMarginsByPaper = updated };
        _capsCache.Store(enriched);
        ReplacePrinter(enriched);
    }

    /// <summary>Строка состояния принтера на человеческом языке.</summary>
    public string PrinterStatusText
    {
        get
        {
            var printer = SelectedPrinter;
            if (printer == null)
                return Loc.Get(_listLoaded ? "PrintNoPrinters" : "PrintersLoading");
            if (_probing.Contains(printer.PrinterName))
                return Loc.Get("PrinterProbing");
            if (_notResponding.Contains(printer.PrinterName))
                return Loc.Get("PrinterNotRespondingShort");
            return Loc.Get("PrinterState_" + printer.State);
        }
    }

    public bool HasPrinter => SelectedPrinter != null;

    /// <summary>Идёт опрос выбранного принтера: статус честно говорит «спрашиваю».</summary>
    public bool IsProbingPrinter => SelectedPrinter != null && _probing.Contains(SelectedPrinter.PrinterName);

    /// <summary>
    /// Первый ответ принтера ещё не пришёл: печать подождёт несколько секунд,
    /// чтобы не уйти на заготовке без полей и форматов. Не ответил — печатать
    /// можно, задание встанет в очередь.
    /// </summary>
    private bool AwaitingCapabilities =>
        SelectedPrinter is { } printer &&
        _probing.Contains(printer.PrinterName) && !_capsKnown.Contains(printer.PrinterName);

    private void RaisePrinterState()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(PrinterStatusText));
        OnPropertyChanged(nameof(IsProbingPrinter));
        OnPropertyChanged(nameof(HasPrinter));
        OnPropertyChanged(nameof(CanPrint));
    }

    [RelayCommand]
    private Task RefreshPrinters() => LoadPrinterListAsync(refresh: true);

    /// <summary>
    /// Принтер, на который реально ушло задание, запоминается: в следующий раз
    /// окно откроется на нём, а не на «Факсе» по умолчанию Windows.
    /// </summary>
    public void RememberPrinterUsed()
    {
        if (SelectedPrinter is not { } printer) return;
        if (string.Equals(_services.Settings.LastPrinterName, printer.PrinterName, StringComparison.Ordinal)) return;
        _services.Settings.LastPrinterName = printer.PrinterName;
        _services.SaveSettings();
    }

    // ----- Профили -----

    private readonly PrintProfileStore _profileStore = new();

    public ObservableCollection<PrintProfile> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteProfile))]
    private PrintProfile? _selectedProfile;

    partial void OnSelectedProfileChanged(PrintProfile? value)
    {
        if (value == null || _applyingProfile) return;
        ApplyProfile(value, explicitChoice: true);
    }

    private bool _applyingProfile;

    /// <summary>
    /// Список профилей. При открытии окна выбран и ПРИМЕНЁН профиль
    /// «Обычная печать» — в том виде, в каком его сохранил пользователь. Раньше
    /// в списке значился один профиль, а действовали настройки по умолчанию:
    /// свой «Обычный» с двусторонней печатью молча не применялся.
    /// </summary>
    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _profileStore.LoadAll())
            Profiles.Add(profile);

        var initial = Profiles.FirstOrDefault(p => p.Name == "Default") ?? Profiles.FirstOrDefault();
        _applyingProfile = true;
        SelectedProfile = initial;
        _applyingProfile = false;
        if (initial != null)
            ApplyProfile(initial, explicitChoice: false);
    }

    /// <summary>
    /// Раскладывает профиль по настройкам окна одним пересчётом: применять
    /// два десятка свойств по одному значило бы два десятка пересборок плана.
    /// </summary>
    /// <param name="explicitChoice">
    /// Профиль выбран человеком в списке: тогда действует и его принтер. При
    /// открытии окна принтер остаётся тем, на котором печатали в прошлый раз.
    /// </param>
    private void ApplyProfile(PrintProfile profile, bool explicitChoice)
    {
        _applyingProfile = true;
        var suspended = _suspendRecalc;
        _suspendRecalc = true;
        try
        {
            Imposition = profile.Imposition;
            SizeMode = profile.Size;
            CustomScalePercent = profile.CustomScale * 100;
            Orientation = profile.Orientation;
            NUpRows = profile.NUpRows;
            NUpColumns = profile.NUpColumns;
            PosterScalePercent = profile.PosterScale * 100;
            PosterOverlapMm = Units.PointsToUnit(profile.PosterOverlapPt, LengthUnit.Millimeters);
            SignatureSize = profile.SignatureSize;
            CompensateCreep = profile.CompensateCreep;
            _wantedDuplex = profile.Duplex;
            _wantedColor = profile.Color;
            Annotations = profile.Annotations;
            Forms = profile.Forms;
            HighQuality = profile.HighQuality;
            MarkPreset = profile.Marks;
            BleedMm = Units.PointsToUnit(profile.BleedPt, LengthUnit.Millimeters);
            UserMarginMm = Units.PointsToUnit(profile.UserMarginPt, LengthUnit.Millimeters);
            Parity = profile.Parity;

            if (profile.PaperName.Length > 0)
            {
                // Бумага профиля запоминается как желаемая: если список форматов
                // ещё не пришёл, она выберется, когда придёт.
                _wantedPaper = null;
                _wantedPaperName = profile.PaperName;
                var paper = PaperSizes.FirstOrDefault(p =>
                    string.Equals(p.Name, profile.PaperName, StringComparison.OrdinalIgnoreCase));
                if (paper != null)
                {
                    PaperNote = null;
                    SetPaper(paper);
                }
            }
        }
        finally
        {
            _suspendRecalc = suspended;
            _applyingProfile = false;
        }

        if (explicitChoice && profile.PrinterName.Length > 0 &&
            FindPrinter(profile.PrinterName) is { } printer && !ReferenceEquals(printer, _selectedPrinter))
        {
            ApplySelectedPrinter(printer);
        }

        // Профиль говорит, чего хочет человек; принтер — что из этого возможно.
        FitChoicesToPrinter(SelectedPrinter);
        Recalculate();
    }

    /// <summary>Сохраняет текущие настройки под указанным именем.</summary>
    public void SaveProfile(string name)
    {
        // Имя, набранное пользователем, сверяется с ПЕРЕВОДАМИ встроенных
        // профилей. Иначе человек, сохранивший свой «Буклет» поверх встроенного
        // «Буклета», получал бы в списке два одинаковых с виду пункта: хранение
        // идёт по неизменяемому опознавателю, а на экране стоит перевод.
        var storageName = BuiltInPrintProfiles.All.FirstOrDefault(p =>
            string.Equals(Loc.Get(p.NameKey), name, StringComparison.CurrentCultureIgnoreCase))
            ?.Name ?? name;

        // В профиль идёт выбор человека, а не вынужденная подгонка под
        // текущий принтер: профиль «цветной», сохранённый при выбранном
        // факсе, не должен навсегда стать серым.
        var profile = PrintProfile.FromSettings(storageName, BuildSettings(),
            SelectedPrinter?.PrinterName ?? "", SelectedPaper?.Name ?? "")
            with { Parity = Parity, Color = _wantedColor, Duplex = _wantedDuplex, HighQuality = HighQuality };
        _profileStore.Save(profile);

        Profiles.Clear();
        foreach (var item in _profileStore.LoadAll())
            Profiles.Add(item);
        _applyingProfile = true;
        SelectedProfile = Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, storageName, StringComparison.CurrentCultureIgnoreCase));
        _applyingProfile = false;
    }

    public bool CanDeleteProfile => SelectedProfile is { IsBuiltIn: false };

    /// <summary>
    /// Удаление своего профиля. После него действует «Обычная печать» — и
    /// показан, и применён: раньше список говорил «Обычная», а в окне
    /// оставались настройки удалённого профиля.
    /// </summary>
    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { IsBuiltIn: false } profile) return;
        _profileStore.Delete(profile.Name);
        LoadProfiles();
    }

    // ----- Метки и поля -----

    /// <summary>
    /// Набор меток. Каждая метка — отдельное свойство, а не флаг через
    /// конвертер: обратное преобразование одного бита не знает остальных,
    /// и такой конвертер неизбежно терял бы соседние галочки.
    /// </summary>
    private PrinterMarks _markPreset = PrinterMarks.None;

    public PrinterMarks MarkPreset
    {
        get => _markPreset;
        set
        {
            if (_markPreset == value) return;
            _markPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MarkCrop));
            OnPropertyChanged(nameof(MarkRegistration));
            OnPropertyChanged(nameof(MarkBleed));
            OnPropertyChanged(nameof(MarkFold));
            OnPropertyChanged(nameof(MarkPageInfo));
            Recalculate();
        }
    }

    private void SetMark(PrinterMarks flag, bool on) =>
        MarkPreset = on ? MarkPreset | flag : MarkPreset & ~flag;

    public bool MarkCrop
    {
        get => MarkPreset.HasFlag(PrinterMarks.CropMarks);
        set => SetMark(PrinterMarks.CropMarks, value);
    }

    public bool MarkRegistration
    {
        get => MarkPreset.HasFlag(PrinterMarks.RegistrationMarks);
        set => SetMark(PrinterMarks.RegistrationMarks, value);
    }

    public bool MarkBleed
    {
        get => MarkPreset.HasFlag(PrinterMarks.BleedMarks);
        set => SetMark(PrinterMarks.BleedMarks, value);
    }

    public bool MarkFold
    {
        get => MarkPreset.HasFlag(PrinterMarks.FoldMarks);
        set => SetMark(PrinterMarks.FoldMarks, value);
    }

    public bool MarkPageInfo
    {
        get => MarkPreset.HasFlag(PrinterMarks.PageInformation);
        set => SetMark(PrinterMarks.PageInformation, value);
    }

    [ObservableProperty] private double _bleedMm;
    partial void OnBleedMmChanged(double value) => Recalculate();

    [ObservableProperty] private double _userMarginMm;
    partial void OnUserMarginMmChanged(double value) => Recalculate();

    // ----- Ход отправки -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrint))]
    [NotifyPropertyChangedFor(nameof(CanSaveToFile))]
    private bool _isSubmitting;

    /// <summary>Сколько листов уже ушло: остановка сообщает правду, а не «0».</summary>
    public int SheetsSubmitted { get; private set; }

    [ObservableProperty] private double _submitProgress;

    [ObservableProperty] private string _submitStatus = "";

    private CancellationTokenSource? _submitCts;

    /// <summary>Токен текущей отправки: длинное задание обязано прерываться.</summary>
    public CancellationToken BeginSubmit(string status)
    {
        _submitCts?.Cancel();
        _submitCts = new CancellationTokenSource();
        IsSubmitting = true;
        SubmitProgress = 0;
        SheetsSubmitted = 0;
        SubmitStatus = status;
        return _submitCts.Token;
    }

    public void ReportSubmit(int done, int total)
    {
        SheetsSubmitted = done;
        SubmitProgress = total <= 0 ? 0 : (double)done / total * 100.0;
        SubmitStatus = Loc.F("PrintSubmitProgress", done, total);
    }

    public void EndSubmit()
    {
        IsSubmitting = false;
        SubmitProgress = 0;
        SubmitStatus = "";
    }

    [RelayCommand]
    private void CancelSubmit()
    {
        _submitCts?.Cancel();
        SubmitStatus = Loc.Get("PrintCancelling");
    }

    // ----- Страницы -----

    [ObservableProperty] private PageScope _scope = PageScope.All;
    partial void OnScopeChanged(PageScope value) => Recalculate();

    [ObservableProperty] private string _rangeText = "";

    /// <summary>
    /// Набранный диапазон сам выбирает вариант «Диапазон», как в окне печати
    /// Windows. Раньше человек вводил «3-5», нажимал «Печать» и получал весь
    /// документ: переключатель оставался на «Все страницы».
    /// </summary>
    partial void OnRangeTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Scope != PageScope.Range)
            Scope = PageScope.Range; // пересчёт сделает обработчик объёма
        else
            Recalculate();
    }

    [ObservableProperty] private PageParity _parity = PageParity.All;
    partial void OnParityChanged(PageParity value) => Recalculate();

    [ObservableProperty] private bool _reversePages;
    partial void OnReversePagesChanged(bool value) => Recalculate();

    [ObservableProperty] private int _currentPageNumber = 1;

    [ObservableProperty] private string _rangeSummary = "";
    [ObservableProperty] private string? _rangeError;

    // ----- Размер и раскладка -----

    [ObservableProperty] private SizeMode _sizeMode = SizeMode.ShrinkOversized;
    partial void OnSizeModeChanged(SizeMode value) => Recalculate();

    [ObservableProperty] private double _customScalePercent = 100;
    partial void OnCustomScalePercentChanged(double value) => Recalculate();

    [ObservableProperty] private OrientationMode _orientation = OrientationMode.Automatic;
    partial void OnOrientationChanged(OrientationMode value) => Recalculate();

    [ObservableProperty] private ImpositionMode _imposition = ImpositionMode.Single;
    partial void OnImpositionChanged(ImpositionMode value)
    {
        OnPropertyChanged(nameof(IsNUp));
        OnPropertyChanged(nameof(IsPoster));
        OnPropertyChanged(nameof(IsBooklet));
        // Буклет требует двусторонней печати с переворотом по короткому краю.
        FitChoicesToPrinter(SelectedPrinter);
        Recalculate();
    }

    public bool IsNUp => Imposition == ImpositionMode.NUp;
    public bool IsPoster => Imposition == ImpositionMode.Poster;
    public bool IsBooklet => Imposition == ImpositionMode.Booklet;

    [ObservableProperty] private int _nUpRows = 2;
    partial void OnNUpRowsChanged(int value) => Recalculate();

    [ObservableProperty] private int _nUpColumns = 2;
    partial void OnNUpColumnsChanged(int value) => Recalculate();

    [ObservableProperty] private double _posterScalePercent = 100;
    partial void OnPosterScalePercentChanged(double value) => Recalculate();

    [ObservableProperty] private double _posterOverlapMm = 5;
    partial void OnPosterOverlapMmChanged(double value) => Recalculate();

    [ObservableProperty] private int _signatureSize;
    partial void OnSignatureSizeChanged(int value) => Recalculate();

    [ObservableProperty] private bool _compensateCreep;
    partial void OnCompensateCreepChanged(bool value) => Recalculate();

    // ----- Прочее -----

    [ObservableProperty] private DuplexMode _duplex = DuplexMode.Simplex;
    partial void OnDuplexChanged(DuplexMode value)
    {
        if (!_fittingToPrinter)
        {
            _wantedDuplex = value;
            // Выбор человека тоже проходит подгонку: в буклете длинный край
            // дал бы перевёрнутые обороты, и он заменяется коротким.
            FitChoicesToPrinter(SelectedPrinter);
        }
        Recalculate();
    }

    [ObservableProperty] private int _copies = 1;

    /// <summary>
    /// Высокое качество: 600 точек на дюйм вместо 300. Заменило флажок
    /// «Печатать как изображение», который ничего не менял — листы и так
    /// всегда уходят на принтер изображением.
    /// </summary>
    [ObservableProperty] private bool _highQuality;
    partial void OnHighQualityChanged(bool value) => Recalculate();
    partial void OnCopiesChanged(int value) => Recalculate();

    [ObservableProperty] private ColorMode _color = ColorMode.Color;
    partial void OnColorChanged(ColorMode value)
    {
        if (!_fittingToPrinter) _wantedColor = value;
        Recalculate();
    }

    /// <summary>
    /// Что выбрал человек (или профиль) — в отличие от <see cref="Color"/> и
    /// <see cref="Duplex"/>, где стоит то, что умеет текущий принтер. Раньше
    /// хранилось только второе: окно открывалось на факсе (принтер Windows по
    /// умолчанию), цвет понижался до серого, и после выбора цветного принтера
    /// так и оставался серым — «печать постоянно ставится в Grayscale».
    /// </summary>
    private ColorMode _wantedColor = ColorMode.Color;
    private DuplexMode _wantedDuplex = DuplexMode.Simplex;
    private bool _fittingToPrinter;

    /// <summary>
    /// Подгоняет цвет и двустороннюю печать под принтер, не забывая выбор
    /// человека. Пока возможности принтера только читаются, ничего не
    /// понижается: «неизвестно» — не то же самое, что «не умеет».
    /// </summary>
    private void FitChoicesToPrinter(PrinterCapabilities? printer)
    {
        var known = printer != null && _capsKnown.Contains(printer.PrinterName);

        // Буклет — альбомный лист, сложенный пополам: печатается только с двух
        // сторон и только с переворотом по короткому краю.
        var duplex = Imposition == ImpositionMode.Booklet &&
                     _wantedDuplex is DuplexMode.Simplex or DuplexMode.LongEdge
            ? DuplexMode.ShortEdge
            : _wantedDuplex;

        _fittingToPrinter = true;
        try
        {
            Color = known && !printer!.SupportsColor && _wantedColor == ColorMode.Color
                ? ColorMode.Grayscale
                : _wantedColor;
            Duplex = known && !printer!.SupportsAnyDuplex && duplex is DuplexMode.LongEdge or DuplexMode.ShortEdge
                ? DuplexMode.Manual
                : duplex;
        }
        finally
        {
            _fittingToPrinter = false;
        }
    }

    [ObservableProperty] private AnnotationPolicy _annotations = AnnotationPolicy.PrintableAnnotations;
    partial void OnAnnotationsChanged(AnnotationPolicy value)
    {
        OnPropertyChanged(nameof(CanChooseForms));
        Recalculate();
    }

    /// <summary>
    /// Поля формы решаются ОТДЕЛЬНО от аннотаций: «печатать пустой бланк» —
    /// частая задача, и раньше её нельзя было выразить, не убрав заодно все
    /// комментарии с листа.
    /// </summary>
    [ObservableProperty] private FormPolicy _forms = FormPolicy.WithValues;
    partial void OnFormsChanged(FormPolicy value) => Recalculate();

    /// <summary>Выбор полей бессмысленен, когда аннотации не печатаются вовсе.</summary>
    public bool CanChooseForms => Annotations != AnnotationPolicy.DocumentOnly;

    // ----- План и предпросмотр -----

    [ObservableProperty] private PrintJobPlan? _plan;

    public ObservableCollection<SheetThumbViewModel> Sheets { get; } = new();

    [ObservableProperty] private int _selectedSheetIndex;
    partial void OnSelectedSheetIndexChanged(int value) => _ = RefreshPreviewAsync();

    [ObservableProperty] private ImageSource? _previewImage;

    [ObservableProperty] private string _summaryText = "";

    public ObservableCollection<PreflightIssue> Issues { get; } = new();

    /// <summary>
    /// Печать доступна, когда есть что печатать, принтер ответил (или ждать его
    /// уже бессмысленно) и предыдущая отправка закончилась: второе нажатие во
    /// время отправки раньше отправляло задание дважды.
    /// </summary>
    public bool CanPrint => Plan is { Sheets.Count: > 0 } && HasPrinter && !Plan.HasBlockingIssues &&
                            !IsSubmitting && !AwaitingCapabilities;

    public bool CanSaveToFile => Plan is { Sheets.Count: > 0 } && !Plan.HasBlockingIssues && !IsSubmitting;

    /// <summary>
    /// Пересчёт плана. Вызывается на КАЖДОЕ изменение настроек: план — не кэш,
    /// а текущее состояние задания, и предпросмотр обязан ему соответствовать.
    /// </summary>
    public void Recalculate()
    {
        if (_suspendRecalc || _disposed) return;

        var pageCount = _document.Document.Session.Model.Pages.Count;
        var selection = new PageSelection
        {
            Scope = Scope,
            RangeText = RangeText,
            ExplicitIndices = ExplicitIndices,
            CurrentPageIndex = Math.Clamp(CurrentPageNumber - 1, 0, Math.Max(0, pageCount - 1)),
            Parity = Parity,
            ReverseOrder = ReversePages,
        };

        var resolved = selection.Resolve(pageCount);
        RangeError = resolved.Error;
        RangeSummary = resolved.IsValid ? Loc.F("PrintPagesResolved", resolved.Normalized) : "";
        if (!resolved.IsValid)
        {
            Plan = null;
            Sheets.Clear();
            Issues.Clear();
            SummaryText = "";
            RaisePlanFlags();
            return;
        }

        var caps = SelectedPrinter ?? PrinterCapabilities.Unknown();
        var paper = SelectedPaper ?? caps.PaperSizes.FirstOrDefault() ?? new PaperSizeOption("A4", new SizePt(595.28, 841.89));

        var pages = resolved.Indices
            .Select(i =>
            {
                var size = _document.Document.GetLogicalPageSize(i);
                return new SourcePage("doc", i, new SizePt(size.WidthPoints, size.HeightPoints));
            })
            .ToList();

        var settings = BuildSettings();
        var sheets = _engine.BuildSheets(pages, settings, paper, caps);
        sheets = _engine.ApplyDuplexPairing(sheets, Duplex, Imposition);
        sheets = _engine.ApplyMarksAndOverlays(sheets, settings, new OverlayContext(
            System.IO.Path.GetFileName(_document.Title), 1, sheets.Count,
            1, Math.Max(1, Copies), DateTime.Now.ToString("dd.MM.yyyy"),
            caps.PrinterName, Environment.UserName));

        var plan = new PrintJobPlan
        {
            JobName = _document.Title,
            PrinterName = caps.PrinterName,
            Capabilities = caps,
            Sheets = sheets,
            Copies = Math.Max(1, Copies),
            Duplex = Duplex,
            // Копии размножает программа, если принтер не сортирует комплекты
            // или не принимает столько копий: задание уходит так, что напечатано
            // будет ровно заказанное.
            CollationBy = caps.SupportsCollation && caps.MaxCopies >= Math.Max(1, Copies)
                ? CollationExecutor.Printer
                : CollationExecutor.Application,
            OutputDpi = HighQuality ? 600 : 300,
        };
        // Две проверки, а не одна: план знает геометрию, но не знает документ.
        // Находки по документу идут ПОСЛЕ плановых — те могут заблокировать
        // печать, и читать их надо первыми.
        var issues = Preflight.Analyze(plan, _permissions)
            .Concat(DocumentPreflight.Analyze(_facts, Color, SelectedPrinter, Describe))
            .ToList();
        plan = plan with { Issues = issues };
        Plan = plan;

        Issues.Clear();
        foreach (var issue in plan.Issues)
            Issues.Add(issue);

        RebuildSheetList(plan);
        SummaryText = BuildSummary(plan, pages.Count);
        RaisePlanFlags();

        _ = RefreshPreviewAsync();
    }

    /// <summary>Что известно о документе: читается один раз при открытии окна.</summary>
    private PrintDocumentFacts _facts = PrintDocumentFacts.Unknown;

    /// <summary>
    /// Код находки → строка словаря. Проверка живёт в слое печати и о языках не
    /// знает, поэтому текст ей даёт интерфейс.
    /// </summary>
    private static string Describe(string code, object[] args)
    {
        var key = code switch
        {
            DocumentPreflight.CodeScanLowDpi => "PfDocScanLowDpi",
            DocumentPreflight.CodeNoContent => "PfDocNoContent",
            DocumentPreflight.CodeGrayOnColorPrinter => "PfDocGrayOnColorPrinter",
            DocumentPreflight.CodeLayers => "PfDocLayers",
            _ => code,
        };
        return args.Length == 0 ? Loc.Get(key) : Loc.F(key, args);
    }

    /// <summary>
    /// Разбор документа для проверки. Ошибка здесь не должна мешать печатать:
    /// не удалось посмотреть — просто не будет подсказок.
    /// </summary>
    private async Task LoadDocumentFactsAsync()
    {
        try
        {
            var handle = _document.Document.PrimaryHandle;
            var summary = await handle.GetImageSummaryAsync(12, CancellationToken.None);
            var layers = _services.Layers.IsAvailable && handle.FilePath.Length > 0
                ? (await _services.Layers.GetLayersAsync(
                    handle.FilePath, _document.Document.Password, CancellationToken.None)).Count
                : 0;
            _facts = new PrintDocumentFacts(
                _document.PageCount, summary.SampledPages, summary.Images,
                summary.TextLength, summary.AverageImageDpi, layers > 0);
            Recalculate();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось разобрать документ для предварительной проверки печати");
        }
    }

    private LayoutSettings BuildSettings() => new()
    {
        Imposition = Imposition,
        Size = SizeMode,
        CustomScale = Math.Max(0.01, CustomScalePercent / 100.0),
        Orientation = Orientation,
        Duplex = Duplex,
        Color = Color,
        Annotations = Annotations,
        Forms = Forms,
        NUp = new NUpSettings
        {
            Rows = Math.Max(1, NUpRows),
            Columns = Math.Max(1, NUpColumns),
            HorizontalGapPt = 8,
            VerticalGapPt = 8,
        },
        Poster = new PosterSettings
        {
            Scale = Math.Max(0.01, PosterScalePercent / 100.0),
            OverlapPt = Units.UnitToPoints(Math.Max(0, PosterOverlapMm), LengthUnit.Millimeters),
        },
        UserMarginsPt = MarginsPt.Uniform(
            Units.UnitToPoints(Math.Max(0, UserMarginMm), LengthUnit.Millimeters)),
        Marks = new MarkSettings
        {
            Marks = MarkPreset,
            BleedPt = Units.UnitToPoints(Math.Max(0, BleedMm), LengthUnit.Millimeters),
        },
        Booklet = new BookletSettings
        {
            SignatureSize = SignatureSize,
            CompensateCreep = CompensateCreep,
        },
    };

    private void RebuildSheetList(PrintJobPlan plan)
    {
        Sheets.Clear();
        for (var i = 0; i < plan.Sheets.Count; i++)
        {
            var sheet = plan.Sheets[i];
            Sheets.Add(new SheetThumbViewModel
            {
                Index = i,
                Caption = plan.Duplex == DuplexMode.Simplex
                    ? Loc.F("PrintSheetN", i + 1)
                    : Loc.F(sheet.IsFront ? "PrintSheetFront" : "PrintSheetBack", i / 2 + 1),
                IsFront = sheet.IsFront,
                HasWarning = sheet.HasClippedContent,
            });
        }
        if (SelectedSheetIndex >= Sheets.Count)
            SelectedSheetIndex = 0;
    }

    private string BuildSummary(PrintJobPlan plan, int sourcePages)
    {
        var parts = new List<string>
        {
            Loc.F("PrintSummaryPages", sourcePages),
            Loc.F("PrintSummarySheets", plan.SheetCount),
            Loc.F("PrintSummarySides", plan.SideCount),
        };
        if (plan.Copies > 1)
            parts.Add(Loc.F("PrintSummaryCopies", plan.Copies));
        return string.Join("   ·   ", parts);
    }

    private void RaisePlanFlags()
    {
        OnPropertyChanged(nameof(CanPrint));
        OnPropertyChanged(nameof(CanSaveToFile));
        OnPropertyChanged(nameof(HasPrinter));
        OnPropertyChanged(nameof(PrinterStatusText));
    }

    /// <summary>
    /// Текст находки для человека: код и значения из слоя печати, перевод —
    /// из словаря интерфейса.
    /// </summary>
    public static string DescribeSubmitFailure(Exception ex) => ex switch
    {
        Services.Printing.PrintSubmitException submit => Loc.F(submit.Code, submit.Args),
        PrintToFileException file => Loc.Get(file.Code),
        System.Printing.PrintQueueException queue => Loc.F("PrintQueueFailed", queue.Message),
        _ => ex.Message,
    };

    /// <summary>
    /// Перерисовывает предпросмотр выбранного листа. Устаревшие задачи
    /// отменяются: при быстром переборе настроек в окно должен попасть
    /// результат последней, а не той, что успела досчитаться.
    /// </summary>
    public async Task RefreshPreviewAsync()
    {
        if (_disposed || Plan is not { } plan || plan.Sheets.Count == 0)
        {
            PreviewImage = null;
            return;
        }

        var index = Math.Clamp(SelectedSheetIndex, 0, plan.Sheets.Count - 1);

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        try
        {
            var composed = SheetComposer.Compose(plan.Sheets[index],
                SheetComposer.DpiForPreview(plan.Sheets[index].PaperSizePt, 700, 900));
            var renderer = new PrintPlanRenderer(_document.Document);
            var image = await renderer.RenderSheetAsync(composed, cts.Token, drawGuides: true);

            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _previewCts)) return;
            PreviewImage = BitmapFactory.ToBitmapSource(image);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось построить предпросмотр печати");
            PreviewImage = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _previewCts?.Cancel();
        _probeCts.Cancel();
    }
}
