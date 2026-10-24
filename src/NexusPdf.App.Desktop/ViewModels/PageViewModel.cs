using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Логическая страница в интерфейсе: размеры при текущем масштабе, лениво
/// отрисовываемое изображение, миниатюра и подсветка найденного текста.
/// </summary>
public sealed partial class PageViewModel : ObservableObject
{
    private const double PtToDiu = 96.0 / 72.0;

    private readonly DocumentViewModel _owner;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _thumbCts;
    private int _renderedPixelWidth;
    private int _renderedFormVersion = -1;
    private int _renderedOverlays = -1;
    private int _thumbOverlays = -1;
    private string? _lastStoredKey;
    private int _lastStoredFormVersion = -1;
    /// <summary>Ключ растра, который рисуется прямо сейчас.</summary>
    private string? _pendingKey;

    public PageViewModel(DocumentViewModel owner, int logicalIndex, PageRef pageRef, PdfPageDescriptor sizePt)
    {
        _owner = owner;
        LogicalIndex = logicalIndex;
        PageRef = pageRef;
        SizePt = sizePt;
    }

    public int LogicalIndex { get; private set; }

    internal void SetLogicalIndex(int index)
    {
        if (LogicalIndex == index) return;
        CancelAll();
        _pendingKey = null;
        LogicalIndex = index;
        OnPropertyChanged(nameof(LogicalIndex));
        OnPropertyChanged(nameof(PageNumber));
        OnPropertyChanged(nameof(AccessibleName));
    }
    /// <summary>Документ, которому принадлежит страница: нужен переносу между документами.</summary>
    public DocumentViewModel Owner => _owner;

    public PageRef PageRef { get; }
    public PdfPageDescriptor SizePt { get; }
    public int PageNumber => LogicalIndex + 1;
    public int RotationDegrees => PageRef.RotationOffset * 90;

    /// <summary>
    /// Что произносит экранный диктор о карточке страницы. Без него WPF читает
    /// имя ТИПА — «NexusPdf.App.Desktop.ViewModels.PageViewModel»: номер лежит
    /// в шаблоне, а не в тексте элемента списка.
    /// </summary>
    public string AccessibleName => Localization.Loc.F("A11yPageItem", PageNumber);

    /// <summary>
    /// Размер карточки в DIU, выровненный по пикселям экрана: карточка и растр
    /// считаются от одного и того же целого числа пикселей, поэтому растр
    /// ложится 1:1 и не растягивается на доли пикселя.
    /// </summary>
    public double WidthDiu => NexusPdf.Ux.ZoomFitting.SnapToPixels(
        SizePt.WidthPoints * PtToDiu * _owner.Zoom, _owner.DpiScale);
    public double HeightDiu => NexusPdf.Ux.ZoomFitting.SnapToPixels(
        SizePt.HeightPoints * PtToDiu * _owner.Zoom, _owner.DpiScale);

    /// <summary>Масштаб пункты → DIU при текущем зуме (для слоя предпросмотра оверлеев).</summary>
    public double DisplayScale => PtToDiu * _owner.Zoom;

    /// <summary>
    /// Слой приближений WPF больше не используется: применённые правки рисует
    /// сам движок вместе со страницей, поэтому экран показывает ровно то, что
    /// окажется в файле. Свойство оставлено пустым, чтобы разметка не ломалась,
    /// а живые жесты рисуются отдельными слоями (рамка и штрих).
    /// </summary>
    public IReadOnlyList<OverlayPreview> OverlayPreviews { get; } = Array.Empty<OverlayPreview>();

    public string SizeText => Localization.Loc.F("PageSize",
        Math.Round(SizePt.WidthPoints / 72.0 * 25.4), Math.Round(SizePt.HeightPoints / 72.0 * 25.4));

    [ObservableProperty]
    private BitmapSource? _image;

    [ObservableProperty]
    private BitmapSource? _thumbImage;

    [ObservableProperty]
    private bool _isRendering;

    /// <summary>
    /// Фильтр, которым растр ложится на карточку. Когда растр совпадает с ней
    /// пиксель в пиксель, он копируется без фильтра — ни одна точка не
    /// размазывается. Пока после смены масштаба показывается прежний растр,
    /// растянутый на новый размер, работает качественный фильтр.
    /// </summary>
    [ObservableProperty]
    private System.Windows.Media.BitmapScalingMode _scalingMode =
        System.Windows.Media.BitmapScalingMode.HighQuality;

