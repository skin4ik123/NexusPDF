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

    /// <summary>
    /// Страницы документа. Коллекция с пакетной заменой: перечисление по одной
    /// заставляло списки интерфейса пересчитывать раскладку на каждую строку.
    /// </summary>
    public BulkObservableCollection<PageViewModel> Pages { get; } = new();

    public event EventHandler<int>? ScrollToPageRequested;

    public string Title => Document.DisplayName;
    public string? FilePath => Document.Session.FilePath;

    /// <summary>
    /// Что произносит экранный диктор о вкладке. Без него читалось имя ТИПА —
    /// «NexusPdf.App.Desktop.ViewModels.DocumentViewModel»: название документа
    /// живёт в шаблоне заголовка, а не в тексте самой вкладки. Несохранённость
    /// проговаривается словом: точка у названия глазами видна, на слух — нет.
    /// </summary>
    public string AccessibleName => Localization.Loc.F(
        IsDirty ? "A11yTabItemDirty" : "A11yTabItem", Title, PageCount);

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
    [NotifyPropertyChangedFor(nameof(CurrentPageSizeWithOrientation))]
    [NotifyPropertyChangedFor(nameof(CurrentPageRotationText))]
    private int _currentPageNumber = 1;

    partial void OnCurrentPageNumberChanged(int value)
    {
        // Свойства страницы обновляются вслед за страницей, но не мешают
        // прокрутке: запрос текста уходит в фон и не ждётся.
        _ = RefreshPagePropertiesAsync();
    }

    public string PageOfText => Loc.F("PageOf", CurrentPageNumber, PageCount);

    public string CurrentPageSizeText =>
        Pages.Count == 0 ? "" : Pages[Math.Clamp(CurrentPageNumber - 1, 0, Pages.Count - 1)].SizeText;

    /// <summary>Размер видимой области просмотра в DIU; обновляется представлением.</summary>
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    /// <summary>Как выбирается масштаб: сам по ширине, сам по странице или вручную.</summary>
    public enum ZoomFit
    {
        /// <summary>Масштаб задал пользователь — программа его не трогает.</summary>
        Manual,
        Width,
        Page,
    }

    /// <summary>
    /// Режим подгонки. Нужен, чтобы масштаб пересчитывался при КАЖДОМ изменении
    /// ширины просмотра: открылась панель справа — документ обязан вписаться в
    /// остаток окна сам, а не уехать под панель.
    /// </summary>
    [ObservableProperty]
    private ZoomFit _fitMode = ZoomFit.Width;

    [RelayCommand]
    private void FitWidth()
    {
        FitMode = ZoomFit.Width;
        ApplyFit();
    }

    [RelayCommand]
    private void FitPage()
    {
        FitMode = ZoomFit.Page;
        ApplyFit();
    }

    private double _lastFitWidth;
    private double _lastFitHeight;

    /// <summary>
    /// Размер видимой области изменился. Пересчёт идёт только при заметном
    /// изменении: появление полосы прокрутки меняет ширину на пару точек, и
    /// без порога масштаб «дышал» бы туда-обратно бесконечно.
    /// </summary>
    public void ApplyViewport(double widthDiu, double heightDiu)
    {
        if (widthDiu > 50) ViewportWidth = widthDiu;
        if (heightDiu > 50) ViewportHeight = heightDiu;

        if (Math.Abs(ViewportWidth - _lastFitWidth) < 4 &&
            Math.Abs(ViewportHeight - _lastFitHeight) < 4)
            return;

        ApplyFit();
    }

    private void ApplyFit()
    {
        if (ViewportWidth <= 50) return;
        _lastFitWidth = ViewportWidth;
        _lastFitHeight = ViewportHeight;

        switch (FitMode)
        {
            case ZoomFit.Width:
                FitWidth(ViewportWidth);
                break;
            case ZoomFit.Page:
                FitPage(ViewportWidth, ViewportHeight > 0 ? ViewportHeight : 700);
                break;
        }
    }

    [ObservableProperty]
    private string _statusText = Loc.Get("Ready");

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Ход текущей длинной операции. Живёт рядом с документом, потому что
    /// занят именно документ: в другой вкладке работа продолжается.
    /// </summary>
    public BusyOperation Busy { get; } = new();

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

    /// <summary>
    /// Масштаб, заданный пользователем: колесо, щипок, кнопки. Подгонка после
    /// этого выключается — иначе следующее же изменение ширины окна отняло бы
    /// у пользователя его масштаб.
    /// </summary>
    public void SetZoom(double zoom)
    {
        FitMode = ZoomFit.Manual;
        Zoom = Math.Clamp(zoom, 0.25, 4.0);
    }

    /// <summary>Масштаб от подгонки — режим не сбрасывается.</summary>
    private void SetZoomFitted(double zoom) => Zoom = Math.Clamp(zoom, 0.25, 4.0);

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
        ViewportWidth = viewportWidthDiu;
        ApplyFit();
    }

    public void FitWidth(double viewportWidthDiu)
    {
        var maxPageWidthPt = Pages.Count == 0 ? 612 : Pages.Max(p => p.SizePt.WidthPoints);
        SetZoomFitted((viewportWidthDiu - PageMarginDiu * 2) / (maxPageWidthPt * PtToDiu));
    }

    public void FitPage(double viewportWidthDiu, double viewportHeightDiu)
    {
        if (Pages.Count == 0) return;
        var page = Pages[Math.Clamp(CurrentPageNumber - 1, 0, Pages.Count - 1)];
        var byWidth = (viewportWidthDiu - PageMarginDiu * 2) / (page.SizePt.WidthPoints * PtToDiu);
        var byHeight = (viewportHeightDiu - PageMarginDiu * 2) / (page.SizePt.HeightPoints * PtToDiu);
        SetZoomFitted(Math.Min(byWidth, byHeight));
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

    // ----- Правка текста прямо на странице -----

    /// <summary>Просьба открыть правку на месте в указанной точке страницы.</summary>
    /// <param name="Explicit">
    /// true — человек нажал «Править текст». Тогда молчать в ответ на промах
    /// нельзя: он попросил открыть правку и ждёт либо поля ввода, либо
    /// объяснения. У двойного клика наоборот: промах — обычное дело.
    /// </param>
    public sealed record InlineEditRequest(PageViewModel Page, double XPt, double YPt, bool Explicit);

    /// <summary>
    /// Поле ввода живёт в представлении, поэтому открывает его оно. Команда и
    /// контекстное меню шлют сюда просьбу — чтобы у правки текста был ОДИН
    /// путь. Раньше кнопка «Править текст» ходила своей дорогой мимо этого
    /// поиска и на добавленной надписи говорила, что текста нет.
    /// </summary>
    public event EventHandler<InlineEditRequest>? InlineEditRequested;

    public void RequestInlineEdit(PageViewModel page, double xPt, double yPt, bool isExplicit) =>
        InlineEditRequested?.Invoke(this, new InlineEditRequest(page, xPt, yPt, isExplicit));

    /// <summary>
    /// Что сказать, когда править в точке нечего. Ставит MainViewModel: там
    /// живут диалоги и запуск распознавания.
    /// </summary>
    public Action<PageViewModel>? ExplainNothingToEdit { get; set; }

    /// <summary>
    /// Строка под курсором, готовая к правке на месте. Их три вида: надпись,
    /// добавленная в этом сеансе, настоящий текстовый объект PDF и строка,
    /// полученная распознаванием. Снаружи они выглядят одинаково —
    /// пользователю всё равно, откуда взялись буквы.
    /// </summary>
    /// <param name="Text">Что написано сейчас.</param>
    /// <param name="RectPt">Рамка в отображаемых пунктах: по ней рисуется поле ввода.</param>
    /// <param name="FontSizePt">Кегль — чтобы поле ввода было того же размера, что и текст.</param>
    /// <param name="ObjectPath">Адрес объекта страницы; пустой у распознанной строки.</param>
    /// <param name="Layer">Слой распознавания; null у настоящего объекта PDF.</param>
    /// <param name="LineIndex">Номер строки внутри слоя распознавания.</param>
    /// <param name="Draft">
    /// Надпись, добавленная в этом сеансе и ещё не запечённая в файл. Такую
    /// строку движок не видит — она живёт в правках сессии, — поэтому правится
    /// заменой самой правки.
    /// </param>
    public sealed record InlineTextTarget(
        string Text, Rect RectPt, double FontSizePt,
        IReadOnlyList<int> ObjectPath, OcrEditableTextOverlay? Layer, int LineIndex,
        string FontName = "", uint ColorArgb = 0xFF000000, bool NeedsRasterErase = false,
        TextOverlay? Draft = null);

    /// <summary>
    /// Ищет правимую строку в точке страницы. Порядок — сверху вниз по тому,
    /// что человек видит: сначала собственные надписи этого сеанса, затем
    /// распознанные строки (обе лежат в правках сессии, попадание считается на
    /// месте), и лишь потом настоящий текст файла через движок.
    /// </summary>
    public async Task<InlineTextTarget?> FindInlineTextAsync(
        PageViewModel page, double xPt, double yPt, CancellationToken ct)
    {
        var draft = FindAddedText(page, xPt, yPt);
        if (draft != null)
            return draft;

        var recognized = FindRecognizedLine(page, xPt, yPt);
        if (recognized != null)
            return recognized;

        var handle = Document.Handles[page.PageRef.SourceId];
        var found = await handle.GetTextObjectAtAsync(
            page.PageRef.SourcePageIndex, page.PageRef.RotationOffset, xPt, yPt, ct);
        if (found == null)
            return null;

        // Текст внутри вложенного объекта правится ДРУГИМ путём: строка
        // стирается растеризацией страницы, а новая пишется поверх настоящим
        // текстом. Цена — страница становится изображением, поэтому решение
        // за человеком; спрашивает представление.
        if (!found.CanEdit)
        {
            return new InlineTextTarget(
                found.Text,
                new Rect(found.XPt, found.YPt, found.WidthPt, found.HeightPt),
                found.FontSizePt, found.ObjectPath, null, -1,
                found.FontName, found.ColorArgb, NeedsRasterErase: true);
        }

        return new InlineTextTarget(
            found.Text,
            new Rect(found.XPt, found.YPt, found.WidthPt, found.HeightPt),
            found.FontSizePt, found.ObjectPath, null, -1,
            found.FontName, found.ColorArgb);
    }

    /// <summary>
    /// Надпись, добавленная в этом сеансе. Ищется по той же рамке, по которой
    /// такой объект выделяется мышью, — иначе выделить его можно было бы там,
    /// где правка не открывается. Движок эти надписи не видит вовсе: они
    /// попадают в файл только при сохранении, а до того живут в правках.
    /// </summary>
    private InlineTextTarget? FindAddedText(PageViewModel page, double xPt, double yPt)
    {
        var overlays = page.PageRef.OverlayList;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            if (overlays[i] is not TextOverlay original)
                continue;
            if (ToDisplayFrame(overlays[i], page) is not TextOverlay shown)
                continue;
            if (OverlayGeometry.BoundsOf(shown) is not { } box)
                continue;
            if (!box.Inflated(NexusPdf.Ux.ObjectHandles.HandleToleranceDip / Math.Max(Zoom, 0.1))
                    .Contains(xPt, yPt))
                continue;

            return new InlineTextTarget(
                original.Text,
                new Rect(box.XPt, box.YPt, box.WidthPt, box.HeightPt),
                original.FontSizePt, Array.Empty<int>(), null, -1,
                original.FontFamily, original.ColorArgb, Draft: original);
        }
        return null;
    }

    private InlineTextTarget? FindRecognizedLine(PageViewModel page, double xPt, double yPt)
    {
        var overlays = Document.Session.Model.Pages[page.LogicalIndex].OverlayList;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            if (overlays[i] is not OcrEditableTextOverlay layer)
                continue;

            var mapped = OverlayDisplayMapper.ToFrame(
                layer, page.PageRef.RotationOffset,
                page.SizePt.WidthPoints, page.SizePt.HeightPoints).Overlay as OcrEditableTextOverlay;
            if (mapped == null)
                continue;

            for (var j = 0; j < mapped.Lines.Count; j++)
            {
                var line = mapped.Lines[j];
                if (xPt < line.XPt || xPt > line.XPt + line.WidthPt ||
                    yPt < line.YPt || yPt > line.YPt + line.HeightPt)
                    continue;
                return new InlineTextTarget(
                    line.Text,
                    new Rect(line.XPt, line.YPt, line.WidthPt, line.HeightPt),
                    line.HeightPt, Array.Empty<int>(), layer, j,
                    Loc.Get("OcrRecognizedLine"), line.InkArgb);
            }
        }
        return null;
    }

    /// <summary>
    /// Цвет бумаги рядом со строкой — с УЖЕ нарисованной страницы. Белый по
    /// умолчанию: на цветной подложке заплатка иначе бросалась бы в глаза, а
    /// угадывать цвет неоткуда, если картинки страницы ещё нет.
    /// </summary>
    private uint SampleLineBackground(PageViewModel page, Rect rectPt)
    {
        var source = page.Image;
        if (source == null || page.SizePt.WidthPoints <= 0 || page.SizePt.HeightPoints <= 0)
            return 0xFFFFFFFF;

        var pxPerPtX = source.PixelWidth / page.SizePt.WidthPoints;
        var pxPerPtY = source.PixelHeight / page.SizePt.HeightPoints;

        // Точки берутся ВОКРУГ строки, а не внутри: внутри буквы.
        var samples = new List<uint>();
        void Take(double xPt, double yPt)
        {
            var x = (int)Math.Round(xPt * pxPerPtX);
            var y = (int)Math.Round(yPt * pxPerPtY);
            if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight)
                return;
            try
            {
                var pixel = new byte[4];
                source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                samples.Add(0xFF000000u | ((uint)pixel[2] << 16) | ((uint)pixel[1] << 8) | pixel[0]);
            }
            catch (ArgumentException)
            {
                // Картинка сменилась под рукой — цвет просто не учитываем.
            }
        }

        var margin = Math.Max(2.0, rectPt.Height * 0.4);
        for (var i = 0; i <= 6; i++)
        {
            var xPt = rectPt.X + rectPt.Width * i / 6.0;
            Take(xPt, rectPt.Y - margin);
            Take(xPt, rectPt.Y + rectPt.Height + margin);
        }
        if (samples.Count == 0)
            return 0xFFFFFFFF;

        // Медиана по яркости: одиночная тёмная точка (соседняя строка, линейка)
        // не должна утянуть заплатку в серый.
        samples.Sort((a, b) => Luma(a).CompareTo(Luma(b)));
        return samples[samples.Count / 2];

        static double Luma(uint argb) =>
            0.2126 * ((argb >> 16) & 0xFF) + 0.7152 * ((argb >> 8) & 0xFF) + 0.0722 * (argb & 0xFF);
    }

    /// <summary>
    /// Может ли шрифт строки нарисовать введённое. У встроенного подмножества
    /// шрифта нужных букв может просто не быть, и узнать об этом надо ДО
    /// правки, а не после сохранения. Распознанная строка пишется системным
    /// шрифтом и рисует что угодно.
    /// </summary>
    public Task<bool> CanRenderInlineAsync(
        PageViewModel page, InlineTextTarget target, string text, CancellationToken ct)
    {
        // Спрашивать имеет смысл ТОЛЬКО у встроенного шрифта настоящего объекта
        // страницы: только он может не знать нужных букв. У распознанной строки
        // и у собственной надписи рисует шрифт из каталога — он с кириллицей и
        // латиницей справляется по определению. Раньше у них не было адреса
        // объекта, вопрос уходил в пустоту, и ответ «этот шрифт не нарисует
        // такие буквы» приходил на обычный русский текст в Segoe UI.
        if (target.Layer != null || target.Draft != null || target.ObjectPath.Count == 0)
            return Task.FromResult(true);
        return Document.Handles[page.PageRef.SourceId].CanFontRenderTextAsync(
            page.PageRef.SourcePageIndex, target.ObjectPath, text, ct);
    }

    /// <summary>
    /// Применяет правку строки. Неизменившийся текст ничего не делает.
    /// dpiScale приходит из представления: масштаб экрана знает окно, а не
    /// модель, и перерисовать страницу надо именно в нём.
    /// </summary>
    public void ApplyInlineText(
        PageViewModel page, InlineTextTarget target, string newText,
        string fontFamily, bool bold, bool italic, double fontSizePt, uint colorArgb,
        double dpiScale)
    {
        var styleChanged = fontFamily.Length > 0;
        if (IsBusy || (newText == target.Text && !styleChanged))
            return;

        if (target.Draft is { } draft)
        {
            // Собственная надпись этого сеанса: она ещё не в файле, поэтому
            // меняется сама правка, а не содержимое страницы. Незаданные
            // свойства остаются прежними — правили только текст.
            var updated = draft with
            {
                Text = newText,
                FontSizePt = fontSizePt > 0 ? fontSizePt : draft.FontSizePt,
                ColorArgb = colorArgb != 0 ? colorArgb : draft.ColorArgb,
                FontFamily = fontFamily.Length > 0 ? fontFamily : draft.FontFamily,
                Bold = fontFamily.Length > 0 ? bold : draft.Bold,
                Italic = fontFamily.Length > 0 ? italic : draft.Italic,
            };
            Document.Session.Apply(new ReplaceOverlayOperation(page.LogicalIndex, draft, updated));
            // Выделение держало ПРЕЖНИЙ объект: без обновления рамка осталась
            // бы от старого текста, а «Удалить» убрало бы не то.
            ClearObjectSelection();
        }
        else if (target.Layer is { } layer)
        {
            // У распознанной строки своё представление: цвет чернил и высота
            // строки живут в самом слое, гарнитура у него всегда системная.
            var lines = layer.Lines.ToList();
            var line = lines[target.LineIndex] with { Text = newText };
            if (styleChanged && colorArgb != 0)
                line = line with { InkArgb = colorArgb };
            lines[target.LineIndex] = line;
            Document.Session.Apply(new ReplaceOverlayOperation(
                page.LogicalIndex, layer, layer with { Lines = lines }));
        }
        else if (target.NeedsRasterErase)
        {
            // Строку внутри вложенного объекта иначе не изменить: старые буквы
            // стираются вместе со страницей (растеризацией при сохранении), а
            // новые ложатся обычным текстовым объектом поверх.
            var fill = SampleLineBackground(page, target.RectPt);
            Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex,
                new RegionEraseDraft(
                    target.RectPt.X, target.RectPt.Y,
                    target.RectPt.Width, target.RectPt.Height, fill)));

            var size = fontSizePt > 0 ? fontSizePt : target.FontSizePt;
            Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex,
                new TextOverlay(
                    newText, target.RectPt.X, target.RectPt.Y, size,
                    colorArgb != 0 ? colorArgb : target.ColorArgb, 0,
                    fontFamily.Length > 0 ? fontFamily : PdfFontCatalog.DefaultFamily,
                    bold, italic)));
            page.ForceRefresh(dpiScale);
        }
        else
        {
            Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex,
                new TextObjectReplacement(
                    target.ObjectPath, newText,
                    fontFamily, bold, italic, fontSizePt, colorArgb)));
            page.ForceRefresh(dpiScale);
        }
        StatusText = Loc.Get("TextEditDone");
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

    // ----- Разрешения документа -----

    /// <summary>
    /// Разрешена ли печать флагами документа. Программа их СОБЛЮДАЕТ, поэтому
    /// команды печати обязаны выключаться заранее и объяснять причину, а не
    /// отказывать в последний момент уже открытым диалогом.
    /// </summary>
    [ObservableProperty]
    private bool _allowsPrinting = true;

    public async Task LoadPermissionsAsync()
    {
        try
        {
            var flags = await Document.PrimaryHandle.GetPermissionsAsync(CancellationToken.None);
            AllowsPrinting = NexusPdf.Printing.PrintPermissions.FromFlags(flags).AllowPrint;
        }
        catch (Exception ex)
        {
            // Не прочитались флаги — считаем разрешённым: запрещать печать
            // из-за собственной ошибки чтения нельзя.
            Serilog.Log.Warning(ex, "Не удалось прочитать разрешения документа");
            AllowsPrinting = true;
        }
    }

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
        OnPropertyChanged(nameof(AccessibleName));
    }

    private Task RefreshFormPageAsync(PageViewModel page, double dpiScale)
    {
        FormRenderVersion++;
        page.ForceRefresh(dpiScale);
        page.ForceRefreshThumbnail();
        return Task.CompletedTask;
    }

    /// <summary>
    /// После обработки (чистка, пересжатие, оптимизация) страницы указывают на
    /// НОВЫЙ файл. Список страниц и растровый кэш пересобираются сами — ключи
    /// кэша содержат идентификатор источника, — но окружение, привязанное к
    /// прежнему дескриптору, надо перечитать: поля форм и подписи в
    /// перекомпонованном файле уже другие.
    /// </summary>
    public void ReloadAfterProcessing()
    {
        IsFormMode = false;
        _formActivePage = null;
        _formModified = false;
        FormRenderVersion++;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(AccessibleName));
        _ = DetectFormsAsync();
        _ = LoadSignaturesAsync();
        // Кнопки верхней панели пересчитывают доступность по общему сигналу
        // WPF, а он приходит от ввода. После долгой фоновой операции мышь может
        // не двигаться минутами, и «Сохранить» оставалось бы серым на уже
        // изменённом документе.
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>После сохранения документ переоткрыт новым дескриптором — форм-окружение потеряно.</summary>
    public void ResetFormStateAfterSave()
    {
        IsFormMode = false;
        _formActivePage = null;
        _formModified = false;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(AccessibleName));
        FormRenderVersion++;
        _ = DetectFormsAsync();
    }

    // ----- Выделение текста мышью и ссылки -----

    private PageViewModel? _selectionPage;
    private int _selectionAnchorChar = -1;
    private int _selectionEndChar = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ShowsTextProperties))]
    [NotifyPropertyChangedFor(nameof(ShowsPageProperties))]
    [NotifyPropertyChangedFor(nameof(SelectedTextPreview))]
    [NotifyPropertyChangedFor(nameof(SelectedTextStats))]
    private string _selectedText = "";

    public bool HasSelection => SelectedText.Length > 0;

    /// <summary>
    /// Панель свойств показывает ТО, ЧТО ВЫБРАНО: объект, выделенный текст
    /// либо — когда не выбрано ничего — саму страницу. Пустая панель со словом
    /// «выберите что-нибудь» занимала бы место и ничего не сообщала.
    /// </summary>
    public bool ShowsTextProperties => !HasObjectSelection && HasSelection;

    public bool ShowsPageProperties => !HasObjectSelection && !HasSelection;

    /// <summary>Первые слова выделения: по ним видно, что именно взято.</summary>
    public string SelectedTextPreview
    {
        get
        {
            var text = SelectedText.Replace('\n', ' ').Replace('\r', ' ').Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Length <= 160 ? text : text[..157] + "…";
        }
    }

    /// <summary>Сколько взято: символы и слова считаются по факту, а не «примерно».</summary>
    public string SelectedTextStats
    {
        get
        {
            var words = SelectedText.Split(

                new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return Loc.F("PropTextStats", SelectedText.Length, words);
        }
    }

    /// <summary>Размер текущей страницы с ориентацией: «210 × 297 мм, книжная».</summary>
    public string CurrentPageSizeWithOrientation
    {
        get
        {
            if (Pages.Count == 0) return "";
            var page = Pages[Math.Clamp(CurrentPageNumber - 1, 0, Pages.Count - 1)];
            var orientation = Loc.Get(page.SizePt.WidthPoints > page.SizePt.HeightPoints
                ? "PropLandscape"
                : "PropPortrait");
            return $"{page.SizeText}, {orientation}";
        }
    }

    /// <summary>Поворот страницы, применённый пользователем в этой сессии.</summary>
    public string CurrentPageRotationText
    {
        get
        {
            if (Pages.Count == 0) return "";
            var page = Pages[Math.Clamp(CurrentPageNumber - 1, 0, Pages.Count - 1)];
            var quarters = ((page.PageRef.RotationOffset % 4) + 4) % 4;
            return quarters == 0 ? Loc.Get("PropRotationNone") : Loc.F("PropRotation", quarters * 90);
        }
    }

    /// <summary>Есть ли на странице текстовый слой — по нему видно скан это или вёрстка.</summary>
    [ObservableProperty]
    private string _currentPageTextInfo = "";

    /// <summary>
    /// Разбор текущей страницы для панели свойств. Делается по смене страницы,
    /// а не на каждый кадр: один запрос текста страницы дёшев, но не бесплатен.
    /// </summary>
    private async Task RefreshPagePropertiesAsync()
    {
        OnPropertyChanged(nameof(CurrentPageSizeWithOrientation));
        OnPropertyChanged(nameof(CurrentPageRotationText));
        try
        {
            var index = Math.Clamp(CurrentPageNumber - 1, 0, Math.Max(0, Pages.Count - 1));
            if (Pages.Count == 0) { CurrentPageTextInfo = ""; return; }
            var (handle, pageIndex) = await Document.ResolveTextPageAsync(index, CancellationToken.None);
            var text = await handle.GetPageTextAsync(pageIndex, CancellationToken.None);
            var letters = text.Count(char.IsLetterOrDigit);
            CurrentPageTextInfo = letters switch
            {
                0 => Loc.Get("PropNoTextLayer"),
                < 40 => Loc.F("PropLittleText", letters),
                _ => Loc.F("PropHasText", letters),
            };
        }
        catch (Exception)
        {
            // Свойства — справка, а не операция: молчим и не мешаем работать.
            CurrentPageTextInfo = "";
        }
    }


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
            // Те же строки в пунктах страницы: по ним ставится разметка текста
            // и вымарывание, поэтому они не должны зависеть от масштаба показа.
            _selectionRectsPt = rects.Select(r => TransformRectToDisplayPt(r, page)).ToList();

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
        _selectionRectsPt = Array.Empty<Rect>();
        SelectedText = "";
    }

    /// <summary>Строки текущего выделения в отображаемых пунктах страницы.</summary>
    private IReadOnlyList<Rect> _selectionRectsPt = Array.Empty<Rect>();

    /// <summary>Страница, на которой сейчас выделен текст (выделение не бывает сквозным).</summary>
    public PageViewModel? SelectionPage => _selectionPage;

    /// <summary>Цвета разметки: маркер жёлтый полупрозрачный, подчёркивание синее, зачёркивание красное.</summary>
    private static uint MarkupColor(NexusPdf.Pdf.Abstractions.TextMarkupKind kind) => kind switch
    {
        NexusPdf.Pdf.Abstractions.TextMarkupKind.Highlight => 0x66FDE047,
        NexusPdf.Pdf.Abstractions.TextMarkupKind.Underline => 0xFF2563EB,
        _ => 0xFFDC2626,
    };

    /// <summary>
    /// Разметка ВЫДЕЛЕННОГО текста: маркер, подчёркивание, зачёркивание.
    /// Ставится ровно по строкам выделения, поэтому не нужно попадать рамкой в
    /// текст мышью, и видно её сразу — до сохранения.
    /// </summary>
    public bool MarkupSelection(NexusPdf.Pdf.Abstractions.TextMarkupKind kind)
    {
        if (IsBusy || _selectionPage is not { } page || _selectionRectsPt.Count == 0)
            return false;

        var rects = _selectionRectsPt
            .Where(r => r.Width > 0.1 && r.Height > 0.1)
            .Select(r => new NexusPdf.Pdf.Abstractions.TextMarkupRect(r.X, r.Y, r.Width, r.Height))
            .ToList();
        if (rects.Count == 0)
            return false;

        Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex,
            new NexusPdf.Pdf.Abstractions.TextMarkupDraft(
                kind, rects, MarkupColor(kind), Contents: "", Author: Environment.UserName)));
        ClearTextSelection();
        StatusText = Loc.Get("UxMarkupDone");
        return true;
    }

    /// <summary>
    /// Вымарывание ВЫДЕЛЕННОГО текста: по строке на каждую строку выделения.
    /// Содержимое под ними уничтожается при сохранении — это не чёрная плашка
    /// поверх текста.
    /// </summary>
    public bool RedactSelection()
    {
        if (IsBusy || _selectionPage is not { } page || _selectionRectsPt.Count == 0)
            return false;

        var applied = 0;
        foreach (var rect in _selectionRectsPt)
        {
            if (rect.Width <= 0.1 || rect.Height <= 0.1) continue;
            Document.Session.Apply(new AddOverlayOperation(page.LogicalIndex,
                new NexusPdf.Pdf.Abstractions.RedactionDraft(rect.X, rect.Y, rect.Width, rect.Height)));
            applied++;
        }
        if (applied == 0)
            return false;

        ClearTextSelection();
        StatusText = Loc.Get("RedactHint");
        return true;
    }

    // ----- Выделение наложенного объекта -----

    /// <summary>Выбранный объект: страница, его место в списке и рамка.</summary>
    public sealed record ObjectSelection(
        PageViewModel Page,
        int OverlayIndex,
        NexusPdf.Pdf.Abstractions.PageOverlay Overlay,
        NexusPdf.Pdf.Abstractions.OverlayBox Box);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasObjectSelection))]
    private ObjectSelection? _selectedObject;

    public bool HasObjectSelection => SelectedObject != null;

    /// <summary>
    /// Что именно выбрано — от этого зависит контекстное меню. Разные виды
    /// объектов дают разные меню, поэтому вид определяется по самому объекту,
    /// а не по инструменту, которым его создали.
    /// </summary>
    public NexusPdf.Ux.SelectionKind SelectedObjectKind => SelectedObject?.Overlay switch
    {
        NexusPdf.Pdf.Abstractions.TextOverlay => NexusPdf.Ux.SelectionKind.TextObject,
        NexusPdf.Pdf.Abstractions.ImageOverlay => NexusPdf.Ux.SelectionKind.Image,
        NexusPdf.Pdf.Abstractions.ShapeAnnotationDraft => NexusPdf.Ux.SelectionKind.Shape,
        NexusPdf.Pdf.Abstractions.InkAnnotationDraft => NexusPdf.Ux.SelectionKind.Shape,
        NexusPdf.Pdf.Abstractions.RedactionDraft => NexusPdf.Ux.SelectionKind.Shape,
        NexusPdf.Pdf.Abstractions.NoteAnnotationDraft => NexusPdf.Ux.SelectionKind.Annotation,
        NexusPdf.Pdf.Abstractions.TextMarkupDraft => NexusPdf.Ux.SelectionKind.Annotation,
        _ => NexusPdf.Ux.SelectionKind.Nothing,
    };

    /// <summary>
    /// Выбрать объект под точкой. Перебор идёт с конца: сверху лежит
    /// нарисованный последним, и щелчок обязан попадать именно в него.
    /// </summary>
    public bool SelectObjectAt(PageViewModel page, double xPt, double yPt)
    {
        var overlays = page.PageRef.OverlayList;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            var overlay = ToDisplayFrame(overlays[i], page);
            var abilities = NexusPdf.Pdf.Abstractions.OverlayGeometry.AbilitiesOf(overlay);
            // Щелчком выбирается только то, что можно двигать. Разметка текста
            // лежит поверх строк, и перехватывать ею выделение текста нельзя:
            // читать и копировать документ пользователь будет чаще, чем
            // передвигать маркер (снять разметку можно в панели комментариев).
            if (!abilities.CanMove && !abilities.CanResize)
                continue;
            if (NexusPdf.Pdf.Abstractions.OverlayGeometry.BoundsOf(overlay) is not { } box)
                continue;
            // Допуск в точках страницы, чтобы тонкую линию можно было поймать
            // и на мелком масштабе.
            if (!box.Inflated(NexusPdf.Ux.ObjectHandles.HandleToleranceDip / Math.Max(Zoom, 0.1))
                    .Contains(xPt, yPt))
                continue;

            SelectObject(new ObjectSelection(page, i, overlays[i], box));
            return true;
        }
        return false;
    }

    private void SelectObject(ObjectSelection selection)
    {
        ClearObjectSelection();
        SelectedObject = selection;
        UpdateObjectFrame(selection.Box);
        StatusText = Loc.Get("UxObjectSelected");
    }

    public void ClearObjectSelection()
    {
        if (SelectedObject is { } previous)
        {
            previous.Page.ObjectFrame = null;
            previous.Page.ObjectHandles = Array.Empty<Rect>();
        }
        SelectedObject = null;
        _dragHandle = NexusPdf.Ux.ResizeHandle.None;
    }

    /// <summary>Оверлей в системе координат текущего показа страницы.</summary>
    private NexusPdf.Pdf.Abstractions.PageOverlay ToDisplayFrame(
        NexusPdf.Pdf.Abstractions.PageOverlay overlay, PageViewModel page)
    {
        var source = Document.Handles[page.PageRef.SourceId].Info.Pages[page.PageRef.SourcePageIndex];
        var quarter = page.PageRef.RotationOffset;
        var (width, height) = quarter % 2 == 0
            ? (source.WidthPoints, source.HeightPoints)
            : (source.HeightPoints, source.WidthPoints);
        var (mapped, _) = NexusPdf.Pdf.Abstractions.OverlayDisplayMapper.ToFrame(
            overlay, quarter, width, height);
        return mapped;
    }

    /// <summary>
    /// Держит объект на странице. Утащенный за край объект не виден ни на
    /// экране, ни в сохранённом файле — вернуть его оттуда пользователю нечем,
    /// поэтому проще не выпускать.
    /// </summary>
    private NexusPdf.Ux.HandleBox ClampToPage(NexusPdf.Ux.HandleBox box, PageViewModel page)
    {
        var (width, height) = page.PageRef.RotationOffset % 2 == 0
            ? (page.SizePt.WidthPoints, page.SizePt.HeightPoints)
            : (page.SizePt.HeightPoints, page.SizePt.WidthPoints);

        var normalized = NexusPdf.Pdf.Abstractions.OverlayGeometry.Normalize(
            new NexusPdf.Pdf.Abstractions.OverlayBox(box.X, box.Y, box.Width, box.Height));

        var x = Math.Clamp(normalized.XPt, 0, Math.Max(0, width - normalized.WidthPt));
        var y = Math.Clamp(normalized.YPt, 0, Math.Max(0, height - normalized.HeightPt));
        return new NexusPdf.Ux.HandleBox(x, y, normalized.WidthPt, normalized.HeightPt);
    }

    private void UpdateObjectFrame(NexusPdf.Pdf.Abstractions.OverlayBox box)
    {
        if (SelectedObject is not { } selection) return;
        var page = selection.Page;
        page.ObjectFrame = new Rect(box.XPt, box.YPt, box.WidthPt, box.HeightPt);

        var abilities = NexusPdf.Pdf.Abstractions.OverlayGeometry.AbilitiesOf(
            ToDisplayFrame(selection.Overlay, page));
        if (!abilities.CanResize)
        {
            page.ObjectHandles = Array.Empty<Rect>();
            return;
        }

        // Ручки задаются в пунктах страницы, но их размер на экране постоянный:
        // иначе на мелком масштабе в них невозможно попасть.
        var side = NexusPdf.Ux.ObjectHandles.HandleSizeDip / Math.Max(Zoom, 0.1);
        var frame = new NexusPdf.Ux.HandleBox(box.XPt, box.YPt, box.WidthPt, box.HeightPt);
        page.ObjectHandles = NexusPdf.Ux.ObjectHandles.All
            .Select(h => NexusPdf.Ux.ObjectHandles.CenterOf(frame, h))
            .Select(c => new Rect(c.X - side / 2, c.Y - side / 2, side, side))
            .ToList();
    }

    // ----- Перетаскивание выбранного объекта -----

    private NexusPdf.Ux.ResizeHandle _dragHandle = NexusPdf.Ux.ResizeHandle.None;
    private NexusPdf.Ux.HandleBox _dragStartBox;

    public bool IsDraggingObject => _dragHandle != NexusPdf.Ux.ResizeHandle.None;

    /// <summary>Что под точкой: ручка рамки, тело объекта или ничего.</summary>
    public NexusPdf.Ux.ResizeHandle HitObjectHandle(PageViewModel page, double xPt, double yPt)
    {
        if (SelectedObject is not { } selection || !ReferenceEquals(selection.Page, page))
            return NexusPdf.Ux.ResizeHandle.None;

        var abilities = NexusPdf.Pdf.Abstractions.OverlayGeometry.AbilitiesOf(
            ToDisplayFrame(selection.Overlay, page));
        var box = new NexusPdf.Ux.HandleBox(
            selection.Box.XPt, selection.Box.YPt, selection.Box.WidthPt, selection.Box.HeightPt);
        return NexusPdf.Ux.ObjectHandles.HitTest(
            box, xPt, yPt, abilities.CanResize, 1.0 / Math.Max(Zoom, 0.1));
    }

    public void BeginObjectDrag(NexusPdf.Ux.ResizeHandle handle)
    {
        if (SelectedObject is not { } selection || handle == NexusPdf.Ux.ResizeHandle.None) return;
        var abilities = NexusPdf.Pdf.Abstractions.OverlayGeometry.AbilitiesOf(
            ToDisplayFrame(selection.Overlay, selection.Page));
        if (handle == NexusPdf.Ux.ResizeHandle.Move && !abilities.CanMove) return;

        _dragHandle = handle;
        _dragStartBox = new NexusPdf.Ux.HandleBox(
            selection.Box.XPt, selection.Box.YPt, selection.Box.WidthPt, selection.Box.HeightPt);
    }

    /// <summary>
    /// Живой показ будущего положения. Сам объект не двигается до отпускания
    /// кнопки: каждая правка перерисовывает страницу движком, и делать это на
    /// каждое движение мыши — значит получить рывки вместо перетаскивания.
    /// </summary>
    public void UpdateObjectDrag(double dxPt, double dyPt)
    {
        if (SelectedObject is not { } selection || !IsDraggingObject) return;
        var dragged = NexusPdf.Ux.ObjectHandles.Drag(_dragStartBox, _dragHandle, dxPt, dyPt);
        dragged = NexusPdf.Ux.Snapping.Apply(
            dragged, SnapToGrid, GridStepPt, Array.Empty<double>(), Array.Empty<double>());
        dragged = ClampToPage(dragged, selection.Page);
        selection.Page.DragPreviewRect = new Rect(
            Math.Min(dragged.X, dragged.X + dragged.Width),
            Math.Min(dragged.Y, dragged.Y + dragged.Height),
            Math.Abs(dragged.Width), Math.Abs(dragged.Height));
    }

    /// <summary>Применить перетаскивание одной операцией — она же и отменяется одним Ctrl+Z.</summary>
    public void CommitObjectDrag(double dxPt, double dyPt)
    {
        if (SelectedObject is not { } selection || !IsDraggingObject)
        {
            CancelObjectDrag();
            return;
        }

        var handle = _dragHandle;
        selection.Page.DragPreviewRect = null;
        _dragHandle = NexusPdf.Ux.ResizeHandle.None;

        if (Math.Abs(dxPt) < 0.01 && Math.Abs(dyPt) < 0.01)
            return;   // щелчок без перетаскивания — объект просто выбран

        var dragged = NexusPdf.Ux.ObjectHandles.Drag(_dragStartBox, handle, dxPt, dyPt);
        dragged = NexusPdf.Ux.Snapping.Apply(
            dragged, SnapToGrid, GridStepPt, Array.Empty<double>(), Array.Empty<double>());
        dragged = ClampToPage(dragged, selection.Page);

        var displayed = ToDisplayFrame(selection.Overlay, selection.Page);
        NexusPdf.Pdf.Abstractions.PageOverlay? updated;
        if (handle == NexusPdf.Ux.ResizeHandle.Move)
        {
            updated = NexusPdf.Pdf.Abstractions.OverlayGeometry.Moved(
                displayed, dragged.X - _dragStartBox.X, dragged.Y - _dragStartBox.Y);
        }
        else
        {
            updated = NexusPdf.Pdf.Abstractions.OverlayGeometry.Resized(displayed,
                new NexusPdf.Pdf.Abstractions.OverlayBox(
                    dragged.X, dragged.Y, dragged.Width, dragged.Height));
        }
        if (updated == null) return;

        // Оверлей возвращается в модель с ТЕКУЩЕЙ ориентацией страницы:
        // пользователь двигал его на повёрнутой странице, и запомнить надо
        // именно это положение.
        var stamped = updated with { PlacedRotation = selection.Page.PageRef.RotationOffset };
        Document.Session.Apply(new ReplaceOverlayOperation(
            selection.Page.LogicalIndex, selection.Overlay, stamped));

        ReselectAfterChange(selection.Page, selection.OverlayIndex);
        StatusText = Loc.Get(handle == NexusPdf.Ux.ResizeHandle.Move
            ? "UxObjectMoved" : "UxObjectResized");
    }

    public void CancelObjectDrag()
    {
        if (SelectedObject is { } selection)
            selection.Page.DragPreviewRect = null;
        _dragHandle = NexusPdf.Ux.ResizeHandle.None;
    }

    /// <summary>Заново выбрать объект по его месту в списке после правки модели.</summary>
    private void ReselectAfterChange(PageViewModel page, int overlayIndex)
    {
        ClearObjectSelection();
        var refreshed = Pages.FirstOrDefault(p => p.LogicalIndex == page.LogicalIndex) ?? page;
        var overlays = refreshed.PageRef.OverlayList;
        if (overlayIndex < 0 || overlayIndex >= overlays.Count) return;

        var displayed = ToDisplayFrame(overlays[overlayIndex], refreshed);
        if (NexusPdf.Pdf.Abstractions.OverlayGeometry.BoundsOf(displayed) is not { } box) return;
        SelectedObject = new ObjectSelection(refreshed, overlayIndex, overlays[overlayIndex], box);
        UpdateObjectFrame(box);
    }

    // ----- Команды над выбранным объектом -----

    public bool DeleteSelectedObject()
    {
        if (SelectedObject is not { } selection || IsBusy) return false;
        var page = selection.Page;
        var index = selection.OverlayIndex;
        ClearObjectSelection();
        Document.Session.Apply(new RemoveOverlayAtOperation(page.LogicalIndex, index));
        StatusText = Loc.Get("UxObjectDeleted");
        return true;
    }

    public bool DuplicateSelectedObject()
    {
        if (SelectedObject is not { } selection || IsBusy) return false;
        // Копия рядом, а не поверх оригинала: иначе непонятно, появилась она
        // вообще или нет.
        var displayed = ToDisplayFrame(selection.Overlay, selection.Page);
        var moved = NexusPdf.Pdf.Abstractions.OverlayGeometry.Moved(displayed, 12, 12) ?? displayed;
        Document.Session.Apply(new AddOverlayOperation(selection.Page.LogicalIndex, moved));
        StatusText = Loc.Get("UxObjectDuplicated");
        return true;
    }

    public bool MoveSelectedObjectInOrder(bool forward)
    {
        if (SelectedObject is not { } selection || IsBusy) return false;
        var count = selection.Page.PageRef.OverlayList.Count;
        var target = forward ? selection.OverlayIndex + 1 : selection.OverlayIndex - 1;
        if (target < 0 || target >= count) return false;

        Document.Session.Apply(new ReorderOverlayOperation(
            selection.Page.LogicalIndex, selection.OverlayIndex, target));
        ReselectAfterChange(selection.Page, target);
        StatusText = Loc.Get(forward ? "UxObjectForward" : "UxObjectBackward");
        return true;
    }

    /// <summary>Сдвиг выбранного объекта стрелками клавиатуры — точнее мыши.</summary>
    public bool NudgeSelectedObject(double dxPt, double dyPt)
    {
        if (SelectedObject is not { } selection || IsBusy) return false;
        var displayed = ToDisplayFrame(selection.Overlay, selection.Page);
        var clamped = ClampToPage(new NexusPdf.Ux.HandleBox(
            selection.Box.XPt + dxPt, selection.Box.YPt + dyPt,
            selection.Box.WidthPt, selection.Box.HeightPt), selection.Page);
        var moved = NexusPdf.Pdf.Abstractions.OverlayGeometry.Moved(
            displayed, clamped.X - selection.Box.XPt, clamped.Y - selection.Box.YPt);
        if (moved == null) return false;

        var stamped = moved with { PlacedRotation = selection.Page.PageRef.RotationOffset };
        Document.Session.Apply(new ReplaceOverlayOperation(
            selection.Page.LogicalIndex, selection.Overlay, stamped));
        ReselectAfterChange(selection.Page, selection.OverlayIndex);
        return true;
    }

    /// <summary>Описание выбранного объекта для окна свойств.</summary>
    public string DescribeSelectedObject()
    {
        if (SelectedObject is not { } selection)
            return Loc.Get("UxNoObjectSelection");

        var kindKey = selection.Overlay switch
        {
            NexusPdf.Pdf.Abstractions.TextOverlay => "UxObjectText",
            NexusPdf.Pdf.Abstractions.ImageOverlay => "UxObjectImage",
            NexusPdf.Pdf.Abstractions.NoteAnnotationDraft => "UxObjectNote",
            NexusPdf.Pdf.Abstractions.ShapeAnnotationDraft shape =>
                shape.IsEllipse ? "UxObjectEllipse" : "UxObjectRect",
            NexusPdf.Pdf.Abstractions.InkAnnotationDraft => "UxObjectInk",
            NexusPdf.Pdf.Abstractions.RedactionDraft => "UxObjectRedaction",
            NexusPdf.Pdf.Abstractions.TextMarkupDraft => "UxObjectMarkup",
            _ => "UxObjectOther",
        };

        var box = selection.Box;
        const double PtToMm = 25.4 / 72.0;
        return Loc.F("UxObjectProps",
            Loc.Get(kindKey),
            selection.Page.PageNumber,
            Math.Round(box.XPt * PtToMm, 1), Math.Round(box.YPt * PtToMm, 1),
            Math.Round(box.WidthPt * PtToMm, 1), Math.Round(box.HeightPt * PtToMm, 1),
            selection.OverlayIndex + 1, selection.Page.PageRef.OverlayList.Count);
    }

    // ----- Панель свойств выбранного объекта -----

    private const double PointToMm = 25.4 / 72.0;
    private bool _applyingObjectProperties;

    public string SelectedObjectTitle => SelectedObject?.Overlay switch
    {
        NexusPdf.Pdf.Abstractions.TextOverlay => Loc.Get("UxObjectText"),
        NexusPdf.Pdf.Abstractions.ImageOverlay => Loc.Get("UxObjectImage"),
        NexusPdf.Pdf.Abstractions.NoteAnnotationDraft => Loc.Get("UxObjectNote"),
        NexusPdf.Pdf.Abstractions.ShapeAnnotationDraft shape =>
            Loc.Get(shape.IsEllipse ? "UxObjectEllipse" : "UxObjectRect"),
        NexusPdf.Pdf.Abstractions.InkAnnotationDraft => Loc.Get("UxObjectInk"),
        NexusPdf.Pdf.Abstractions.RedactionDraft => Loc.Get("UxObjectRedaction"),
        NexusPdf.Pdf.Abstractions.TextMarkupDraft => Loc.Get("UxObjectMarkup"),
        _ => Loc.Get("UxObjectOther"),
    };

    /// <summary>Растягивается ли выбранный объект — от этого зависят поля размера.</summary>
    public bool CanResizeSelectedObject => SelectedObject is { } s &&
        NexusPdf.Pdf.Abstractions.OverlayGeometry.AbilitiesOf(ToDisplayFrame(s.Overlay, s.Page)).CanResize;

    public bool CanMoveSelectedObject => SelectedObject is { } s &&
        NexusPdf.Pdf.Abstractions.OverlayGeometry.AbilitiesOf(ToDisplayFrame(s.Overlay, s.Page)).CanMove;

    public string SelectedObjectOrderText => SelectedObject is { } s
        ? Loc.F("UxObjectOrder", s.OverlayIndex + 1, s.Page.PageRef.OverlayList.Count)
        : "";

    /// <summary>Положение и размер в миллиметрах: пользователь мыслит листом, а не пунктами.</summary>
    public double SelectedObjectXMm
    {
        get => SelectedObject is { } s ? Math.Round(s.Box.XPt * PointToMm, 1) : 0;
        set => ApplyObjectBox(x: value / PointToMm);
    }

    public double SelectedObjectYMm
    {
        get => SelectedObject is { } s ? Math.Round(s.Box.YPt * PointToMm, 1) : 0;
        set => ApplyObjectBox(y: value / PointToMm);
    }

    public double SelectedObjectWidthMm
    {
        get => SelectedObject is { } s ? Math.Round(s.Box.WidthPt * PointToMm, 1) : 0;
        set => ApplyObjectBox(width: value / PointToMm);
    }

    public double SelectedObjectHeightMm
    {
        get => SelectedObject is { } s ? Math.Round(s.Box.HeightPt * PointToMm, 1) : 0;
        set => ApplyObjectBox(height: value / PointToMm);
    }

    /// <summary>
    /// Применяет введённые в панели свойств числа. Каждое поле — отдельная
    /// операция отмены: пользователь правит их по одному, и откатываться они
    /// должны так же.
    /// </summary>
    private void ApplyObjectBox(
        double? x = null, double? y = null, double? width = null, double? height = null)
    {
        if (_applyingObjectProperties || SelectedObject is not { } selection || IsBusy) return;

        var box = selection.Box;
        var clamped = ClampToPage(new NexusPdf.Ux.HandleBox(
            x ?? box.XPt, y ?? box.YPt, width ?? box.WidthPt, height ?? box.HeightPt), selection.Page);
        var target = new NexusPdf.Pdf.Abstractions.OverlayBox(
            clamped.X, clamped.Y, clamped.Width, clamped.Height);
        if (Math.Abs(target.XPt - box.XPt) < 0.05 && Math.Abs(target.YPt - box.YPt) < 0.05 &&
            Math.Abs(target.WidthPt - box.WidthPt) < 0.05 && Math.Abs(target.HeightPt - box.HeightPt) < 0.05)
            return;

        var displayed = ToDisplayFrame(selection.Overlay, selection.Page);
        var sizeChanged = width != null || height != null;
        var updated = sizeChanged
            ? NexusPdf.Pdf.Abstractions.OverlayGeometry.Resized(displayed, target)
            : NexusPdf.Pdf.Abstractions.OverlayGeometry.Moved(
                displayed, target.XPt - box.XPt, target.YPt - box.YPt);
        if (updated == null)
        {
            // Объект не двигается или не растягивается: поле обязано вернуть
            // прежнее значение, а не сделать вид, что применилось.
            RaiseObjectPropertyChanged();
            return;
        }

        _applyingObjectProperties = true;
        try
        {
            var stamped = updated with { PlacedRotation = selection.Page.PageRef.RotationOffset };
            Document.Session.Apply(new ReplaceOverlayOperation(
                selection.Page.LogicalIndex, selection.Overlay, stamped));
            ReselectAfterChange(selection.Page, selection.OverlayIndex);
        }
        finally
        {
            _applyingObjectProperties = false;
        }
        RaiseObjectPropertyChanged();
    }

    private void RaiseObjectPropertyChanged()
    {
        OnPropertyChanged(nameof(SelectedObjectTitle));
        OnPropertyChanged(nameof(SelectedObjectXMm));
        OnPropertyChanged(nameof(SelectedObjectYMm));
        OnPropertyChanged(nameof(SelectedObjectWidthMm));
        OnPropertyChanged(nameof(SelectedObjectHeightMm));
        OnPropertyChanged(nameof(SelectedObjectOrderText));
        OnPropertyChanged(nameof(CanResizeSelectedObject));
        OnPropertyChanged(nameof(CanMoveSelectedObject));
    }

    partial void OnSelectedObjectChanged(ObjectSelection? value) => RaiseObjectPropertyChanged();

    [RelayCommand]
    private void DeleteObject() => DeleteSelectedObject();

    [RelayCommand]
    private void DuplicateObject() => DuplicateSelectedObject();

    [RelayCommand]
    private void BringObjectForward() => MoveSelectedObjectInOrder(forward: true);

    [RelayCommand]
    private void SendObjectBackward() => MoveSelectedObjectInOrder(forward: false);

    // ----- Сетка и привязка -----

    /// <summary>Привязывать перетаскивание к сетке (по умолчанию включено).</summary>
    [ObservableProperty]
    private bool _snapToGrid = true;

    [ObservableProperty]
    private double _gridStepPt = NexusPdf.Ux.Snapping.DefaultGridPt;

    /// <summary>Найти в документе то, что сейчас выделено (без ручного переноса в поле поиска).</summary>
    public async Task FindSelectedTextAsync()
    {
        var query = SelectedText.Trim();
        if (query.Length == 0) return;
        // Длинный кусок в поле поиска бесполезен: ищется первая строка.
        var firstLine = query.Split('\n', '\r').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? query;
        IsFindVisible = true;
        // Присваивание запускает поиск по мере ввода с паузой; здесь ждать
        // паузу незачем — запрос уже готов целиком.
        FindQuery = firstLine.Length > 120 ? firstLine[..120] : firstLine;
        await RunSearchAsync();
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

    /// <summary>
    /// Разовая загрузка строк текста страницы. Запрашивается ВЕСЬ текст сразу:
    /// движок отдаёт его слитыми строками (их десятки, а не тысячи), поэтому
    /// одна загрузка на страницу дешевле, чем вопрос на каждое движение мыши.
    /// </summary>
    public async Task EnsureTextAreasAsync(PageViewModel page)
    {
        if (page.TextAreasLoaded) return;
        page.TextAreasLoaded = true; // повторных попыток при ошибке не делаем
        try
        {
            var (handle, pageIndex) = await Document.ResolveTextPageAsync(
                page.LogicalIndex, CancellationToken.None);
            var text = await handle.GetPageTextAsync(pageIndex, CancellationToken.None);
            if (text.Length == 0)
            {
                page.TextAreas = Array.Empty<Rect>();
                return;
            }
            var rects = await handle.GetTextRectsAsync(pageIndex, 0, text.Length, CancellationToken.None);
            page.TextAreas = rects.Select(r => TransformRectToDiu(r, page)).ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось получить строки текста страницы {Page}", page.PageNumber);
            page.TextAreas = Array.Empty<Rect>();
        }
    }

    /// <summary>
    /// Есть ли текст под курсором. Строки чуть «подращиваются» по высоте:
    /// попадание точно в границу строки требовало бы снайперской точности, а
    /// мигание курсора между стрелкой и текстовым раздражает сильнее промаха.
    /// </summary>
    public bool HasTextAt(PageViewModel page, double xDiu, double yDiu)
    {
        const double slack = 2;
        foreach (var area in page.TextAreas)
        {
            if (xDiu >= area.X - slack && xDiu <= area.X + area.Width + slack &&
                yDiu >= area.Y - slack && yDiu <= area.Y + area.Height + slack)
                return true;
        }
        return false;
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

    /// <summary>Страницы ЧУЖОГО документа, брошенные сюда, и место вставки.</summary>
    public sealed record CrossDocumentDrop(
        DocumentViewModel Source, IReadOnlyList<int> Indices, int InsertIndex);

    /// <summary>
    /// Просьба перенести страницы из другого документа. Сам вид этого сделать
    /// не может — нужен движок, — поэтому решение принимает главная модель.
    /// </summary>
    public event EventHandler<CrossDocumentDrop>? PagesDroppedFromOtherDocument;

    private static IReadOnlyList<PageViewModel> DraggedPages(object? data) => data switch
    {
        PageViewModel single => new[] { single },
        IEnumerable<object> many => many.OfType<PageViewModel>().ToArray(),
        _ => Array.Empty<PageViewModel>(),
    };

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        var pages = DraggedPages(dropInfo.Data);
        if (pages.Count == 0) return;

        // Свои страницы переставляются, чужие — добавляются: у переноса между
        // документами это разные по смыслу действия, и курсор обязан показывать
        // именно то, что произойдёт.
        dropInfo.Effects = ReferenceEquals(pages[0].Owner, this)
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
        dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        var items = DraggedPages(dropInfo.Data);
        if (items.Count == 0) return;

        var insertIndex = Math.Clamp(dropInfo.InsertIndex, 0, Pages.Count);
        var source = items[0].Owner;

        // Чужие страницы нельзя переставлять своей операцией: её индексы
        // относятся к ЭТОМУ документу, и она перемешала бы совсем другие
        // страницы вместо вставки принесённых.
        if (!ReferenceEquals(source, this))
        {
            PagesDroppedFromOtherDocument?.Invoke(this, new CrossDocumentDrop(
                source,
                items.Where(p => ReferenceEquals(p.Owner, source))
                    .Select(p => p.LogicalIndex).OrderBy(i => i).ToList(),
                insertIndex));
            return;
        }

        var indices = items.Select(p => p.LogicalIndex).OrderBy(i => i).ToArray();
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
        var pt = TransformRectToDisplayPt(r, page);
        var scale = PtToDiu * Zoom;
        // Минимум 2 DIU: строка высотой в волосок иначе не видна на экране.
        return new Rect(pt.X * scale, pt.Y * scale,
            Math.Max(2, pt.Width * scale), Math.Max(2, pt.Height * scale));
    }

    /// <summary>
    /// Тот же перевод, но в ОТОБРАЖАЕМЫХ ПУНКТАХ страницы — системе координат
    /// оверлеев. Масштаб показа сюда не входит: разметка и вымарывание не
    /// должны зависеть от того, насколько документ увеличен на экране.
    /// </summary>
    private Rect TransformRectToDisplayPt(NexusPdf.Pdf.Abstractions.PdfTextRect r, PageViewModel page)
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

        var x1 = Math.Min(transformed[0].X, transformed[1].X);
        var y1 = Math.Min(transformed[0].Y, transformed[1].Y);
        var x2 = Math.Max(transformed[0].X, transformed[1].X);
        var y2 = Math.Max(transformed[0].Y, transformed[1].Y);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
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
        OnPropertyChanged(nameof(AccessibleName));
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

        var model = Document.Session.Model;
        var rebuilt = new List<PageViewModel>(model.Pages.Count);
        for (var i = 0; i < model.Pages.Count; i++)
            rebuilt.Add(new PageViewModel(this, i, model.Pages[i], Document.GetLogicalPageSize(i)));
        // Одно уведомление на весь список: триста отдельных добавлений держали
        // поток интерфейса секунду, и всё это время документ не показывался.
        var started = System.Diagnostics.Stopwatch.StartNew();
        Pages.ReplaceAll(rebuilt);
        if (rebuilt.Count > 50)
            Serilog.Log.Debug("Список из {Count} страниц собран за {Ms:N0} мс",
                rebuilt.Count, started.Elapsed.TotalMilliseconds);

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(PageOfText));
        OnPropertyChanged(nameof(CurrentPageSizeText));
        if (CurrentPageNumber > Pages.Count)
            CurrentPageNumber = Pages.Count;
        // Свойства страницы обновляются и при ПЕРЕСБОРКЕ (открытие документа,
        // поворот, удаление): без этого панель показывала бы вчерашние данные
        // или пустоту сразу после открытия.
        _ = RefreshPagePropertiesAsync();
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
