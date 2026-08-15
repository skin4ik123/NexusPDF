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
    private readonly PrintLayoutEngine _engine = new();

    private CancellationTokenSource? _previewCts;
    private bool _suspendRecalc;
    private bool _disposed;

    public PrintCenterViewModel(DocumentViewModel document, AppServices services)
    {
        _document = document;
        _services = services;

        _suspendRecalc = true;
        LoadPrinters();
        LoadProfiles();
        CurrentPageNumber = document.CurrentPageNumber;
        _suspendRecalc = false;

        Recalculate();
        _ = LoadPermissionsAsync();
    }

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

    [ObservableProperty]
    private PrinterCapabilities? _selectedPrinter;

    partial void OnSelectedPrinterChanged(PrinterCapabilities? value)
    {
        if (value == null) return;

        // Смена принтера меняет доступные форматы: несовместимые настройки
        // сбрасываются на допустимые, а не молча остаются неверными.
        _suspendRecalc = true;
        PaperSizes.Clear();
        foreach (var paper in value.PaperSizes)
            PaperSizes.Add(paper);

        // A4 предпочитается явно и раньше Letter: у части драйверов Letter идёт
        // в списке первым, и поиск «A4 или Letter» одним проходом выбирал бы
        // американский формат российскому пользователю.
        SelectedPaper = PaperSizes.FirstOrDefault(p => p.Name == SelectedPaper?.Name)
                        ?? PaperSizes.FirstOrDefault(p => p.Name == "A4")
                        ?? PaperSizes.FirstOrDefault(p => p.Name == "Letter")
                        ?? PaperSizes.FirstOrDefault();

        if (Duplex != DuplexMode.Simplex && !value.SupportsAnyDuplex)
            Duplex = DuplexMode.Manual;
        if (Color == ColorMode.Color && !value.SupportsColor)
            Color = ColorMode.Grayscale;

        _suspendRecalc = false;
        Recalculate();
    }

    public ObservableCollection<PaperSizeOption> PaperSizes { get; } = new();

    [ObservableProperty]
    private PaperSizeOption? _selectedPaper;
    partial void OnSelectedPaperChanged(PaperSizeOption? value) => Recalculate();

    /// <summary>Строка состояния принтера на человеческом языке.</summary>
    public string PrinterStatusText => SelectedPrinter == null
        ? Loc.Get("PrintNoPrinters")
        : Loc.Get("PrinterState_" + SelectedPrinter.State);

    public bool HasPrinter => SelectedPrinter != null;

    [RelayCommand]
    private void RefreshPrinters()
    {
        var previous = SelectedPrinter?.PrinterName;
        LoadPrinters();
        if (previous != null)
            SelectedPrinter = Printers.FirstOrDefault(p => p.PrinterName == previous) ?? SelectedPrinter;
        Recalculate();
    }

    private void LoadPrinters()
    {
        Printers.Clear();
        foreach (var printer in _printers.Discover())
            Printers.Add(printer);
        SelectedPrinter = Printers.FirstOrDefault(p => p.IsDefault) ?? Printers.FirstOrDefault();
        OnPropertyChanged(nameof(HasPrinter));
        OnPropertyChanged(nameof(PrinterStatusText));
    }

    // ----- Профили -----

    private readonly PrintProfileStore _profileStore = new();

    public ObservableCollection<PrintProfile> Profiles { get; } = new();

    [ObservableProperty]
    private PrintProfile? _selectedProfile;

    partial void OnSelectedProfileChanged(PrintProfile? value)
    {
        if (value == null || _applyingProfile) return;
        ApplyProfile(value);
    }

    private bool _applyingProfile;

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _profileStore.LoadAll())
            Profiles.Add(profile);

        // Профиль показывается выбранным, но НЕ применяется: применение
        // затёрло бы настройки, уже подогнанные под возможности принтера
        // (например, серый режим на монохромном устройстве).
        _applyingProfile = true;
        SelectedProfile ??= Profiles.FirstOrDefault();
        _applyingProfile = false;
    }

    /// <summary>
    /// Раскладывает профиль по настройкам окна одним пересчётом: применять
    /// два десятка свойств по одному значило бы два десятка пересборок плана.
    /// </summary>
    private void ApplyProfile(PrintProfile profile)
    {
        _applyingProfile = true;
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
            Duplex = profile.Duplex;
            Color = profile.Color;
            Annotations = profile.Annotations;
            PrintAsImage = profile.PrintAsImage;
            MarkPreset = profile.Marks;
            BleedMm = Units.PointsToUnit(profile.BleedPt, LengthUnit.Millimeters);
            UserMarginMm = Units.PointsToUnit(profile.UserMarginPt, LengthUnit.Millimeters);
            Parity = profile.Parity;

            if (profile.PaperName.Length > 0)
            {
                var paper = PaperSizes.FirstOrDefault(p => p.Name == profile.PaperName);
                if (paper != null) SelectedPaper = paper;
            }
        }
        finally
        {
            _suspendRecalc = false;
            _applyingProfile = false;
        }
        Recalculate();
    }

    /// <summary>Сохраняет текущие настройки под указанным именем.</summary>
    public void SaveProfile(string name)
    {
        var profile = PrintProfile.FromSettings(name, BuildSettings(),
            SelectedPrinter?.PrinterName ?? "", SelectedPaper?.Name ?? "")
            with { Parity = Parity };
        _profileStore.Save(profile);

        LoadProfiles();
        _applyingProfile = true;
        SelectedProfile = Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        _applyingProfile = false;
    }

    public bool CanDeleteProfile => SelectedProfile is { IsBuiltIn: false };

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { IsBuiltIn: false } profile) return;
        _profileStore.Delete(profile.Name);
        LoadProfiles();
        _applyingProfile = true;
        SelectedProfile = Profiles.FirstOrDefault();
        _applyingProfile = false;
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

    [ObservableProperty] private bool _isSubmitting;

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
        SubmitStatus = status;
        return _submitCts.Token;
    }

    public void ReportSubmit(int done, int total)
    {
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
    partial void OnRangeTextChanged(string value) => Recalculate();

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
    partial void OnDuplexChanged(DuplexMode value) => Recalculate();

    [ObservableProperty] private int _copies = 1;
    partial void OnCopiesChanged(int value) => Recalculate();

    [ObservableProperty] private ColorMode _color = ColorMode.Color;
    partial void OnColorChanged(ColorMode value) => Recalculate();

    [ObservableProperty] private AnnotationPolicy _annotations = AnnotationPolicy.PrintableAnnotations;
    partial void OnAnnotationsChanged(AnnotationPolicy value) => Recalculate();

    [ObservableProperty] private bool _printAsImage;
    partial void OnPrintAsImageChanged(bool value) => Recalculate();

    // ----- План и предпросмотр -----

    [ObservableProperty] private PrintJobPlan? _plan;

    public ObservableCollection<SheetThumbViewModel> Sheets { get; } = new();

    [ObservableProperty] private int _selectedSheetIndex;
    partial void OnSelectedSheetIndexChanged(int value) => _ = RefreshPreviewAsync();

    [ObservableProperty] private ImageSource? _previewImage;

    [ObservableProperty] private string _summaryText = "";

    public ObservableCollection<PreflightIssue> Issues { get; } = new();

    public bool CanPrint => Plan is { Sheets.Count: > 0 } && HasPrinter && !Plan.HasBlockingIssues;
    public bool CanSaveToFile => Plan is { Sheets.Count: > 0 } && !Plan.HasBlockingIssues;

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
            CollationBy = caps.SupportsCollation ? CollationExecutor.Printer : CollationExecutor.Application,
        };
        plan = plan with { Issues = Preflight.Analyze(plan, _permissions) };
        Plan = plan;

        Issues.Clear();
        foreach (var issue in plan.Issues)
            Issues.Add(issue);

        RebuildSheetList(plan);
        SummaryText = BuildSummary(plan, pages.Count);
        RaisePlanFlags();

        _ = RefreshPreviewAsync();
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
        PrintAsImage = PrintAsImage,
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
        _printers.Dispose();
    }
}
