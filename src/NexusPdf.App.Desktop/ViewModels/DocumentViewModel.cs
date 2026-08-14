using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Application;
using NexusPdf.Domain;

namespace NexusPdf.App.Desktop.ViewModels;

public sealed partial class DocumentViewModel : ObservableObject, IDropTarget
{
    private const double PtToDiu = 96.0 / 72.0;
    private const double PageMarginDiu = 24;

    private readonly SearchService _search = new();
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private CancellationTokenSource? _searchCts;

    public DocumentViewModel(OpenedDocument document, RenderCache cache)
    {
        Document = document;
        Cache = cache;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        // Session.Changed может прийти с фонового потока (например, после
        // переоткрытия при сохранении) — перестройка привязанных коллекций
        // обязана выполняться на потоке диспетчера.
        Document.Session.Changed += (_, _) =>
        {
            if (_dispatcher.CheckAccess())
                OnSessionChanged();
            else
                _dispatcher.Invoke(OnSessionChanged);
        };
        RebuildPages();
    }

    public OpenedDocument Document { get; }
    public RenderCache Cache { get; }

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    public event EventHandler<int>? ScrollToPageRequested;

    public string Title => Document.DisplayName;
    public string? FilePath => Document.Session.FilePath;
    public bool IsDirty => Document.Session.IsDirty;
    public int PageCount => Pages.Count;
    public bool CanUndo => Document.Session.History.CanUndo;
    public bool CanRedo => Document.Session.History.CanRedo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
    private double _zoom = 1.0;

    public string ZoomPercentText => $"{Math.Round(Zoom * 100)}%";

