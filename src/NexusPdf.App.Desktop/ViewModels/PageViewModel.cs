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
    private const int ThumbPixelWidth = 150;

    private readonly DocumentViewModel _owner;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _thumbCts;
    private int _renderedPixelWidth;
    private int _renderedFormVersion = -1;
    private int _renderedOverlays = -1;
    private int _thumbOverlays = -1;
    private string? _lastStoredKey;
    private int _lastStoredFormVersion = -1;

    public PageViewModel(DocumentViewModel owner, int logicalIndex, PageRef pageRef, PdfPageDescriptor sizePt)
    {
        _owner = owner;
        LogicalIndex = logicalIndex;
        PageRef = pageRef;
        SizePt = sizePt;
    }

    public int LogicalIndex { get; }
    public PageRef PageRef { get; }
    public PdfPageDescriptor SizePt { get; }
    public int PageNumber => LogicalIndex + 1;
    public int RotationDegrees => PageRef.RotationOffset * 90;

    public double WidthDiu => SizePt.WidthPoints * PtToDiu * _owner.Zoom;
    public double HeightDiu => SizePt.HeightPoints * PtToDiu * _owner.Zoom;

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

    // ----- Живая рамка при drag-размещении аннотации (в пунктах страницы) -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DragRectXPt))]
    [NotifyPropertyChangedFor(nameof(DragRectYPt))]
    [NotifyPropertyChangedFor(nameof(DragRectWPt))]
    [NotifyPropertyChangedFor(nameof(DragRectHPt))]
    [NotifyPropertyChangedFor(nameof(HasDragRect))]
    private Rect? _dragPreviewRect;

    public bool HasDragRect => DragPreviewRect.HasValue;
    public double DragRectXPt => DragPreviewRect?.X ?? 0;
    public double DragRectYPt => DragPreviewRect?.Y ?? 0;
    public double DragRectWPt => DragPreviewRect?.Width ?? 0;
    public double DragRectHPt => DragPreviewRect?.Height ?? 0;

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
    }

    /// <summary>Запрос полноразмерного растра под текущую ширину в устройственных пикселях.</summary>
    public async void EnsureImage(double dpiScale)
    {
        var pixelWidth = Math.Max(16, (int)Math.Round(WidthDiu * dpiScale));
        var pixelHeight = Math.Max(16, (int)Math.Round(HeightDiu * dpiScale));
        var formVersion = _owner.FormRenderVersion;
        // Отпечаток правок входит и в условие пропуска, и в ключ кэша: без него
        // после добавления правки вернулась бы прежняя картинка без неё.
        var overlays = _owner.Document.GetOverlaySignature(LogicalIndex);
        if (_renderedPixelWidth == pixelWidth && _renderedFormVersion == formVersion
            && _renderedOverlays == overlays && Image != null)
            return;

        var key = RenderCache.MakeKey(PageRef.SourceId, PageRef.SourcePageIndex, PageRef.RotationOffset, pixelWidth)
                  + ":f" + formVersion + ":o" + overlays;
        if (_owner.Cache.TryGet(key) is { } cached)
        {
            Image = cached;
            _renderedPixelWidth = pixelWidth;
            _renderedFormVersion = formVersion;
            return;
        }

        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        IsRendering = true;
        try
        {
            var raw = await _owner.Document.RenderLogicalPageAsync(LogicalIndex, pixelWidth, pixelHeight, cts.Token);
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
                IsRendering = false;
        }
    }

    public async void EnsureThumbnail()
    {
        // Правки должны быть видны и на миниатюре: иначе список страниц
        // показывает документ, которого уже нет.
        var overlays = _owner.Document.GetOverlaySignature(LogicalIndex);
        if (_thumbOverlays != overlays)
        {
            ThumbImage = null;
            _thumbOverlays = overlays;
        }
        if (ThumbImage != null || _thumbCts is { IsCancellationRequested: false })
            return;

        var scale = ThumbPixelWidth / Math.Max(1.0, SizePt.WidthPoints);
        var pixelHeight = Math.Max(16, (int)(SizePt.HeightPoints * scale));

        var key = RenderCache.MakeKey(PageRef.SourceId, PageRef.SourcePageIndex, PageRef.RotationOffset, ThumbPixelWidth)
                  + ":f" + _owner.FormRenderVersion + ":o" + overlays;
        if (_owner.Cache.TryGet(key) is { } cached)
        {
            ThumbImage = cached;
            return;
        }

        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        try
        {
            var raw = await _owner.Document.RenderLogicalPageAsync(LogicalIndex, ThumbPixelWidth, pixelHeight, cts.Token);
            var bitmap = BitmapFactory.ToBitmapSource(raw);
            _owner.Cache.Store(key, bitmap);
            ThumbImage = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось создать миниатюру страницы {Page}", PageNumber);
        }
        finally
        {
            _thumbCts = null;
        }
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
        _renderCts?.Cancel();
        Image = null;
        _renderedPixelWidth = 0;
    }

    public void CancelAll()
    {
        _renderCts?.Cancel();
        _thumbCts?.Cancel();
    }
}