    /// <summary>Прямоугольники подсветки в DIU при текущем масштабе.</summary>
    [ObservableProperty]
    private IReadOnlyList<Rect> _highlights = Array.Empty<Rect>();

    /// <summary>Прямоугольники ВЫДЕЛЕННОГО мышью текста (отдельно от подсветки поиска).</summary>
    [ObservableProperty]
    private IReadOnlyList<Rect> _selectionRects = Array.Empty<Rect>();

    /// <summary>
    /// Ссылки страницы в DIU: читаются из документа один раз, дальше попадание
    /// курсора проверяется без обращения к движку (иначе нативный вызов на
    /// каждое движение мыши).
    /// </summary>
    public IReadOnlyList<(Rect Area, NexusPdf.Pdf.Abstractions.PdfPageLink Link)> LinkAreas { get; set; } =
        Array.Empty<(Rect, NexusPdf.Pdf.Abstractions.PdfPageLink)>();

    public bool LinksLoaded { get; set; }

    /// <summary>
    /// Строки текста страницы в единицах показа. Нужны курсору: над текстом он
    /// обязан быть текстовым, а над пустым полем и картинкой — обычной стрелкой.
    /// Считаются один раз на страницу и переживают перерисовку.
    /// </summary>
    public IReadOnlyList<Rect> TextAreas { get; set; } = Array.Empty<Rect>();

    public bool TextAreasLoaded { get; set; }

    /// <summary>
    /// Поля формы страницы в пунктах: по ним щелчок в обычном просмотре сразу
    /// попадает в поле, а курсор над полем показывает, что туда можно писать.
    /// </summary>
    public IReadOnlyList<NexusPdf.Pdf.Abstractions.PdfFormFieldArea> FormAreas { get; set; } =
        Array.Empty<NexusPdf.Pdf.Abstractions.PdfFormFieldArea>();

    public bool FormAreasLoaded { get; set; }
    public bool FormAreasLoading { get; set; }

    /// <summary>
    /// Выбрана ли карточка в организаторе. Живёт в модели, а не в списке:
    /// любая правка пересобирает страницы, и выбор, хранившийся только в
    /// списке, слетал после каждого поворота.
    /// </summary>
    [ObservableProperty]
    private bool _isOrganizeSelected;

    partial void OnIsOrganizeSelectedChanged(bool value) => _owner.OnOrganizeSelectionChanged();

    /// <summary>
    /// Лист миниатюры той же формы, что и страница, вписанный в ячейку
    /// организатора. Раньше любой лист рисовался портретной карточкой, и
    /// альбомная страница выглядела фотографией на белом листе, а поворот на
    /// четверть было не отличить от исходного.
    /// </summary>
    public double OrganizeCardWidth => FitCard().Width;
    public double OrganizeCardHeight => FitCard().Height;

    /// <summary>Миниатюра в боковой панели: высота известна до загрузки, и список не прыгает.</summary>
    public double SideThumbHeight =>
        Math.Round(SideThumbWidth * SizePt.HeightPoints / Math.Max(1, SizePt.WidthPoints)) + 2; // + рамка

    public const double SideThumbWidth = 138;

    private (double Width, double Height) FitCard()
    {
        var boxWidth = _owner.OrganizeThumbWidth - 2;
        var boxHeight = _owner.OrganizeThumbHeight - 2;
        var scale = Math.Min(boxWidth / Math.Max(1, SizePt.WidthPoints), boxHeight / Math.Max(1, SizePt.HeightPoints));
        return (Math.Max(8, Math.Round(SizePt.WidthPoints * scale)), Math.Max(8, Math.Round(SizePt.HeightPoints * scale)));
    }

    public void NotifyThumbSizeChanged()
    {
        OnPropertyChanged(nameof(OrganizeCardWidth));
        OnPropertyChanged(nameof(OrganizeCardHeight));
    }

    // ----- Живая рамка при drag-размещении аннотации (в пунктах страницы) -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragRectXPt))]
    [NotifyPropertyChangedFor(nameof(DragRectYPt))]
    [NotifyPropertyChangedFor(nameof(DragRectWPt))]
    [NotifyPropertyChangedFor(nameof(DragRectHPt))]
    [NotifyPropertyChangedFor(nameof(HasDragRect))]
    private Rect? _dragPreviewRect;

    /// <summary>Поворот живой рамки (градусы по часовой) — у повёрнутого текстового блока.</summary>
    [ObservableProperty]
    private double _dragPreviewAngle;