    [ObservableProperty]
    private bool _isOrganizeMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageOfText))]
    [NotifyPropertyChangedFor(nameof(CurrentPageSizeText))]
    private int _currentPageNumber = 1;

    public string PageOfText => Loc.F("PageOf", CurrentPageNumber, PageCount);

    public string CurrentPageSizeText =>
        Pages.Count == 0 ? "" : Pages[Math.Clamp(CurrentPageNumber - 1, 0, Pages.Count - 1)].SizeText;

    /// <summary>Размер видимой области просмотра в DIU; обновляется представлением.</summary>
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    [RelayCommand]
    private void FitWidth() => FitWidth(ViewportWidth > 0 ? ViewportWidth : 900);

    [RelayCommand]
    private void FitPage() => FitPage(
        ViewportWidth > 0 ? ViewportWidth : 900,
        ViewportHeight > 0 ? ViewportHeight : 700);

    [ObservableProperty]
    private string _statusText = Loc.Get("Ready");

    [ObservableProperty]
    private bool _isBusy;

    // ----- Поиск -----

    [ObservableProperty]
    private bool _isFindVisible;

    [ObservableProperty]
    private string _findQuery = "";

    partial void OnFindQueryChanged(string value)
    {
        // Изменение запроса сбрасывает результаты: следующий Enter запустит новый поиск.
        Matches = Array.Empty<SearchMatch>();
        _currentMatch = -1;
        FindStatus = "";
        ClearHighlights();
    }

    [ObservableProperty]
    private string _findStatus = "";

    public IReadOnlyList<SearchMatch> Matches { get; private set; } = Array.Empty<SearchMatch>();
    private int _currentMatch = -1;
    private PageViewModel? _highlightedPage;

    // ----- Масштаб -----

    partial void OnZoomChanged(double value)
    {
        foreach (var page in Pages)
            page.NotifyZoomChanged();
        ClearHighlights();
    }

    public void SetZoom(double zoom) => Zoom = Math.Clamp(zoom, 0.25, 4.0);

    [RelayCommand]
    private void ZoomIn() => SetZoom(Zoom * 1.15);

    [RelayCommand]
    private void ZoomOut() => SetZoom(Zoom / 1.15);

    [RelayCommand]
    private void ZoomActual() => SetZoom(1.0);

    /// <summary>Подгон по ширине: вычисляется по самой широкой странице и ширине видимой области (DIU).</summary>
    public void FitWidth(double viewportWidthDiu)
    {
        var maxPageWidthPt = Pages.Count == 0 ? 612 : Pages.Max(p => p.SizePt.WidthPoints);
        SetZoom((viewportWidthDiu - PageMarginDiu * 2) / (maxPageWidthPt * PtToDiu));
    }

    public void FitPage(double viewportWidthDiu, double viewportHeightDiu)
    {
        if (Pages.Count == 0) return;
        var page = Pages[Math.Clamp(CurrentPageNumber - 1, 0, Pages.Count - 1)];
        var byWidth = (viewportWidthDiu - PageMarginDiu * 2) / (page.SizePt.WidthPoints * PtToDiu);
        var byHeight = (viewportHeightDiu - PageMarginDiu * 2) / (page.SizePt.HeightPoints * PtToDiu);
        SetZoom(Math.Min(byWidth, byHeight));
    }

    // ----- Позиция чтения -----

    /// <summary>Верхняя граница каждой страницы в DIU (для перехода к странице и определения текущей).</summary>
    public double GetPageTop(int index)
    {
        double top = 0;
        for (var i = 0; i < index && i < Pages.Count; i++)
            top += Pages[i].HeightDiu + PageMarginDiu;
        return top;
    }

    public void UpdateCurrentPage(double verticalOffsetDiu, double viewportHeightDiu)
    {
        double top = 0;
        var anchor = verticalOffsetDiu + viewportHeightDiu / 3;
        for (var i = 0; i < Pages.Count; i++)
        {
            var bottom = top + Pages[i].HeightDiu + PageMarginDiu;
            if (anchor < bottom)
            {
                CurrentPageNumber = i + 1;
                return;
            }
            top = bottom;
        }
        CurrentPageNumber = Pages.Count;
    }

    public void GoToPage(int pageNumber)
    {
        var index = Math.Clamp(pageNumber - 1, 0, Pages.Count - 1);
        ScrollToPageRequested?.Invoke(this, index);
    }

    // ----- Размещение нового контента кликом -----

    /// <summary>Фабрика оверлея: страница + точка клика (в пунктах от левого верхнего угла).</summary>
    public sealed record PendingPlacement(Func<PageViewModel, double, double, Pdf.Abstractions.PageOverlay> Factory);

    [ObservableProperty]
    private PendingPlacement? _pendingOverlay;

    public void BeginPlacement(Func<PageViewModel, double, double, Pdf.Abstractions.PageOverlay> factory)
    {
        PendingOverlay = new PendingPlacement(factory);
        StatusText = Loc.Get("PlaceHint");
    }

    public void CancelPlacement()
    {
        if (PendingOverlay == null) return;
        PendingOverlay = null;
        StatusText = Loc.Get("Ready");
    }

    public void PlacePendingOverlay(PageViewModel page, double xPt, double yPt)
    {
        if (PendingOverlay is not { } pending) return;
        var overlay = pending.Factory(page, xPt, yPt);
        PendingOverlay = null;
        StatusText = Loc.Get("Ready");
        Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex, overlay));
    }

    // ----- Операции систематизации -----

    private static int[] ToIndices(IList? selection) =>
        selection?.Cast<PageViewModel>().Select(p => p.LogicalIndex).OrderBy(i => i).ToArray()
        ?? Array.Empty<int>();

    [RelayCommand]
    private void RotateSelected(IList? selection) => Rotate(ToIndices(selection), 1);

    [RelayCommand]
    private void RotateSelectedLeft(IList? selection) => Rotate(ToIndices(selection), -1);

    [RelayCommand]
    private void RotateSelected180(IList? selection) => Rotate(ToIndices(selection), 2);

    [RelayCommand]
    private void RotateAll() => Rotate(Enumerable.Range(0, Pages.Count).ToArray(), 1);

    private void Rotate(int[] indices, int quarterTurns)
    {
        if (indices.Length == 0) return;
        Document.Session.Apply(new RotatePagesOperation(indices, quarterTurns));
    }

    [RelayCommand]
    private void DeleteSelected(IList? selection)
    {
        var indices = ToIndices(selection);
        if (indices.Length == 0 || indices.Length >= Pages.Count) return;
        Document.Session.Apply(new DeletePagesOperation(indices));
    }

    [RelayCommand]
    private void DuplicateSelected(IList? selection)
    {
        var indices = ToIndices(selection);
        if (indices.Length == 0) return;
        Document.Session.Apply(new DuplicatePagesOperation(indices));
    }

    [RelayCommand]
    private void Undo()
    {
        Document.Session.Undo();
    }

    [RelayCommand]
    private void Redo()
    {
        Document.Session.Redo();
    }

    // ----- Drag-and-drop перестановка (gong-wpf-dragdrop) -----

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        if (dropInfo.Data is PageViewModel || dropInfo.Data is IEnumerable<object>)
        {
            dropInfo.Effects = DragDropEffects.Move;
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        }
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        var items = dropInfo.Data switch
        {
            PageViewModel single => new[] { single },
            IEnumerable<object> many => many.OfType<PageViewModel>().ToArray(),
            _ => Array.Empty<PageViewModel>(),
        };
        if (items.Length == 0) return;

        var indices = items.Select(p => p.LogicalIndex).OrderBy(i => i).ToArray();
        var insertIndex = Math.Clamp(dropInfo.InsertIndex, 0, Pages.Count);
        Document.Session.Apply(new MovePagesOperation(indices, insertIndex));
    }

    // ----- Поиск -----

    [RelayCommand]
    private void ToggleFind()
    {
        IsFindVisible = !IsFindVisible;
        if (!IsFindVisible)
            ClearSearch();
    }

    [RelayCommand]
    private async Task FindNext()
    {
        if (Matches.Count == 0)
        {
            await RunSearchAsync();
            return;
        }
        _currentMatch = (_currentMatch + 1) % Matches.Count;
        await ShowCurrentMatchAsync();
    }

    [RelayCommand]
    private async Task FindPrevious()
    {
        if (Matches.Count == 0)
        {
            await RunSearchAsync();
            return;
        }
        _currentMatch = (_currentMatch - 1 + Matches.Count) % Matches.Count;
        await ShowCurrentMatchAsync();
    }

    public async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        ClearHighlights();
        _currentMatch = -1;
        Matches = Array.Empty<SearchMatch>();
        FindStatus = "";
        if (string.IsNullOrWhiteSpace(FindQuery))
            return;

        StatusText = Loc.Get("SearchingStatus");
        try
        {
            var result = await _search.SearchAsync(Document, FindQuery, caseSensitive: false, cts.Token);
            // Публикуем результат только если этот запуск всё ещё актуален:
            // поздняя континуация отменённого поиска не должна воскрешать
            // устаревшие совпадения по прежней раскладке страниц.
            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _searchCts))
                return;
            Matches = result;
            if (Matches.Count == 0)
            {
                FindStatus = Loc.Get("NoMatches");
            }
            else
            {
                _currentMatch = 0;
                await ShowCurrentMatchAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Поиск прерван ошибкой");
            FindStatus = Loc.Get("NoMatches");
        }
        finally
        {
            StatusText = Loc.Get("Ready");
        }
    }

    private async Task ShowCurrentMatchAsync()
    {
        if (_currentMatch < 0 || _currentMatch >= Matches.Count) return;
        var match = Matches[_currentMatch];
        if (match.LogicalPageIndex >= Pages.Count) return; // страницы изменились после поиска
        FindStatus = Loc.F("MatchesFound", _currentMatch + 1, Matches.Count);

        ClearHighlights();
        var page = Pages[match.LogicalPageIndex];
        try
        {
            var rects = await Document.Handles[page.PageRef.SourceId].GetTextRectsAsync(
                page.PageRef.SourcePageIndex, match.CharIndex, match.Length, CancellationToken.None);
            page.Highlights = rects
                .Select(r => TransformRectToDiu(r, page))
                .ToList();
            _highlightedPage = page;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось получить прямоугольники подсветки");
        }

        GoToPage(match.LogicalPageIndex + 1);
    }

    /// <summary>
    /// Перевод прямоугольника текста из координат исходной страницы PDF
    /// (начало — левый нижний угол) в DIU-координаты отображаемой страницы
    /// с учётом добавочного поворота.
    /// </summary>
    private Rect TransformRectToDiu(NexusPdf.Pdf.Abstractions.PdfTextRect r, PageViewModel page)
    {
        var source = Document.Handles[page.PageRef.SourceId].Info.Pages[page.PageRef.SourcePageIndex];
        var w = source.WidthPoints;
        var h = source.HeightPoints;

        // Точки в device-координатах без добавочного поворота (origin — левый верхний угол).
        var corners = new[]
        {
            (X: r.Left, Y: h - r.Top),
            (X: r.Right, Y: h - r.Bottom),
        };

        var q = page.PageRef.RotationOffset;
        var transformed = corners.Select(c => q switch
        {
            1 => (X: h - c.Y, Y: c.X),
            2 => (X: w - c.X, Y: h - c.Y),
            3 => (X: c.Y, Y: w - c.X),
            _ => c,
        }).ToArray();

        var scale = PtToDiu * Zoom;
        var x1 = Math.Min(transformed[0].X, transformed[1].X) * scale;
        var y1 = Math.Min(transformed[0].Y, transformed[1].Y) * scale;
        var x2 = Math.Max(transformed[0].X, transformed[1].X) * scale;
        var y2 = Math.Max(transformed[0].Y, transformed[1].Y) * scale;
        return new Rect(x1, y1, Math.Max(2, x2 - x1), Math.Max(2, y2 - y1));
    }

    private void ClearHighlights()
    {
        if (_highlightedPage != null)
        {
            _highlightedPage.Highlights = Array.Empty<Rect>();
            _highlightedPage = null;
        }
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        Matches = Array.Empty<SearchMatch>();
        _currentMatch = -1;
        FindStatus = "";
        ClearHighlights();
    }

    // ----- Перестройка после операций -----

    private void OnSessionChanged()
    {
        RebuildPages();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        ClearSearch();
    }

    private void RebuildPages()
    {
        foreach (var page in Pages)
            page.CancelAll();
        Pages.Clear();
        var model = Document.Session.Model;
        for (var i = 0; i < model.Pages.Count; i++)
            Pages.Add(new PageViewModel(this, i, model.Pages[i], Document.GetLogicalPageSize(i)));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageOfText));
        OnPropertyChanged(nameof(CurrentPageSizeText));
        if (CurrentPageNumber > Pages.Count)
            CurrentPageNumber = Pages.Count;
    }

    public async ValueTask DisposeAsync()
    {
        _searchCts?.Cancel();
        foreach (var page in Pages)
            page.CancelAll();
        foreach (var sourceId in Document.Handles.Keys)
            Cache.RemoveSource(sourceId);
        await Document.DisposeAsync();
    }
}
