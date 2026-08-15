using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using NexusPdf.Pdf.Abstractions;
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

    /// <summary>Правки форм идут напрямую в pdfium-документ мимо DocumentSession —
    /// без этого флага закрытие вкладки тихо теряло бы введённые значения.</summary>
    private bool _formModified;

    public bool IsDirty => Document.Session.IsDirty || _formModified;
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

    private CancellationTokenSource? _findDebounceCts;

    partial void OnFindQueryChanged(string value)
    {
        Matches = Array.Empty<SearchMatch>();
        _currentMatch = -1;
        FindStatus = "";
        ClearHighlights();

        // Поиск по мере ввода: ждать Enter, как раньше, — лишний шаг, которого
        // нет ни в одном современном просмотрщике. Пауза гасит поиск на каждую
        // букву при наборе.
        _findDebounceCts?.Cancel();
        if (string.IsNullOrWhiteSpace(value))
            return;
        var cts = new CancellationTokenSource();
        _findDebounceCts = cts;
        _ = RunSearchAfterPauseAsync(cts);
    }

    private async Task RunSearchAfterPauseAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(350, cts.Token);
            if (!ReferenceEquals(cts, _findDebounceCts))
                return;
            await RunSearchAsync();
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил печатать — этот запуск больше не нужен.
        }
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
    private bool _initialFitDone;

    /// <summary>
    /// Однократная подгонка по ширине при первом показе документа. Дальше
    /// масштаб принадлежит пользователю и сам не меняется.
    /// </summary>
    public void ApplyInitialFit(double viewportWidthDiu)
    {
        if (_initialFitDone || viewportWidthDiu < 50) return;
        _initialFitDone = true;
        FitWidth(viewportWidthDiu);
    }

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

    // ----- Размещение нового контента кликом или растягиванием рамки -----

    /// <summary>
    /// Ожидающее размещение: либо фабрика по точке клика, либо фабрика по
    /// прямоугольнику (drag). Координаты — в пунктах от левого верхнего угла.
    /// </summary>
    /// <summary>
    /// Любая фабрика может вернуть null: тогда операция не применяется, а
    /// обработку берёт на себя вызвавший код (например, правка области или
    /// картинки во внешнем редакторе — там результат появляется много позже
    /// жеста).
    /// </summary>
    public sealed record PendingPlacement(
        Func<PageViewModel, double, double, Pdf.Abstractions.PageOverlay?>? PointFactory,
        Func<PageViewModel, Rect, Pdf.Abstractions.PageOverlay?>? RectFactory);

    [ObservableProperty]
    private PendingPlacement? _pendingOverlay;

    public void BeginPlacement(Func<PageViewModel, double, double, Pdf.Abstractions.PageOverlay?> factory)
    {
        PendingOverlay = new PendingPlacement(factory, null);
        StatusText = Loc.Get("PlaceHint");
    }

    public void BeginRectPlacement(Func<PageViewModel, Rect, Pdf.Abstractions.PageOverlay?> factory)
    {
        PendingOverlay = new PendingPlacement(null, factory);
        StatusText = Loc.Get("PlaceRectHint");
    }

    public void PlacePendingRect(PageViewModel page, Rect rectPt)
    {
        if (PendingOverlay is not { RectFactory: { } factory }) return;
        if (IsBusy) return;
        if (rectPt.Width < 4 || rectPt.Height < 4) return; // случайный клик — не считается рамкой
        var overlay = factory(page, rectPt);
        PendingOverlay = null;
        StatusText = Loc.Get("Ready");
        if (overlay != null)
            Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex, overlay));
    }

    public void CancelPlacement()
    {
        if (PendingOverlay == null) return;
        PendingOverlay = null;
        StatusText = Loc.Get("Ready");
    }

    public void PlacePendingOverlay(PageViewModel page, double xPt, double yPt)
    {
        if (PendingOverlay is not { PointFactory: { } factory }) return;
        if (IsBusy) return; // идёт сохранение/печать: клик игнорируем, правка не должна молча потеряться
        var overlay = factory(page, xPt, yPt);
        PendingOverlay = null;
        StatusText = Loc.Get("Ready");
        if (overlay != null)
            Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex, overlay));
    }

    // ----- Цифровые подписи (статус при открытии) -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignatures))]
    [NotifyPropertyChangedFor(nameof(AllSignaturesValid))]
    [NotifyPropertyChangedFor(nameof(SignaturesValidButUntrusted))]
    private IReadOnlyList<NexusPdf.Signing.PdfSignatureInfo> _signatures =
        Array.Empty<NexusPdf.Signing.PdfSignatureInfo>();

    public bool HasSignatures => Signatures.Count > 0;

    // Зелёный статус — только полный порядок, ВКЛЮЧАЯ доверие к цепочке:
    // криптографически верная подпись самодельного сертификата с громким
    // именем не должна выглядеть доверенной (поведение как у Adobe).
    public bool AllSignaturesValid =>
        Signatures.Count > 0 &&
        Signatures.All(s => s.IsCryptoValid && s.CoversWholeDocument && s.IsTrusted);

    public bool SignaturesValidButUntrusted =>
        Signatures.Count > 0 &&
        Signatures.All(s => s.IsCryptoValid && s.CoversWholeDocument) &&
        Signatures.Any(s => !s.IsTrusted);

    /// <summary>Задача текущей инспекции: перед подписанием на неё нужно дождаться.</summary>
    public Task SignaturesLoaded { get; private set; } = Task.CompletedTask;

    public Task LoadSignaturesAsync() => SignaturesLoaded = LoadSignaturesCoreAsync();

    private async Task LoadSignaturesCoreAsync()
    {
        if (FilePath is not { } path) return;
        try
        {
            Signatures = await NexusPdf.Signing.PdfSignatureInspector.InspectAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось проверить подписи {Path}", path);
        }
    }

    // ----- Заполнение форм (AcroForm) -----

    [ObservableProperty]
    private bool _hasAcroForm;

    [ObservableProperty]
    private bool _isFormMode;

    /// <summary>Версия форм-рендера: входит в ключ кэша, каждый ввод обновляет растр страницы.</summary>
    public int FormRenderVersion { get; private set; }

    private PageViewModel? _formActivePage;

    public async Task DetectFormsAsync()
    {
        try
        {
            HasAcroForm = await Document.PrimaryHandle.GetFormTypeAsync(CancellationToken.None) == 1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось определить тип формы");
        }
    }

    public bool HasActiveFormPage => _formActivePage != null;

    [RelayCommand]
    private async Task ToggleFormMode()
    {
        if (!HasAcroForm || IsBusy) return;
        if (IsFormMode)
        {
            // Полное завершение окружения: значения зафиксированы, подсветка
            // полей уходит из рендеров (в т.ч. из печати).
            await Document.PrimaryHandle.FormEndAsync(CancellationToken.None);
            IsFormMode = false;
            _formActivePage = null;
            StatusText = Loc.Get("Ready");
            BumpFormRender();
            return;
        }

        if (!await Document.PrimaryHandle.InitFormsAsync(CancellationToken.None))
        {
            HasAcroForm = false;
            return;
        }
        CancelPlacement();
        IsFormMode = true;
        StatusText = Loc.Get("FormModeHint");
        BumpFormRender();
    }

    private void BumpFormRender()
    {
        FormRenderVersion++;
        RebuildPages(); // растры перечитываются с полями/новыми значениями
    }

    /// <summary>Запрос показа собственного выпадающего списка combo/list-поля (pdfium попапов не рисует).</summary>
    public sealed record FormComboRequest(PageViewModel Page, NexusPdf.Pdf.Abstractions.PdfComboInfo Combo, double DpiScale);

    public event EventHandler<FormComboRequest>? FormComboRequested;

    public async Task FormClickAsync(PageViewModel page, double xPt, double yPt, double dpiScale)
    {
        if (!IsFormMode || IsBusy) return;
        // Формы принадлежат первичному источнику; вставленные чужие страницы не интерактивны.
        if (page.PageRef.SourceId != Document.PrimarySourceId) return;

        // Выпадающие списки рисуем сами: у pdfium нет собственных попапов.
        var combo = await Document.PrimaryHandle.GetFormComboAtAsync(
            page.PageRef.SourcePageIndex, page.PageRef.RotationOffset, xPt, yPt, CancellationToken.None);
        if (combo != null)
        {
            _formActivePage = page;
            FormComboRequested?.Invoke(this, new FormComboRequest(page, combo, dpiScale));
            return;
        }

        await Document.PrimaryHandle.FormClickAsync(
            page.PageRef.SourcePageIndex, page.PageRef.RotationOffset, xPt, yPt, CancellationToken.None);
        _formActivePage = page;
        MarkFormModified();
        await RefreshFormPageAsync(page, dpiScale);
    }

    /// <summary>Выбор пункта выпадающего списка (вызывается попапом из View).</summary>
    public async Task FormComboSelectAsync(
        PageViewModel page, NexusPdf.Pdf.Abstractions.PdfComboInfo combo, int optionIndex, double dpiScale)
    {
        if (!IsFormMode || IsBusy) return;
        await Document.PrimaryHandle.SetFormComboSelectionAsync(
            page.PageRef.SourcePageIndex, page.PageRef.RotationOffset,
            combo.XPt + combo.WidthPt / 2, combo.YPt + combo.HeightPt / 2,
            optionIndex, CancellationToken.None);
        MarkFormModified();
        await RefreshFormPageAsync(page, dpiScale);
    }

    public async Task FormCharAsync(char character, double dpiScale)
    {
        if (!IsFormMode || IsBusy || _formActivePage is not { } page) return;
        await Document.PrimaryHandle.FormCharAsync(character, CancellationToken.None);
        MarkFormModified();
        await RefreshFormPageAsync(page, dpiScale);
    }

    public async Task FormKeyAsync(int virtualKey, double dpiScale)
    {
        if (!IsFormMode || IsBusy || _formActivePage is not { } page) return;
        await Document.PrimaryHandle.FormKeyDownAsync(virtualKey, CancellationToken.None);
        MarkFormModified();
        await RefreshFormPageAsync(page, dpiScale);
    }

    private void MarkFormModified()
    {
        if (_formModified) return;
        _formModified = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private Task RefreshFormPageAsync(PageViewModel page, double dpiScale)
    {
        FormRenderVersion++;
        page.ForceRefresh(dpiScale);
        page.ForceRefreshThumbnail();
        return Task.CompletedTask;
    }

    /// <summary>После сохранения документ переоткрыт новым дескриптором — форм-окружение потеряно.</summary>
    public void ResetFormStateAfterSave()
    {
        IsFormMode = false;
        _formActivePage = null;
        _formModified = false;
        OnPropertyChanged(nameof(IsDirty));
        FormRenderVersion++;
        _ = DetectFormsAsync();
    }

    // ----- Выделение текста мышью и ссылки -----

    private PageViewModel? _selectionPage;
    private int _selectionAnchorChar = -1;
    private int _selectionEndChar = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string _selectedText = "";

    public bool HasSelection => SelectedText.Length > 0;

    /// <summary>Начало выделения: символ под курсором становится якорем.</summary>
    public async Task<bool> BeginTextSelectionAsync(PageViewModel page, double xPt, double yPt)
    {
        ClearTextSelection();
        // Текст берётся со страницы С ПРАВКАМИ: распознанный OCR слой и
        // добавленные надписи должны выделяться сразу, а не после сохранения.
        var (handle, pageIndex) = await Document.ResolveTextPageAsync(
            page.LogicalIndex, CancellationToken.None);
        var index = await handle.GetCharIndexAtAsync(
            pageIndex, page.PageRef.RotationOffset, xPt, yPt, CancellationToken.None);
        if (index < 0)
            return false;
        _selectionPage = page;
        _selectionAnchorChar = index;
        _selectionEndChar = index;
        return true;
    }

    /// <summary>Протяжка выделения до символа под курсором.</summary>
    public async Task UpdateTextSelectionAsync(PageViewModel page, double xPt, double yPt)
    {
        // Выделение живёт в пределах одной страницы: у каждой страницы своя
        // система координат символов, сквозное выделение — отдельная работа.
        if (_selectionPage == null || !ReferenceEquals(page, _selectionPage) || _selectionAnchorChar < 0)
            return;
        var (handle, pageIndex) = await Document.ResolveTextPageAsync(
            page.LogicalIndex, CancellationToken.None);
        var index = await handle.GetCharIndexAtAsync(
            pageIndex, page.PageRef.RotationOffset, xPt, yPt, CancellationToken.None);
        if (index < 0 || index == _selectionEndChar)
            return;
        _selectionEndChar = index;
        await RefreshSelectionAsync();
    }

    private async Task RefreshSelectionAsync()
    {
        if (_selectionPage is not { } page || _selectionAnchorChar < 0 || _selectionEndChar < 0)
            return;
        var start = Math.Min(_selectionAnchorChar, _selectionEndChar);
        var count = Math.Abs(_selectionEndChar - _selectionAnchorChar) + 1;
        var (handle, pageIndex) = await Document.ResolveTextPageAsync(
            page.LogicalIndex, CancellationToken.None);
        try
        {
            var rects = await handle.GetTextRectsAsync(
                pageIndex, start, count, CancellationToken.None);
            page.SelectionRects = rects.Select(r => TransformRectToDiu(r, page)).ToList();

            var text = await handle.GetPageTextAsync(pageIndex, CancellationToken.None);
            SelectedText = start < text.Length
                ? text.Substring(start, Math.Min(count, text.Length - start))
                : "";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось обновить выделение текста");
        }
    }

    public void ClearTextSelection()
    {
        if (_selectionPage != null)
            _selectionPage.SelectionRects = Array.Empty<Rect>();
        _selectionPage = null;
        _selectionAnchorChar = -1;
        _selectionEndChar = -1;
        SelectedText = "";
    }

    [RelayCommand]
    private void CopySelection()
    {
        if (!HasSelection) return;
        try
        {
            System.Windows.Clipboard.SetText(SelectedText);
            StatusText = Loc.F("CopiedChars", SelectedText.Length);
        }
        catch (Exception ex)
        {
            // Буфер обмена может быть занят другим процессом.
            Serilog.Log.Warning(ex, "Не удалось скопировать текст в буфер обмена");
            StatusText = Loc.Get("CopyFailed");
        }
    }

    /// <summary>Выделить весь текст текущей страницы (Ctrl+A).</summary>
    [RelayCommand]
    private async Task SelectAllOnPage()
    {
        var index = Math.Clamp(CurrentPageNumber - 1, 0, Math.Max(0, Pages.Count - 1));
        if (index >= Pages.Count) return;
        var page = Pages[index];
        var handle = Document.Handles[page.PageRef.SourceId];
        var text = await handle.GetPageTextAsync(page.PageRef.SourcePageIndex, CancellationToken.None);
        if (text.Length == 0) return;
        ClearTextSelection();
        _selectionPage = page;
        _selectionAnchorChar = 0;
        _selectionEndChar = text.Length - 1;
        await RefreshSelectionAsync();
    }

    /// <summary>
    /// Предупреждение об активном содержимом при открытии. Программа его не
    /// выполняет, но пользователь обязан узнать о нём сразу, а не из свойств.
    /// </summary>
    public async Task CheckActiveContentAsync()
    {
        try
        {
            var active = await Document.PrimaryHandle.GetActiveContentAsync(CancellationToken.None);
            if (!active.HasAny)
                return;
            var parts = new List<string>();
            if (active.JavaScriptCount > 0)
                parts.Add(Loc.F("ActiveContentJs", active.JavaScriptCount));
            if (active.AttachmentCount > 0)
                parts.Add(Loc.F("ActiveContentAttachments", active.AttachmentCount));
            if (active.LaunchActionCount > 0)
                parts.Add(Loc.Get("ActiveContentLaunch"));
            StatusText = Loc.F("ActiveContentBanner", string.Join(", ", parts));
            Serilog.Log.Information(
                "Активное содержимое: скриптов {Js}, вложений {Att}, launch {Launch}",
                active.JavaScriptCount, active.AttachmentCount, active.LaunchActionCount);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось проверить активное содержимое");
        }
    }

    /// <summary>Ссылки страницы (лениво, один раз на страницу).</summary>
    public async Task EnsureLinksAsync(PageViewModel page)
    {
        if (page.LinksLoaded) return;
        page.LinksLoaded = true; // повторных попыток при ошибке не делаем
        try
        {
            var links = await Document.Handles[page.PageRef.SourceId]
                .GetPageLinksAsync(page.PageRef.SourcePageIndex, CancellationToken.None);
            page.LinkAreas = links
                .Select(l => (Area: TransformRectToDiu(l.RectPt, page), Link: l))
                .ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать ссылки страницы {Page}", page.PageNumber);
        }
    }

    /// <summary>Ссылка под курсором (координаты — DIU внутри страницы) или null.</summary>
    public NexusPdf.Pdf.Abstractions.PdfPageLink? LinkAt(PageViewModel page, double xDiu, double yDiu)
    {
        foreach (var (area, link) in page.LinkAreas)
        {
            if (xDiu >= area.X && xDiu <= area.X + area.Width &&
                yDiu >= area.Y && yDiu <= area.Y + area.Height)
                return link;
        }
        return null;
    }

    /// <summary>Событие: пользователь активировал внешнюю ссылку (подтверждение показывает окно).</summary>
    public event EventHandler<string>? ExternalLinkRequested;

    public void ActivateLink(NexusPdf.Pdf.Abstractions.PdfPageLink link)
    {
        if (link.Uri is { Length: > 0 } uri)
        {
            // Внешний адрес НИКОГДА не открывается молча.
            ExternalLinkRequested?.Invoke(this, uri);
            return;
        }
        if (link.TargetPageIndex >= 0)
        {
            // Ссылка адресует страницу ИСХОДНОГО файла; после перестановки
            // страниц ищем её текущее положение в сессии.
            var pages = Document.Session.Model.Pages;
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i].SourcePageIndex == link.TargetPageIndex)
                {
                    GoToPage(i + 1);
                    return;
                }
            }
            StatusText = Loc.Get("LinkTargetMissing");
        }
    }

    // ----- Панель комментариев -----

    /// <summary>Строка панели: для черновика хранится ссылка на сам оверлей —
    /// индексы устаревают между изменением сессии и асинхронным обновлением
    /// панели. Существующая аннотация помнит источник и свой индекс в нём
    /// (исходный файл не мутируется до сохранения — индекс стабилен).</summary>
    public sealed record CommentItem(
        int PageNumber, string TypeLabel, string Author, string Text,
        bool IsDraft, NexusPdf.Pdf.Abstractions.PageOverlay? Draft,
        Guid SourceId = default, int SourcePageIndex = -1, int AnnotIndex = -1)
    {
        /// <summary>Черновики удаляются всегда; существующие — если найден их индекс.</summary>
        public bool Deletable => IsDraft || AnnotIndex >= 0;
    }

    public ObservableCollection<CommentItem> Comments { get; } = new();

    [ObservableProperty]
    private bool _isCommentsVisible;

    [RelayCommand]
    private async Task ToggleComments()
    {
        IsCommentsVisible = !IsCommentsVisible;
        if (IsCommentsVisible)
            await RefreshCommentsAsync();
    }

    [RelayCommand]
    private void DeleteComment(CommentItem? item)
    {
        if (item == null) return;
        var pages = Document.Session.Model.Pages;

        if (item is { IsDraft: true, Draft: { } draft })
        {
            // Актуальные индексы резолвятся по ссылке в момент клика: панель
            // могла ещё не обновиться после предыдущего изменения сессии.
            for (var i = 0; i < pages.Count; i++)
            {
                var list = pages[i].OverlayList;
                for (var k = 0; k < list.Count; k++)
                {
                    if (ReferenceEquals(list[k], draft))
                    {
                        Document.Session.Apply(new RemoveOverlayAtOperation(i, k));
                        return;
                    }
                }
            }
            return; // оверлея уже нет (Undo/устаревшая строка)
        }

        if (item.AnnotIndex < 0) return;
        // Существующая аннотация. Сначала — страница, с которой строка была
        // построена (критично при ДУБЛЯХ одной исходной страницы: у копий
        // одинаковый источник, и поиск «первой подходящей» пометил бы не ту).
        var target = -1;
        var byNumber = item.PageNumber - 1;
        if (byNumber >= 0 && byNumber < pages.Count &&
            pages[byNumber].SourceId == item.SourceId &&
            pages[byNumber].SourcePageIndex == item.SourcePageIndex)
        {
            target = byNumber;
        }
        else
        {
            // Порядок страниц изменился: среди копий источника предпочитаем
            // ту, где эта аннотация ещё не помечена.
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i].SourceId != item.SourceId ||
                    pages[i].SourcePageIndex != item.SourcePageIndex)
                    continue;
                if (!pages[i].RemovedAnnotationList.Contains(item.AnnotIndex))
                {
                    target = i;
                    break;
                }
                if (target < 0)
                    target = i;
            }
        }
        if (target >= 0 && !pages[target].RemovedAnnotationList.Contains(item.AnnotIndex))
            Document.Session.Apply(new RemoveExistingAnnotationOperation(target, item.AnnotIndex));
    }

    [RelayCommand]
    private void GoToComment(CommentItem? item)
    {
        if (item != null)
            GoToPage(item.PageNumber);
    }

    private int _commentsRefreshVersion;

    public async Task RefreshCommentsAsync()
    {
        // Перекрывающиеся обновления (быстрые правки подряд) не должны давать
        // «победу» устаревшего снимка: публикует результат только последний запуск.
        var version = ++_commentsRefreshVersion;

        var items = new List<CommentItem>();
        var pages = Document.Session.Model.Pages.ToArray();
        for (var i = 0; i < pages.Length; i++)
        {
            // Черновики этой сессии (ещё не сохранены).
            foreach (var overlay in pages[i].OverlayList)
            {
                var (label, author, text) = overlay switch
                {
                    NexusPdf.Pdf.Abstractions.NoteAnnotationDraft n => (Loc.Get("AnnotNote"), n.Author, n.Contents),
                    NexusPdf.Pdf.Abstractions.ShapeAnnotationDraft s => (
                        s.FillArgb != 0 && s.BorderWidthPt == 0 ? Loc.Get("AnnotHighlight")
                            : s.IsEllipse ? Loc.Get("AnnotEllipse") : Loc.Get("AnnotRect"),
                        s.Author, s.Contents),
                    NexusPdf.Pdf.Abstractions.RedactionDraft =>
                        (Loc.Get("AnnotRedaction"), "", Loc.Get("AnnotRedactionText")),
                    _ => ("", "", ""),
                };
                if (label.Length > 0)
                    items.Add(new CommentItem(i + 1, label, author, text, true, overlay));
            }

            // Существующие аннотации файла (только чтение).
            try
            {
                var existing = await Document.Handles[pages[i].SourceId]
                    .GetAnnotationsAsync(pages[i].SourcePageIndex, CancellationToken.None);
                foreach (var a in existing)
                {
                    // Помеченные к удалению в этой сессии не показываются.
                    if (pages[i].RemovedAnnotationList.Contains(a.AnnotIndex))
                        continue;
                    var label = a.Subtype switch
                    {
                        1 => Loc.Get("AnnotNote"),
                        5 => Loc.Get("AnnotRect"),
                        6 => Loc.Get("AnnotEllipse"),
                        9 => Loc.Get("AnnotHighlight"),
                        _ => Loc.Get("AnnotOther"),
                    };
                    // Виджеты форм (Subtype 20) — часть AcroForm: их удаление
                    // сломало бы форму, кнопки удаления у них нет.
                    var deletableIndex = a.Subtype == 20 ? -1 : a.AnnotIndex;
                    if (a.Contents.Length > 0 || a.Subtype is 1 or 5 or 6 or 9)
                        items.Add(new CommentItem(i + 1, label, a.Author, a.Contents, false, null,
                            pages[i].SourceId, pages[i].SourcePageIndex, deletableIndex));
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Не удалось прочитать аннотации страницы {Page}", i + 1);
            }

            if (version != _commentsRefreshVersion)
                return; // запущено более свежее обновление
        }

        if (version != _commentsRefreshVersion)
            return;
        Comments.Clear();
        foreach (var item in items.OrderBy(c => c.PageNumber))
            Comments.Add(item);
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
            // Координаты берутся С ТОЙ ЖЕ страницы, по которой шёл поиск:
            // на странице с несохранённым слоем OCR или добавленной надписью
            // исходный лист не знает этих символов, и подсветка либо
            // пропадала, либо вставала не на то слово.
            var (handle, pageIndex) = await Document.ResolveTextPageAsync(
                match.LogicalPageIndex, CancellationToken.None);
            var rects = await handle.GetTextRectsAsync(
                pageIndex, match.CharIndex, match.Length, CancellationToken.None);
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
        // Ссылка на активную страницу формы указывает на пересозданный список.
        if (_formActivePage is { } stale)
        {
            _formActivePage = Pages.FirstOrDefault(p =>
                p.PageRef.SourceId == stale.PageRef.SourceId &&
                p.PageRef.SourcePageIndex == stale.PageRef.SourcePageIndex);
            if (_formActivePage == null)
                _ = Document.PrimaryHandle.FormKillFocusAsync(CancellationToken.None);
        }
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        ClearSearch();
        RemapBookmarks(); // страницы могли переставить или удалить
        DropRestoreOffer();
        if (IsCommentsVisible)
            _ = RefreshCommentsAsync();
    }

    /// <summary>
    /// Предложение вернуть свободный штрих живёт ровно до следующего изменения
    /// документа. Оно опирается на то, что выпрямленный штрих — вершина стека
    /// отмены; любая другая правка (в том числе Ctrl+Z вручную) это ломает.
    /// Сессия шлёт Changed синхронно, а EndStroke выставляет предложение УЖЕ
    /// ПОСЛЕ своего Apply — поэтому собственный штрих здесь не гасится.
    /// </summary>
    private void DropRestoreOffer()
    {
        if (_lastStraightened == null) return;
        _lastStraightened = null;
        CanRestoreFreeStroke = false;
    }

    // ----- Рисование от руки -----

    public enum DrawTool { None, Pencil, Line, Arrow }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrawing))]
    [NotifyPropertyChangedFor(nameof(IsPencilActive))]
    [NotifyPropertyChangedFor(nameof(IsLineActive))]
    [NotifyPropertyChangedFor(nameof(IsArrowActive))]
    private DrawTool _activeDrawTool = DrawTool.None;

    public bool IsDrawing => ActiveDrawTool != DrawTool.None;
    public bool IsPencilActive => ActiveDrawTool == DrawTool.Pencil;
    public bool IsLineActive => ActiveDrawTool == DrawTool.Line;
    public bool IsArrowActive => ActiveDrawTool == DrawTool.Arrow;

    /// <summary>Цвет линии. По умолчанию красный — рисунок должен быть виден поверх чёрного текста.</summary>
    [ObservableProperty]
    private uint _drawColorArgb = 0xFFE02424;

    [ObservableProperty]
    private double _drawWidthPt = 2.0;

    /// <summary>Сила стабилизации, 0 — выключена. Меняется пользователем.</summary>
    [ObservableProperty]
    private double _drawStabilization = StrokeProcessor.DefaultStabilization;

    /// <summary>Автовыпрямление почти прямых штрихов карандаша.</summary>
    [ObservableProperty]
    private bool _drawAutoStraighten = true;

    private PageViewModel? _drawPage;
    private readonly List<StrokePoint> _drawRaw = new();

    /// <summary>
    /// Последний штрих, который автовыпрямление превратило в отрезок. Пока он
    /// здесь, пользователь может вернуть свой исходный «живой» штрих.
    /// </summary>
    private (int PageIndex, InkAnnotationDraft Straightened, InkAnnotationDraft Free)? _lastStraightened;

    [ObservableProperty]
    private bool _canRestoreFreeStroke;

    public void SelectDrawTool(DrawTool tool)
    {
        CancelPlacement();
        ClearTextSelection();
        ActiveDrawTool = ActiveDrawTool == tool ? DrawTool.None : tool;
        StatusText = ActiveDrawTool switch
        {
            DrawTool.Pencil => Loc.Get("DrawHintPencil"),
            DrawTool.Line => Loc.Get("DrawHintLine"),
            DrawTool.Arrow => Loc.Get("DrawHintArrow"),
            _ => Loc.Get("Ready"),
        };
    }

    public void BeginStroke(PageViewModel page, double xPt, double yPt)
    {
        if (!IsDrawing || IsBusy) return;
        _drawPage = page;
        // Рамка, в которой рисуют: если страницу потом повернут, штрих
        // поедет вместе с ней.
        _drawPageRotation = page.PageRef.RotationOffset;
        _drawRaw.Clear();
        _drawRaw.Add(new StrokePoint(xPt, yPt));
        page.DrawPreviewWidth = DrawWidthPt;
        page.DrawPreviewBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(DrawColorArgb >> 24), (byte)(DrawColorArgb >> 16),
            (byte)(DrawColorArgb >> 8), (byte)DrawColorArgb));
        UpdatePreview(page, new[] { new StrokePoint(xPt, yPt) });
    }

    /// <summary>Продолжение штриха. shift — привязка направления к 45°.</summary>
    public void ContinueStroke(PageViewModel page, double xPt, double yPt, bool shift)
    {
        if (_drawPage == null || !ReferenceEquals(page, _drawPage) || _drawRaw.Count == 0) return;
        var point = new StrokePoint(xPt, yPt);

        if (ActiveDrawTool is DrawTool.Line or DrawTool.Arrow)
        {
            // Линия и стрелка — всегда два конца: тянется только конец.
            if (shift)
                point = StrokeProcessor.SnapTo45(_drawRaw[0], point);
            if (_drawRaw.Count == 1) _drawRaw.Add(point); else _drawRaw[^1] = point;
            UpdatePreview(page, _drawRaw);
            return;
        }

        // Карандаш: точки, которые не сдвинулись, не накапливаем.
        if (StrokeProcessor.Distance(_drawRaw[^1], point) < 0.4)
            return;
        _drawRaw.Add(point);
        UpdatePreview(page, StrokeProcessor.Stabilize(_drawRaw, DrawStabilization));
    }

    /// <summary>Штрих прерван (окно потеряло мышь, Esc): ничего не записываем.</summary>
    public void CancelStroke(PageViewModel page)
    {
        if (_drawPage == null || !ReferenceEquals(page, _drawPage)) return;
        _drawPage = null;
        _drawRaw.Clear();
        page.DrawPreview = null;
    }

    /// <summary>Завершение штриха: обработка геометрии и запись в документ.</summary>
    public void EndStroke(PageViewModel page)
    {
        if (_drawPage == null || !ReferenceEquals(page, _drawPage)) return;
        _drawPage = null;
        page.DrawPreview = null;

        var raw = _drawRaw.ToList();
        _drawRaw.Clear();

        var commit = StrokeProcessor.Commit(raw, CurrentKind,
            DrawStabilization, DrawAutoStraighten, DrawWidthPt);
        if (commit == null)
            return; // случайный клик, а не штрих

        var overlay = BuildInk(commit.Strokes);
        Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex, overlay));

        // Автовыпрямление сработало — предложим вернуть свободный штрих.
        _lastStraightened = commit.WasStraightened
            ? (page.LogicalIndex, overlay, BuildInk(commit.FreeStrokes))
            : null;
        CanRestoreFreeStroke = commit.WasStraightened;
        StatusText = commit.WasStraightened
            ? Loc.Get("DrawStraightened")
            : Loc.Get("DrawDone");
    }

    private StrokeProcessor.StrokeKind CurrentKind => ActiveDrawTool switch
    {
        DrawTool.Line => StrokeProcessor.StrokeKind.Line,
        DrawTool.Arrow => StrokeProcessor.StrokeKind.Arrow,
        _ => StrokeProcessor.StrokeKind.Pencil,
    };

    private InkAnnotationDraft BuildInk(IReadOnlyList<IReadOnlyList<StrokePoint>> strokes) =>
        new(strokes.Select(s => (IReadOnlyList<InkPoint>)
                s.Select(p => new InkPoint(p.X, p.Y)).ToList()).ToList(),
            DrawColorArgb, DrawWidthPt, "", Environment.UserName)
        { PlacedRotation = _drawPageRotation };

    private int _drawPageRotation;

    /// <summary>Возврат свободного штриха вместо автоматически выпрямленного.</summary>
    [RelayCommand]
    private void RestoreFreeStroke()
    {
        if (_lastStraightened is not { } last) return;
        CanRestoreFreeStroke = false;
        _lastStraightened = null;

        // Отменять вслепую нельзя: если сверху оказалась чужая правка, Undo
        // снял бы именно её и пользователь молча потерял бы свою работу.
        var pages = Document.Session.Model.Pages;
        if (last.PageIndex >= pages.Count ||
            pages[last.PageIndex].OverlayList.Count == 0 ||
            !ReferenceEquals(pages[last.PageIndex].OverlayList[^1], last.Straightened))
        {
            StatusText = Loc.Get("Ready");
            return;
        }

        Document.Session.Undo();
        Document.Session.Apply(new AddOverlayOperation(last.PageIndex, last.Free));
        StatusText = Loc.Get("DrawFreeRestored");
    }

    private void UpdatePreview(PageViewModel page, IReadOnlyList<StrokePoint> points)
    {
        var collection = new PointCollection(points.Count);
        foreach (var point in points)
            collection.Add(new Point(point.X, point.Y));
        // Стрелка в предпросмотре тоже с наконечником — что видно, то и ляжет.
        if (ActiveDrawTool == DrawTool.Arrow && points.Count >= 2)
        {
            foreach (var barb in StrokeProcessor.ArrowHead(points[^2], points[^1], DrawWidthPt))
            {
                collection.Add(new Point(barb[0].X, barb[0].Y));
                collection.Add(new Point(barb[^1].X, barb[^1].Y));
            }
        }
        page.DrawPreview = collection;
    }

    // ----- Оглавление документа -----

    public ObservableCollection<BookmarkViewModel> Bookmarks { get; } = new();

    [ObservableProperty]
    private bool _hasBookmarks;

    /// <summary>Боковая панель показывает оглавление вместо миниатюр.</summary>
    [ObservableProperty]
    private bool _isOutlineVisible;

    private bool _bookmarksLoaded;

    /// <summary>Читает оглавление один раз за документ; отсутствие оглавления — не ошибка.</summary>
    public async Task EnsureBookmarksAsync()
    {
        if (_bookmarksLoaded) return;
        _bookmarksLoaded = true;
        try
        {
            var tree = await Document.PrimaryHandle.GetBookmarksAsync(CancellationToken.None);
            Bookmarks.Clear();
            foreach (var node in tree)
            {
                var vm = new BookmarkViewModel(node, MapSourcePageToLogical) { IsExpanded = true };
                Bookmarks.Add(vm);
            }
            HasBookmarks = Bookmarks.Count > 0;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать оглавление документа");
            HasBookmarks = false;
        }
    }

    /// <summary>Страница исходного документа → её место в текущем порядке; -1 — страница удалена.</summary>
    private int MapSourcePageToLogical(int sourcePageIndex)
    {
        var pages = Document.Session.Model.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].SourceId == Document.PrimarySourceId &&
                pages[i].SourcePageIndex == sourcePageIndex)
                return i;
        }
        return -1;
    }

    private void RemapBookmarks()
    {
        foreach (var bookmark in Bookmarks)
            bookmark.Remap(MapSourcePageToLogical);
    }

    [RelayCommand]
    private async Task ToggleOutline()
    {
        await EnsureBookmarksAsync();
        IsOutlineVisible = !IsOutlineVisible;
    }

    /// <summary>Переход по закладке. Узел без страницы — просто заголовок раздела.</summary>
    public void GoToBookmark(BookmarkViewModel bookmark)
    {
        if (!bookmark.CanNavigate) return;
        CurrentPageNumber = bookmark.LogicalPageIndex + 1;
        ScrollToPageRequested?.Invoke(this, bookmark.LogicalPageIndex);
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
