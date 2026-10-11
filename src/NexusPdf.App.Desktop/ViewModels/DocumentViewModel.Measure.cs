using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Инструмент измерения: расстояние, периметр, площадь и калибровка масштаба.
///
/// Точки измерения берутся с привязкой к векторной графике страницы (концы
/// линий, пересечения, середины): на чертеже из САПР точность измерения — это
/// точность попадания в линию, а не в пиксель рядом с ней. Масштаб задаётся
/// человеком («1 : 200») или калибровкой по отрезку с известной длиной: сам
/// PDF о масштабе чаще всего ничего не знает.
/// </summary>
public sealed partial class DocumentViewModel
{
    public enum MeasureTool { None, Distance, Perimeter, Area, Calibrate }

    /// <summary>Радиус привязки на экране — ближе этого курсор прилипает к линии.</summary>
    private const double SnapRadiusDip = 10;

    /// <summary>Кегль подписи измерения на листе.</summary>
    public const double MeasureFontSizePt = 9;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeasuring))]
    [NotifyPropertyChangedFor(nameof(IsMeasureDistanceActive))]
    [NotifyPropertyChangedFor(nameof(IsMeasurePerimeterActive))]
    [NotifyPropertyChangedFor(nameof(IsMeasureAreaActive))]
    [NotifyPropertyChangedFor(nameof(IsMeasureCalibrateActive))]
    private MeasureTool _activeMeasureTool = MeasureTool.None;

    public bool IsMeasuring => ActiveMeasureTool != MeasureTool.None;
    public bool IsMeasureDistanceActive => ActiveMeasureTool == MeasureTool.Distance;
    public bool IsMeasurePerimeterActive => ActiveMeasureTool == MeasureTool.Perimeter;
    public bool IsMeasureAreaActive => ActiveMeasureTool == MeasureTool.Area;
    public bool IsMeasureCalibrateActive => ActiveMeasureTool == MeasureTool.Calibrate;

    /// <summary>Во сколько раз натура больше листа: 200 для чертежа 1:200.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureScaleText))]
    private double _measureScaleRatio = 100;

    [ObservableProperty]
    private MeasureUnit _measureUnit = MeasureUnit.Meter;

    [ObservableProperty]
    private int _measureDecimals = 2;

    /// <summary>Привязка к линиям чертежа. Alt при наведении отключает её на один раз.</summary>
    [ObservableProperty]
    private bool _measureSnap = true;

    [ObservableProperty]
    private uint _measureColorArgb = 0xFFDC2626;

    /// <summary>Последняя измеренная величина — для панели инструментов.</summary>
    [ObservableProperty]
    private string _lastMeasurementText = "";

    /// <summary>Масштаб как текст поля ввода: «1 : 200». Непонятный ввод не принимается, поле возвращается к прежнему.</summary>
    public string MeasureScaleText
    {
        get => MeasurementMath.FormatRatio(MeasureScaleRatio, CultureInfo.CurrentCulture);
        set
        {
            if (MeasurementMath.TryParseRatio(value, out var ratio) && ratio > 0)
                MeasureScaleRatio = ratio;
            else
                OnPropertyChanged(nameof(MeasureScaleText));
        }
    }

    public static string UnitSymbol(MeasureUnit unit) => Loc.Get("MeasureUnit" + unit);

    partial void OnMeasureScaleRatioChanged(double value) => RefreshMeasurePreview();
    partial void OnMeasureUnitChanged(MeasureUnit value) => RefreshMeasurePreview();
    partial void OnMeasureDecimalsChanged(int value) => RefreshMeasurePreview();

    partial void OnMeasureColorArgbChanged(uint value)
    {
        foreach (var page in Pages)
            page.MeasureBrush = BrushFor(value);
    }

    private static Brush BrushFor(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    /// <summary>Настройки измерения, с которыми открывается документ (запомненные с прошлого раза).</summary>
    public void ApplyMeasureSettings(double scaleRatio, MeasureUnit unit, int decimals, bool snap)
    {
        if (scaleRatio > 0 && double.IsFinite(scaleRatio)) MeasureScaleRatio = scaleRatio;
        MeasureUnit = unit;
        MeasureDecimals = Math.Clamp(decimals, 0, 4);
        MeasureSnap = snap;
    }

    // ----- Ведущееся измерение -----

    private PageViewModel? _measurePage;
    private PageViewModel? _measureHoverPage;
    private readonly List<InkPoint> _measurePoints = new();
    private InkPoint? _measureHoverPt;
    private MeasureTool _toolBeforeCalibrate = MeasureTool.Distance;

    public bool HasMeasureInProgress => _measurePoints.Count > 0;

    /// <summary>Просьба спросить у человека настоящую длину отрезка, который он только что указал.</summary>
    public sealed record CalibrationRequest(PageViewModel Page, double PaperLengthPt);

    public event EventHandler<CalibrationRequest>? CalibrationRequested;

    public void SelectMeasureTool(MeasureTool tool)
    {
        if (ActiveMeasureTool == tool)
        {
            StopMeasurement();
            return;
        }

        // Измерение несовместимо с остальными режимами мыши: рисованием,
        // размещением объекта, выделением — всё это закрывается.
        CancelPlacement();
        ClearTextSelection();
        ClearObjectSelection();
        if (IsDrawing)
            SelectDrawTool(DrawTool.None);
        ResetMeasurementState();

        if (tool == MeasureTool.Calibrate && ActiveMeasureTool != MeasureTool.None)
            _toolBeforeCalibrate = ActiveMeasureTool;
        var wasMeasuring = IsMeasuring;
        ActiveMeasureTool = tool;
        StatusText = tool switch
        {
            MeasureTool.Distance => Loc.Get("MeasureHintDistance"),
            MeasureTool.Perimeter => Loc.Get("MeasureHintPerimeter"),
            MeasureTool.Area => Loc.Get("MeasureHintArea"),
            MeasureTool.Calibrate => Loc.Get("MeasureHintCalibrate"),
            _ => Loc.Get("Ready"),
        };
        if (!wasMeasuring)
            _ = SuggestScaleFromPageTextAsync();
    }

    private bool _scaleFromTextChecked;

    /// <summary>
    /// Масштаб со штампа чертежа. Сам PDF о масштабе обычно не знает, но на
    /// листе он написан: «SCALE 1:200», «МАСШТАБ 1:100». При первом включении
    /// линейки текст текущей страницы просматривается, и найденный масштаб
    /// подставляется — человек видит это в строке состояния и может поправить.
    /// </summary>
    private async Task SuggestScaleFromPageTextAsync()
    {
        if (_scaleFromTextChecked || Pages.Count == 0) return;
        _scaleFromTextChecked = true;
        try
        {
            var index = Math.Clamp(CurrentPageNumber - 1, 0, Pages.Count - 1);
            var (handle, pageIndex) = await Document.ResolveTextPageAsync(index, CancellationToken.None);
            var text = await handle.GetPageTextAsync(pageIndex, CancellationToken.None);
            var found = MeasurementMath.FindScaleInText(text);
            if (found is not { } ratio || !IsMeasuring) return;
            if (Math.Abs(ratio - MeasureScaleRatio) < 1e-9) return;
            MeasureScaleRatio = ratio;
            StatusText = Loc.F("MeasureScaleFromSheet", MeasureScaleText);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Масштаб со штампа прочитать не удалось");
        }
    }

    /// <summary>Выключить инструмент целиком (Esc без начатого измерения, смена группы на рельсе).</summary>
    public void StopMeasurement()
    {
        if (!IsMeasuring && !HasMeasureInProgress) return;
        ResetMeasurementState();
        ActiveMeasureTool = MeasureTool.None;
        StatusText = Loc.Get("Ready");
    }

    /// <summary>Отменить начатое измерение, оставив инструмент включённым.</summary>
    public void CancelMeasurement()
    {
        if (!HasMeasureInProgress) return;
        ResetMeasurementState();
        StatusText = Loc.Get("MeasureCancelled");
    }

    private void ResetMeasurementState()
    {
        _measurePoints.Clear();
        _measureHoverPt = null;
        _measurePage?.ClearMeasurePreview();
        _measureHoverPage?.ClearMeasurePreview();
        _measurePage = null;
        _measureHoverPage = null;
    }

    /// <summary>Убрать последнюю поставленную точку (Backspace); без точек — отменить измерение.</summary>
    public void UndoMeasurePoint()
    {
        if (_measurePoints.Count == 0) return;
        _measurePoints.RemoveAt(_measurePoints.Count - 1);
        if (_measurePoints.Count == 0)
        {
            CancelMeasurement();
            return;
        }
        if (_measurePage != null)
            UpdateMeasurePreview(_measurePage, _measureHoverPt, SnapKind.None);
    }

    /// <summary>
    /// Курсор над страницей: показывается точка, в которую ляжет щелчок (с
    /// привязкой и ограничением угла), и живая величина отрезка до неё.
    /// </summary>
    public void MeasureHover(PageViewModel page, double xPt, double yPt, bool constrainAngle, bool noSnap)
    {
        if (!IsMeasuring) return;
        if (_measurePage != null && !ReferenceEquals(page, _measurePage))
        {
            // Измерение идёт на другой странице — здесь ничего не ведём.
            if (_measureHoverPage != null && !ReferenceEquals(_measureHoverPage, _measurePage))
                _measureHoverPage.ClearMeasurePreview();
            _measureHoverPage = null;
            return;
        }
        if (_measureHoverPage != null && !ReferenceEquals(_measureHoverPage, page))
            _measureHoverPage.ClearMeasurePreview();
        _measureHoverPage = page;

        var (point, kind) = ResolvePoint(page, xPt, yPt, constrainAngle, noSnap);
        _measureHoverPt = point;
        UpdateMeasurePreview(page, point, kind);
    }

    /// <summary>Курсор ушёл со страницы: подсказка у курсора прячется, поставленные точки остаются.</summary>
    public void MeasureHoverLeave()
    {
        if (!IsMeasuring) return;
        _measureHoverPt = null;
        if (_measureHoverPage != null && !ReferenceEquals(_measureHoverPage, _measurePage))
            _measureHoverPage.ClearMeasurePreview();
        if (_measurePage != null && _measurePoints.Count > 0)
            UpdateMeasurePreview(_measurePage, null, SnapKind.None);
        else
            _measureHoverPage?.ClearMeasurePreview();
    }

    /// <summary>Щелчок: ещё одна точка измерения; расстояние и калибровка заканчиваются второй точкой.</summary>
    public void MeasureClick(PageViewModel page, double xPt, double yPt, bool constrainAngle, bool noSnap)
    {
        if (!IsMeasuring || IsBusy) return;
        if (_measurePage != null && !ReferenceEquals(page, _measurePage))
            ResetMeasurementState(); // продолжать на другой странице нельзя — начинаем заново
        _measurePage = page;
        _measureHoverPage = page;

        var (point, kind) = ResolvePoint(page, xPt, yPt, constrainAngle, noSnap);

        // Щелчок по первой точке замыкает контур.
        if (kind == SnapKind.OwnVertex && _measurePoints.Count >= 3 &&
            MeasurementMath.Distance(point, _measurePoints[0]) < 0.001 &&
            ActiveMeasureTool is MeasureTool.Perimeter or MeasureTool.Area)
        {
            FinishMeasurement();
            return;
        }

        // Второй щелчок двойного щелчка приходит в ту же точку — он не точка.
        if (_measurePoints.Count > 0 && MeasurementMath.Distance(point, _measurePoints[^1]) < 0.01)
            return;

        _measurePoints.Add(point);
        switch (ActiveMeasureTool)
        {
            case MeasureTool.Distance when _measurePoints.Count >= 2:
                CommitMeasurement();
                return;
            case MeasureTool.Calibrate when _measurePoints.Count >= 2:
            {
                var length = MeasurementMath.PathLengthPt(_measurePoints, false);
                ResetMeasurementState();
                if (length > 0.01)
                    CalibrationRequested?.Invoke(this, new CalibrationRequest(page, length));
                return;
            }
        }

        UpdateMeasurePreview(page, point, SnapKind.None);
        StatusText = ActiveMeasureTool == MeasureTool.Area
            ? Loc.Get("MeasureHintAreaNext")
            : Loc.Get("MeasureHintNext");
    }

    /// <summary>Enter, двойной щелчок или правая кнопка: закончить ломаную или контур.</summary>
    public void FinishMeasurement()
    {
        if (!IsMeasuring || _measurePage == null) return;
        var minimum = ActiveMeasureTool == MeasureTool.Area ? 3 : 2;
        if (_measurePoints.Count < minimum)
        {
            if (_measurePoints.Count > 0)
                StatusText = Loc.Get("MeasureNeedMorePoints");
            return;
        }
        if (ActiveMeasureTool == MeasureTool.Calibrate)
        {
            var length = MeasurementMath.PathLengthPt(_measurePoints, false);
            var page = _measurePage;
            ResetMeasurementState();
            CalibrationRequested?.Invoke(this, new CalibrationRequest(page, length));
            return;
        }
        CommitMeasurement();
    }

    /// <summary>Калибровка: человек назвал настоящую длину указанного отрезка.</summary>
    public void ApplyCalibration(double paperLengthPt, double realValue, MeasureUnit unit)
    {
        var ratio = MeasurementMath.RatioFromKnownLength(paperLengthPt, realValue, unit);
        if (ratio > 0 && double.IsFinite(ratio))
        {
            MeasureUnit = unit;
            MeasureScaleRatio = ratio;
            StatusText = Loc.F("MeasureScaleSet", MeasureScaleText);
        }
        if (ActiveMeasureTool == MeasureTool.Calibrate)
            ActiveMeasureTool = _toolBeforeCalibrate == MeasureTool.Calibrate
                ? MeasureTool.Distance
                : _toolBeforeCalibrate;
    }

    /// <summary>Калибровку отменили: инструмент возвращается к прежнему.</summary>
    public void CancelCalibration()
    {
        if (ActiveMeasureTool == MeasureTool.Calibrate)
            ActiveMeasureTool = _toolBeforeCalibrate == MeasureTool.Calibrate
                ? MeasureTool.Distance
                : _toolBeforeCalibrate;
        StatusText = Loc.Get("Ready");
    }

    private void CommitMeasurement()
    {
        if (_measurePage == null || _measurePoints.Count < 2) return;
        var page = _measurePage;
        var kind = ActiveMeasureTool switch
        {
            MeasureTool.Perimeter => MeasurementKind.Perimeter,
            MeasureTool.Area => MeasurementKind.Area,
            _ => MeasurementKind.Distance,
        };
        var points = _measurePoints.ToList();
        var label = BuildLabel(kind, points);
        var draft = new MeasurementDraft(
            kind, points, MeasureScaleRatio, MeasureUnit, MeasureDecimals, label,
            MeasureColorArgb, Environment.UserName, MeasureFontSizePt)
        {
            PlacedRotation = page.PageRef.RotationOffset,
        };

        ResetMeasurementState();
        Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex, draft));
        LastMeasurementText = label;
        StatusText = Loc.F("MeasureDone", label);
    }

    /// <summary>Подпись величины: число в культуре пользователя и единица; площадь — с «²».</summary>
    public string BuildLabel(MeasurementKind kind, IReadOnlyList<InkPoint> points)
    {
        var culture = CultureInfo.CurrentCulture;
        var unit = UnitSymbol(MeasureUnit);
        if (kind == MeasurementKind.Area)
        {
            var area = MeasurementMath.ToRealArea(
                MeasurementMath.PolygonAreaPt2(points), MeasureScaleRatio, MeasureUnit);
            return MeasurementMath.FormatValue(area, MeasureDecimals, culture) + " " + unit + "²";
        }
        var length = MeasurementMath.ToRealLength(
            MeasurementMath.PathLengthPt(points, false), MeasureScaleRatio, MeasureUnit);
        return MeasurementMath.FormatValue(length, MeasureDecimals, culture) + " " + unit;
    }

    private string FormatLength(double lengthPt)
    {
        var value = MeasurementMath.ToRealLength(lengthPt, MeasureScaleRatio, MeasureUnit);
        return MeasurementMath.FormatValue(value, MeasureDecimals, CultureInfo.CurrentCulture) + " " + UnitSymbol(MeasureUnit);
    }

    private string FormatArea(double areaPt2)
    {
        var value = MeasurementMath.ToRealArea(areaPt2, MeasureScaleRatio, MeasureUnit);
        return MeasurementMath.FormatValue(value, MeasureDecimals, CultureInfo.CurrentCulture) + " " + UnitSymbol(MeasureUnit) + "²";
    }

    /// <summary>
    /// Точка, в которую ляжет щелчок: привязка к чертежу (если не отключена
    /// клавишей Alt), затем ограничение угла по Shift — оно важнее привязки,
    /// как в чертёжных программах.
    /// </summary>
    private (InkPoint Point, SnapKind Kind) ResolvePoint(
        PageViewModel page, double xPt, double yPt, bool constrainAngle, bool noSnap)
    {
        var point = new InkPoint(xPt, yPt);
        var kind = SnapKind.None;

        if (MeasureSnap && !noSnap)
        {
            var radius = SnapRadiusDip / Math.Max(page.DisplayScale, 0.01);

            // Свои точки — первыми: замкнуть контур важнее, чем прилипнуть к
            // линии чертежа, проходящей через ту же вершину.
            var limit = ActiveMeasureTool is MeasureTool.Perimeter or MeasureTool.Area
                ? _measurePoints.Count - 1   // последняя точка — та, от которой тянем
                : 0;
            for (var i = 0; i < limit; i++)
            {
                if (MeasurementMath.Distance(point, _measurePoints[i]) <= radius)
                    return (_measurePoints[i], SnapKind.OwnVertex);
            }

            if (SnapIndexFor(page)?.Find(xPt, yPt, radius) is { } hit)
            {
                point = new InkPoint(hit.X, hit.Y);
                kind = hit.Kind;
            }
        }

        if (constrainAngle && _measurePoints.Count > 0)
        {
            var constrained = MeasurementMath.ConstrainTo45(_measurePoints[^1], point);
            if (MeasurementMath.Distance(constrained, point) > 1e-9)
            {
                point = constrained;
                kind = SnapKind.None;
            }
        }
        return (point, kind);
    }

    private void RefreshMeasurePreview()
    {
        if (_measurePage != null && _measurePoints.Count > 0)
            UpdateMeasurePreview(_measurePage, _measureHoverPt, SnapKind.None);
    }

    /// <summary>Ломаная, маркер привязки и живая величина для карточки страницы.</summary>
    private void UpdateMeasurePreview(PageViewModel page, InkPoint? hover, SnapKind snapKind)
    {
        page.MeasureBrush = BrushFor(MeasureColorArgb);

        var guide = new PointCollection(_measurePoints.Count + 1);
        foreach (var p in _measurePoints)
            guide.Add(new Point(p.XPt, p.YPt));
        if (hover is { } h)
            guide.Add(new Point(h.XPt, h.YPt));
        page.MeasureGuide = guide;

        var closing = new PointCollection(2);
        if (ActiveMeasureTool == MeasureTool.Area && _measurePoints.Count >= 2 && hover is { } tail)
        {
            closing.Add(new Point(tail.XPt, tail.YPt));
            closing.Add(new Point(_measurePoints[0].XPt, _measurePoints[0].YPt));
        }
        page.MeasureClosing = closing;

        var side = page.MarkerPt * 0.45;
        page.MeasureVertices = _measurePoints
            .Select(p => new Rect(p.XPt - side / 2, p.YPt - side / 2, side, side))
            .ToList();

        if (hover is { } snapped && snapKind != SnapKind.None)
        {
            var marker = page.MarkerPt;
            page.MeasureSnapRect = new Rect(snapped.XPt - marker / 2, snapped.YPt - marker / 2, marker, marker);
            page.MeasureSnapKind = snapKind;
        }
        else
        {
            page.MeasureSnapKind = SnapKind.None;
        }

        if (hover is { } cursor)
        {
            page.MeasureReadoutAnchorPt = new Point(cursor.XPt, cursor.YPt);
            page.MeasureReadoutText = ReadoutFor(cursor);
        }
        else
        {
            page.MeasureReadoutText = "";
        }
    }

    /// <summary>Что показать у курсора: длина отрезка, сумма, площадь — по инструменту.</summary>
    private string ReadoutFor(InkPoint cursor)
    {
        var culture = CultureInfo.CurrentCulture;
        if (_measurePoints.Count == 0)
            return "";
        var last = _measurePoints[^1];
        var segmentPt = MeasurementMath.Distance(last, cursor);

        switch (ActiveMeasureTool)
        {
            case MeasureTool.Calibrate:
                return Loc.F("MeasureReadoutCalibrate",
                    (segmentPt * MeasurementMath.PtToMm).ToString("0.0", culture));
            case MeasureTool.Distance:
            {
                var dx = MeasurementMath.ToRealLength(Math.Abs(cursor.XPt - last.XPt), MeasureScaleRatio, MeasureUnit);
                var dy = MeasurementMath.ToRealLength(Math.Abs(cursor.YPt - last.YPt), MeasureScaleRatio, MeasureUnit);
                return Loc.F("MeasureReadoutDistance",
                    FormatLength(segmentPt),
                    MeasurementMath.FormatValue(dx, MeasureDecimals, culture),
                    MeasurementMath.FormatValue(dy, MeasureDecimals, culture),
                    MeasurementMath.AngleDegrees(last, cursor).ToString("0.0", culture));
            }
            case MeasureTool.Perimeter:
            {
                var total = MeasurementMath.PathLengthPt(_measurePoints, false) + segmentPt;
                return Loc.F("MeasureReadoutPerimeter", FormatLength(segmentPt), FormatLength(total));
            }
            case MeasureTool.Area:
            {
                var polygon = new List<InkPoint>(_measurePoints) { cursor };
                var area = polygon.Count >= 3 ? MeasurementMath.PolygonAreaPt2(polygon) : 0;
                var perimeter = MeasurementMath.PathLengthPt(polygon, closed: polygon.Count >= 3);
                return Loc.F("MeasureReadoutArea", FormatArea(area), FormatLength(perimeter));
            }
            default:
                return "";
        }
    }

    // ----- Привязка к геометрии страницы -----

    private readonly Dictionary<(Guid Source, int Page, int Rotation), VectorSnapIndex> _snapIndexes = new();
    private readonly HashSet<(Guid Source, int Page, int Rotation)> _snapLoading = new();

    private static (Guid, int, int) SnapKeyOf(PageViewModel page) =>
        (page.PageRef.SourceId, page.PageRef.SourcePageIndex, page.PageRef.RotationOffset);

    /// <summary>Индекс отрезков страницы; пока он читается — null, привязки нет.</summary>
    private VectorSnapIndex? SnapIndexFor(PageViewModel page)
    {
        var key = SnapKeyOf(page);
        if (_snapIndexes.TryGetValue(key, out var index))
            return index;
        if (_snapLoading.Add(key))
            _ = LoadSnapIndexAsync(page, key);
        return null;
    }

    /// <summary>
    /// Чтение геометрии идёт один раз на страницу и в фоне: на большом чертеже
    /// это сотни тысяч отрезков и секунда-другая, а мышь ждать не должна.
    /// </summary>
    private async Task LoadSnapIndexAsync(PageViewModel page, (Guid Source, int Page, int Rotation) key)
    {
        var showStatus = IsMeasuring;
        if (showStatus)
            StatusText = Loc.Get("MeasureSnapPreparing");
        try
        {
            var handle = Document.Handles[key.Source];
            var segments = await handle.GetPageSegmentsAsync(key.Page, key.Rotation, CancellationToken.None);
            var width = page.SizePt.WidthPoints;
            var height = page.SizePt.HeightPoints;
            var index = await Task.Run(() => new VectorSnapIndex(segments, width, height));
            // Индекс большого чертежа — десятки мегабайт; держим лишь несколько
            // последних страниц, остальные перечитаются при надобности.
            if (_snapIndexes.Count >= 6)
                _snapIndexes.Clear();
            _snapIndexes[key] = index;
            Serilog.Log.Debug("Привязка: страница {Page}, отрезков {Count}", key.Page + 1, index.Count);
            if (showStatus && IsMeasuring && !HasMeasureInProgress)
                StatusText = Loc.F("MeasureSnapReady", index.Count);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать геометрию страницы {Page} для привязки", key.Page + 1);
            // Пустой индекс: привязки не будет, но измерять можно.
            _snapIndexes[key] = new VectorSnapIndex(Array.Empty<PdfSegment>(), 1, 1);
        }
        finally
        {
            _snapLoading.Remove(key);
        }
    }

    /// <summary>
    /// Список страниц пересобран (например, после фиксации измерения): ссылки
    /// на карточки устарели, и начатое измерение переезжает на новые.
    /// </summary>
    private void RemapMeasurementAfterRebuild()
    {
        if (_measurePage is { } stale)
        {
            _measurePage = Pages.FirstOrDefault(p => p.LogicalIndex == stale.LogicalIndex);
            _measureHoverPage = _measurePage;
            if (_measurePage != null && _measurePoints.Count > 0)
                UpdateMeasurePreview(_measurePage, _measureHoverPt, SnapKind.None);
            else if (_measurePage == null)
                ResetMeasurementState();
        }
        else if (_measureHoverPage is { } hoverStale)
        {
            _measureHoverPage = Pages.FirstOrDefault(p => p.LogicalIndex == hoverStale.LogicalIndex);
        }
    }
}
