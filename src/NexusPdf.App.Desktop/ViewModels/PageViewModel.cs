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

    public PageViewModel(DocumentViewModel owner, int logicalIndex, PageRef pageRef, PdfPageDescriptor sizePt)
    {
        _owner = owner;
        LogicalIndex = logicalIndex;
        PageRef = pageRef;
        SizePt = sizePt;
        BuildOverlayPreviews();
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

    public IReadOnlyList<OverlayPreview> OverlayPreviews { get; private set; } = Array.Empty<OverlayPreview>();

    private void BuildOverlayPreviews()
    {
        // Черновики пересчитываются в текущую отображаемую рамку тем же кодом,
        // что и при запекании: экран обязан совпадать с будущим результатом.
        OverlayPreviews = PageRef.OverlayList
            .Select(raw =>
            {
                var (remapped, extraAngle) = OverlayDisplayMapper.ToFrame(
                    raw, PageRef.RotationOffset, SizePt.WidthPoints, SizePt.HeightPoints);
                return OverlayPreview.From(remapped, extraAngle);
            })
            .Where(p => p != null)
            .Cast<OverlayPreview>()
            .ToList();
    }

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
        if (_renderedPixelWidth == pixelWidth && Image != null)
            return;

        var key = RenderCache.MakeKey(PageRef.SourceId, PageRef.SourcePageIndex, PageRef.RotationOffset, pixelWidth);
        if (_owner.Cache.TryGet(key) is { } cached)
        {
            Image = cached;
            _renderedPixelWidth = pixelWidth;
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
            _owner.Cache.Store(key, bitmap);
            if (!cts.IsCancellationRequested)
            {
                Image = bitmap;
                _renderedPixelWidth = pixelWidth;
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
        if (ThumbImage != null || _thumbCts is { IsCancellationRequested: false })
            return;

        var scale = ThumbPixelWidth / Math.Max(1.0, SizePt.WidthPoints);
        var pixelHeight = Math.Max(16, (int)(SizePt.HeightPoints * scale));

        var key = RenderCache.MakeKey(PageRef.SourceId, PageRef.SourcePageIndex, PageRef.RotationOffset, ThumbPixelWidth);
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