    public bool HasDragRect => DragPreviewRect.HasValue;
    public double DragRectXPt => DragPreviewRect?.X ?? 0;
    public double DragRectYPt => DragPreviewRect?.Y ?? 0;
    public double DragRectWPt => DragPreviewRect?.Width ?? 0;
    public double DragRectHPt => DragPreviewRect?.Height ?? 0;

    // ----- Цель инструмента «Текст» под курсором (пункты страницы) -----

    /// <summary>Блок или абзац — рамкой, клетка или строка бланка — светлой заливкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverIsArea))]
    [NotifyPropertyChangedFor(nameof(HoverIsBlock))]
    [NotifyPropertyChangedFor(nameof(HoverIsParagraph))]
    private TextHoverKind _hoverTargetKind;

    [ObservableProperty]
    private Rect _hoverTargetRect;

    [ObservableProperty]
    private double _hoverTargetAngle;

    public bool HoverIsArea => HoverTargetKind is TextHoverKind.Cell or TextHoverKind.Line;
    public bool HoverIsBlock => HoverTargetKind == TextHoverKind.Block;
    public bool HoverIsParagraph => HoverTargetKind == TextHoverKind.Paragraph;

    public void SetTextHover(TextHoverKind kind, NexusPdf.Pdf.Abstractions.OverlayBox? rect, double angle)
    {
        if (rect is { } box)
        {
            HoverTargetRect = new Rect(box.XPt, box.YPt, Math.Max(0, box.WidthPt), Math.Max(0, box.HeightPt));
            HoverTargetAngle = angle;
        }
        HoverTargetKind = rect == null ? TextHoverKind.None : kind;
    }

    // ----- Рамка выделенного объекта (в пунктах страницы) -----

    /// <summary>
    /// Рамка выбранного наложенного объекта. Рисуется поверх страницы вместе с
    /// ручками: без видимой рамки «выделение» существует только в голове
    /// программы.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasObjectFrame))]
    [NotifyPropertyChangedFor(nameof(ObjectFrameXPt))]
    [NotifyPropertyChangedFor(nameof(ObjectFrameYPt))]
    [NotifyPropertyChangedFor(nameof(ObjectFrameWPt))]
    [NotifyPropertyChangedFor(nameof(ObjectFrameHPt))]
    private Rect? _objectFrame;

    public bool HasObjectFrame => ObjectFrame.HasValue;
    public double ObjectFrameXPt => ObjectFrame?.X ?? 0;
    public double ObjectFrameYPt => ObjectFrame?.Y ?? 0;
    public double ObjectFrameWPt => ObjectFrame?.Width ?? 0;
    public double ObjectFrameHPt => ObjectFrame?.Height ?? 0;

    /// <summary>Поворот рамки выбранного объекта вокруг её центра (градусы по часовой).</summary>
    [ObservableProperty]
    private double _objectFrameAngle;

    /// <summary>Ручки рамки в пунктах страницы; пусто — объект не растягивается.</summary>
    [ObservableProperty]
    private IReadOnlyList<Rect> _objectHandles = Array.Empty<Rect>();

    // ----- Живой штрих во время рисования (в пунктах страницы) -----

    /// <summary>
    /// Точки штриха, который пользователь ведёт прямо сейчас. Показывается
    /// уже СГЛАЖЕННЫМ: пользователь должен видеть ту линию, которая ляжет в
    /// документ, а не сырую дрожащую.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrawPreview))]
    private System.Windows.Media.PointCollection? _drawPreview;

    public bool HasDrawPreview => DrawPreview is { Count: > 1 };

    // ----- Измерение: ведущаяся ломаная, точка привязки и живая величина -----

    /// <summary>Поставленные точки плюс точка под курсором, в пунктах страницы.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeasurePreview))]
    private System.Windows.Media.PointCollection? _measureGuide;

    /// <summary>Замыкающий отрезок площади: от курсора к первой точке.</summary>
    [ObservableProperty]
    private System.Windows.Media.PointCollection? _measureClosing;

