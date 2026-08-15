using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Application;
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
        CurrentPageNumber = document.CurrentPageNumber;
        _suspendRecalc = false;

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
        plan = plan with { Issues = Preflight.Analyze(plan) };
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
