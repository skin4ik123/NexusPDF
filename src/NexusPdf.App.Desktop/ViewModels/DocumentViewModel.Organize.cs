using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using GongSolutions.Wpf.DragDrop;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Организатор страниц: размер миниатюр и перетаскивание выбранных страниц
/// наружу — в папку Проводника.
/// </summary>
public sealed partial class DocumentViewModel
{
    // ----- Размер миниатюр -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrganizeThumbHeight))]
    [NotifyPropertyChangedFor(nameof(ThumbPixelWidth))]
    private double _organizeThumbWidth = ThumbnailSizing.DefaultWidth;

    public double OrganizeThumbHeight => Math.Round(OrganizeThumbWidth * ThumbnailSizing.HeightFactor);

    /// <summary>Ширина растра миниатюры под текущий размер карточки и плотность экрана.</summary>
    public int ThumbPixelWidth => ThumbnailSizing.PixelWidth(OrganizeThumbWidth, DpiScale);

    /// <summary>Размер миниатюр устоялся (ползунок отпущен) — его пора запомнить.</summary>
    public event EventHandler? ThumbSizeSettled;

    private System.Windows.Threading.DispatcherTimer? _thumbSizeTimer;

    partial void OnOrganizeThumbWidthChanged(double value)
    {
        var clamped = ThumbnailSizing.Clamp(value);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            OrganizeThumbWidth = clamped;
            return;
        }
        foreach (var page in Pages)
            page.NotifyThumbSizeChanged();

        // Растры перерисовываются с паузой: ползунок и Ctrl+колесо меняют
        // размер сериями, и рисовать каждый промежуточный шаг — пустая работа.
        if (_thumbSizeTimer == null)
        {
            _thumbSizeTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _thumbSizeTimer.Tick += (_, _) =>
            {
                _thumbSizeTimer.Stop();
                foreach (var page in Pages)
                    page.RefreshThumbnailIfStale();
                ThumbSizeSettled?.Invoke(this, EventArgs.Empty);
            };
        }
        _thumbSizeTimer.Stop();
        _thumbSizeTimer.Start();
    }

    // ----- Выбор страниц в организаторе -----

    /// <summary>Выбор после следующей пересборки страниц; null — перенести по номерам.</summary>
    private IReadOnlyCollection<int>? _organizeSelectionAfterRebuild;

    /// <summary>Идёт пересборка: флажки выбора новых карточек ставятся пачкой, без пересчёта на каждую.</summary>
    private bool _rebuildingPages;

    /// <summary>Список страниц вот-вот заменится (представление запоминает прокрутку и фокус).</summary>
    public event EventHandler? PagesRebuilding;

    /// <summary>Список страниц заменён (представление возвращает прокрутку и фокус).</summary>
    public event EventHandler? PagesRebuilt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrganizeSelection))]
    [NotifyPropertyChangedFor(nameof(OrganizeSelectionText))]
    private int _organizeSelectionCount;

    public bool HasOrganizeSelection => OrganizeSelectionCount > 0;

    /// <summary>«Выбрано 3 из 12» — чтобы было видно, к чему применится команда.</summary>
    public string OrganizeSelectionText => OrganizeSelectionCount == 0
        ? Loc.F("OrganizeNoneSelected", Pages.Count)
        : Loc.F("OrganizeSelectedCount", OrganizeSelectionCount, Pages.Count);

    public IEnumerable<PageViewModel> OrganizeSelectedPages => Pages.Where(p => p.IsOrganizeSelected);

    internal void OnOrganizeSelectionChanged()
    {
        if (_rebuildingPages) return;
        RecountOrganizeSelection();
    }

    /// <summary>Пока документ занят, команды организатора недоступны — и кнопки это показывают.</summary>
    partial void OnIsBusyChanged(bool value) => RecountOrganizeSelection();

    private void RecountOrganizeSelection()
    {
        OrganizeSelectionCount = Pages.Count(p => p.IsOrganizeSelected);
        OnPropertyChanged(nameof(OrganizeSelectionText));
        // Кнопки организатора гаснут, когда выбирать не из чего, — вместо того
        // чтобы молча ничего не делать.
        RotateSelectedCommand.NotifyCanExecuteChanged();
        RotateSelectedLeftCommand.NotifyCanExecuteChanged();
        RotateSelected180Command.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Выбрать страницы по номерам (с нуля), остальные снять.</summary>
    public void SelectOrganizePages(IEnumerable<int> indices)
    {
        var set = indices.ToHashSet();
        _rebuildingPages = true;
        try
        {
            foreach (var page in Pages)
                page.IsOrganizeSelected = set.Contains(page.LogicalIndex);
        }
        finally
        {
            _rebuildingPages = false;
        }
        RecountOrganizeSelection();
    }

    // ----- Перетаскивание страниц наружу -----

    /// <summary>
    /// Готовит PDF из указанных страниц для переноса в Проводник и возвращает
    /// путь к нему; null — файл сделать не удалось (тогда перетаскивание
    /// работает только внутри программы). Ставит главная модель: ей известен
    /// сервис сохранения.
    /// </summary>
    public Func<IReadOnlyList<int>, string?>? DragFileFactory { get; set; }

    private IDragSource? _dragHandler;

    /// <summary>Источник перетаскивания для организатора (gong-wpf-dragdrop).</summary>
    public IDragSource DragHandler => _dragHandler ??= new PageDragHandler(this);

    /// <summary>
    /// Перетаскивание страниц из организатора. Внутри программы это обычная
    /// перестановка (данные библиотеки), а для Проводника к тем же данным
    /// добавляется настоящий файл: страницы, брошенные в папку, становятся
    /// PDF-документом. Файл собирается ДО начала перетаскивания — Проводник
    /// копирует его в момент броска, и к этому моменту он обязан существовать.
    /// </summary>
    private sealed class PageDragHandler(DocumentViewModel owner) : DefaultDragHandler
    {
        private bool _droppedInside;
        private string? _file;
        private IReadOnlyList<int> _indices = Array.Empty<int>();

        public override void StartDrag(IDragInfo dragInfo)
        {
            base.StartDrag(dragInfo);
            _droppedInside = false;
            _file = null;
            _indices = Array.Empty<int>();

            var pages = DraggedPages(dragInfo.Data);
            if (pages.Count == 0 || pages.Any(p => !ReferenceEquals(p.Owner, owner)))
                return;
            _indices = pages.Select(p => p.LogicalIndex).Distinct().OrderBy(i => i).ToList();

            var path = owner.DragFileFactory?.Invoke(_indices);
            if (path == null)
                return;

            var data = new DataObject();
            data.SetData(GongSolutions.Wpf.DragDrop.DragDrop.DataFormat.Name, dragInfo.Data);
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            dragInfo.DataObject = data;
            dragInfo.Effects = DragDropEffects.Copy | DragDropEffects.Move;
            _file = path;
        }

        public override void Dropped(IDropInfo dropInfo)
        {
            _droppedInside = true;
            base.Dropped(dropInfo);
        }

        public override void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo)
        {
            base.DragDropOperationFinished(operationResult, dragInfo);
            // Бросок мимо собственных списков с ненулевым результатом — это
            // Проводник (или другая программа), принявший файл.
            if (!_droppedInside && _file != null && operationResult != DragDropEffects.None)
                owner.StatusText = Loc.F("DragOutDone", PageRanges.Format(_indices), Path.GetFileName(_file));
        }
    }
}