    /// <summary>Квадратики в уже поставленных точках (пункты страницы).</summary>
    [ObservableProperty]
    private IReadOnlyList<Rect> _measureVertices = Array.Empty<Rect>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeasureSnap))]
    [NotifyPropertyChangedFor(nameof(SnapIsBox))]
    [NotifyPropertyChangedFor(nameof(SnapIsRound))]
    [NotifyPropertyChangedFor(nameof(SnapIsDiamond))]
    private NexusPdf.Pdf.Abstractions.SnapKind _measureSnapKind;

    /// <summary>Рамка маркера привязки в пунктах страницы.</summary>
    [ObservableProperty]
    private Rect _measureSnapRect;

    public bool HasMeasureSnap => MeasureSnapKind != NexusPdf.Pdf.Abstractions.SnapKind.None;
    public bool SnapIsBox => MeasureSnapKind is NexusPdf.Pdf.Abstractions.SnapKind.Endpoint
        or NexusPdf.Pdf.Abstractions.SnapKind.OwnVertex;
    public bool SnapIsRound => MeasureSnapKind is NexusPdf.Pdf.Abstractions.SnapKind.OnSegment
        or NexusPdf.Pdf.Abstractions.SnapKind.Midpoint;
    public bool SnapIsDiamond => MeasureSnapKind == NexusPdf.Pdf.Abstractions.SnapKind.Intersection;

    /// <summary>Текст живой величины у курсора («L = 33,12 м …»).</summary>
    [ObservableProperty]
    private string _measureReadoutText = "";

    /// <summary>Якорь подписи величины в пунктах страницы; на экран переводится при текущем масштабе.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureReadoutX))]
    [NotifyPropertyChangedFor(nameof(MeasureReadoutY))]
    private Point _measureReadoutAnchorPt;

    public double MeasureReadoutX => MeasureReadoutAnchorPt.X * DisplayScale + 14;
    public double MeasureReadoutY => MeasureReadoutAnchorPt.Y * DisplayScale + 14;

    public bool HasMeasurePreview => MeasureGuide is { Count: > 0 };

    [ObservableProperty]
    private System.Windows.Media.Brush _measureBrush = System.Windows.Media.Brushes.Red;

    /// <summary>Толщина линии предпросмотра в пунктах — на экране всегда ~1,25 px.</summary>
    public double HairlinePt => 1.25 / Math.Max(DisplayScale, 0.01);

    /// <summary>Сторона маркера привязки в пунктах — на экране всегда ~9 px.</summary>
    public double MarkerPt => 9 / Math.Max(DisplayScale, 0.01);

    public void ClearMeasurePreview()
    {
        MeasureGuide = null;
        MeasureClosing = null;
        MeasureVertices = Array.Empty<Rect>();
        MeasureSnapKind = NexusPdf.Pdf.Abstractions.SnapKind.None;
        MeasureReadoutText = "";
    }

    /// <summary>Толщина линии предпросмотра в пунктах (масштаб накладывается канвой).</summary>
    [ObservableProperty]
    private double _drawPreviewWidth = 2;

    [ObservableProperty]
    private System.Windows.Media.Brush _drawPreviewBrush =
        System.Windows.Media.Brushes.Black;

    public void NotifyZoomChanged()
    {
        OnPropertyChanged(nameof(WidthDiu));
        OnPropertyChanged(nameof(HeightDiu));
        OnPropertyChanged(nameof(DisplayScale));
        OnPropertyChanged(nameof(HairlinePt));
        OnPropertyChanged(nameof(MarkerPt));
        OnPropertyChanged(nameof(MeasureReadoutX));
        OnPropertyChanged(nameof(MeasureReadoutY));
        // Старый растр до перерисовки растягивается на новый размер — без
        // фильтра он выглядел бы зубчатым.
        if (_renderedPixelWidth != TargetPixelWidth())
            ScalingMode = System.Windows.Media.BitmapScalingMode.HighQuality;
    }

    private int TargetPixelWidth() => Math.Max(16, (int)Math.Round(WidthDiu * _owner.DpiScale));

    /// <summary>Карточка сейчас в визуальном дереве — на экране или рядом с ним.</summary>
    private bool _shown;
    private int _attachedViewCount;

    /// <summary>Карточка появилась на экране — нужен растр под её размер.</summary>
    public void Attach(double dpiScale)
    {
        _attachedViewCount++;
        _shown = true;
        EnsureImage(dpiScale);
    }

    /// <summary>Карточка ушла с экрана — растр отпускается (хранить ли его, решает кэш).</summary>
    public void Detach()
    {
        if (_attachedViewCount == 0 || --_attachedViewCount > 0) return;
        _shown = false;
        ReleaseImage();
    }

    /// <summary>
    /// Масштаб или плотность экрана сменились. Перерисовываются только
    /// карточки, которые на экране: раньше после подгонки по ширине страница
    /// так и оставалась со старым растром, растянутым на новый размер, — это и
    /// было «размытие PDF» на ноутбуках.
    /// </summary>
    public void RefreshIfShown()
    {
        if (_shown) EnsureImage(_owner.DpiScale);
    }

    /// <summary>
    /// Запрос полноразмерного растра под текущую ширину в устройственных
    /// пикселях.
    ///
    /// Заодно готовится миниатюра: пока страница рисуется, она показывается
    /// растянутой вместо пустого листа. Полноразмерный растр большой страницы
    /// считается сотни миллисекунд, и всё это время человек смотрел на белое
    /// поле с надписью «Отрисовка» — теперь он видит саму страницу, пусть
    /// сначала и нерезко.
    /// </summary>
    public async void EnsureImage(double dpiScale)
    {
        if (!IsCurrentPage()) return;
        EnsureThumbnail();

        var pixelWidth = Math.Max(16, (int)Math.Round(WidthDiu * dpiScale));
        var pixelHeight = Math.Max(16, (int)Math.Round(HeightDiu * dpiScale));
        var formVersion = _owner.FormRenderVersion;
        // Отпечаток правок входит и в условие пропуска, и в ключ кэша: без него
        // после добавления правки вернулась бы прежняя картинка без неё.
        var overlays = _owner.Document.GetOverlaySignature(LogicalIndex);
        if (_renderedPixelWidth == pixelWidth && _renderedFormVersion == formVersion
            && _renderedOverlays == overlays && Image != null)
        {
            CancelRender();
            ScalingMode = System.Windows.Media.BitmapScalingMode.NearestNeighbor;
            return;
        }

        var key = RenderCache.MakeKey(PageRef.SourceId, PageRef.SourcePageIndex, PageRef.RotationOffset, pixelWidth)
                  + ":f" + formVersion + ":o" + overlays;
        // Ровно этот растр уже рисуется — повторный запрос только отменил бы
        // почти готовую работу и начал её заново.
        if (_renderCts is { IsCancellationRequested: false } && IsRendering && _pendingKey == key)
            return;
        if (_owner.Cache.TryGet(key) is { } cached)
        {
            CancelRender();
            Image = cached;
            _renderedPixelWidth = pixelWidth;
            _renderedFormVersion = formVersion;
            _renderedOverlays = overlays;
            ScalingMode = System.Windows.Media.BitmapScalingMode.NearestNeighbor;
            IsRendering = false;
            return;
        }

        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _pendingKey = key;
        IsRendering = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var raw = await _owner.Document.RenderLogicalPageAsync(LogicalIndex, pixelWidth, pixelHeight, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrentPage()) return;
            // В журнал попадают только МЕДЛЕННЫЕ страницы. Обычный рендер —
            // это десятки миллисекунд; всё, что заметно дольше, означает либо
            // тяжёлую страницу, либо занятый поток интерфейса, и разбираться в
            // этом лучше по журналу, чем по ощущениям.
            var elapsed = started.Elapsed.TotalMilliseconds;
            if (elapsed > 200)
                Serilog.Log.Debug("Медленный рендер стр. {Page} ({Width} px): {Ms:N0} мс",
                    PageNumber, pixelWidth, elapsed);
            var bitmap = BitmapFactory.ToBitmapSource(raw);
            // Растры устаревших форм-версий больше никогда не запросятся —
            // не даём им хоронить бюджет LRU при наборе текста в поле.
            if (_lastStoredKey != null && _lastStoredFormVersion != formVersion)
                _owner.Cache.Remove(_lastStoredKey);
            _owner.Cache.Store(key, bitmap);
            _lastStoredKey = key;
            _lastStoredFormVersion = formVersion;
            if (!cts.IsCancellationRequested)
            {
                Image = bitmap;
                _renderedPixelWidth = pixelWidth;
                _renderedFormVersion = formVersion;
                _renderedOverlays = overlays;
                // Растр совпадает с карточкой пиксель в пиксель — если за время
                // отрисовки масштаб не ушёл дальше.
                ScalingMode = pixelWidth == TargetPixelWidth()
                    ? System.Windows.Media.BitmapScalingMode.NearestNeighbor
                    : System.Windows.Media.BitmapScalingMode.HighQuality;
            }
        }
        catch (OperationCanceledException)
        {
            // Страница ушла из зоны видимости или масштаб сменился раньше.
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось отрисовать страницу {Page}", PageNumber);
        }
        finally
        {
            if (_renderCts == cts)
            {
                _renderCts = null;
                IsRendering = false;
                _pendingKey = null;
            }
            cts.Dispose();
        }
    }

    private bool IsCurrentPage() =>
        (uint)LogicalIndex < (uint)_owner.Document.Session.Model.Pages.Count &&
        _owner.Document.Session.Model.Pages[LogicalIndex] == PageRef;

    private void CancelRender()
    {
        _renderCts?.Cancel();
        _pendingKey = null;
        IsRendering = false;
    }

    /// <summary>Ширина растра, в которой миниатюра нарисована (или рисуется).</summary>
    private int _thumbRenderedWidth;
    private int _thumbFormVersion = -1;
    private string? _thumbPendingKey;

    public async void EnsureThumbnail()
    {
        if (!IsCurrentPage()) return;
        // Правки должны быть видны и на миниатюре: иначе список страниц
        // показывает документ, которого уже нет.
        var overlays = _owner.Document.GetOverlaySignature(LogicalIndex);
        var formVersion = _owner.FormRenderVersion;
        if (_thumbOverlays != overlays || _thumbFormVersion != formVersion)
        {
            ThumbImage = null;
            _thumbOverlays = overlays;
        }
        // Размер карточки в организаторе меняется ползунком: миниатюра,
        // нарисованная под маленькую карточку, на большой расплывается —
        // такую перерисовываем, а не оставляем «уже есть».
        var wanted = _owner.ThumbPixelWidth;
        if (ThumbImage != null && _thumbRenderedWidth == wanted)
            return;

        var scale = wanted / Math.Max(1.0, SizePt.WidthPoints);
        var pixelHeight = Math.Max(16, (int)(SizePt.HeightPoints * scale));

        var key = RenderCache.MakeKey(PageRef.SourceId, PageRef.SourcePageIndex, PageRef.RotationOffset, wanted)
                  + ":f" + formVersion + ":o" + overlays;
        if (_thumbCts is { IsCancellationRequested: false } && _thumbPendingKey == key)
            return;
        if (_owner.Cache.TryGet(key) is { } cached)
        {
            _thumbCts?.Cancel();
            _thumbCts = null;
            ThumbImage = cached;
            _thumbRenderedWidth = wanted;
            _thumbFormVersion = formVersion;
            return;
        }

        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        _thumbPendingKey = key;
        try
        {
            var raw = await _owner.Document.RenderLogicalPageAsync(LogicalIndex, wanted, pixelHeight, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrentPage()) return;
            var bitmap = BitmapFactory.ToBitmapSource(raw);
            _owner.Cache.Store(key, bitmap);
            if (!cts.IsCancellationRequested)
            {
                ThumbImage = bitmap;
                _thumbRenderedWidth = wanted;
                _thumbFormVersion = formVersion;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось создать миниатюру страницы {Page}", PageNumber);
        }
        finally
        {
            if (_thumbCts == cts)
            {
                _thumbCts = null;
                _thumbPendingKey = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>Миниатюра есть, но под другой размер карточки — перерисовать; не было — не трогать (её закажет показ).</summary>
    public void RefreshThumbnailIfStale()
    {
        if (ThumbImage != null && (_thumbRenderedWidth != _owner.ThumbPixelWidth
                                  || _thumbFormVersion != _owner.FormRenderVersion))
            EnsureThumbnail();
    }

    /// <summary>Принудительный ре-рендер (ввод в поле формы): кэш-ключ уже сменился версией формы.</summary>
    public void ForceRefresh(double dpiScale)
    {
        _renderedPixelWidth = 0;
        EnsureImage(dpiScale);
    }

    /// <summary>Миниатюра тоже должна показать новое значение поля.</summary>
    public void ForceRefreshThumbnail()
    {
        _thumbCts?.Cancel();
        _thumbCts = null;
        ThumbImage = null;
        EnsureThumbnail();
    }

    /// <summary>Страница ушла из видимой области — полноразмерный растр отпускаем (кэш решает, хранить ли его).</summary>
    public void ReleaseImage()
    {
        CancelRender();
        Image = null;
        _renderedPixelWidth = 0;
        ScalingMode = System.Windows.Media.BitmapScalingMode.HighQuality;
    }

    public void CancelAll()
    {
        CancelRender();
        _thumbCts?.Cancel();
    }
}
