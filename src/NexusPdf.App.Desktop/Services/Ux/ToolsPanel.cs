using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using GongSolutions.Wpf.DragDrop;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>Группа панели инструментов: заголовок и кнопки под ним.</summary>
public sealed partial class ToolGroup : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required ObservableCollection<QuickPanelItem> Items { get; init; }

    /// <summary>
    /// Видимый состав группы. При поиске отсеивает не подошедшие кнопки, но
    /// НЕ трогает <see cref="Items"/> — перетаскивание и сохранение раскладки
    /// работают с настоящим списком.
    /// </summary>
    public ICollectionView View
    {
        get
        {
            _view ??= CollectionViewSource.GetDefaultView(Items);
            return _view;
        }
    }

    private ICollectionView? _view;

    /// <summary>Пустая группа при поиске прячется целиком — заголовок без кнопок бесполезен.</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Во время поиска разделы раскрыты: искать в свёрнутом бессмысленно.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    public void ApplyFilter(string query)
    {
        if (query.Length == 0)
        {
            View.Filter = null;
            IsVisible = true;
            return;
        }
        View.Filter = o => o is QuickPanelItem item && item.Matches(query);
        IsVisible = Items.Any(i => i.Matches(query));
        if (IsVisible) IsExpanded = true;
    }
}

/// <summary>
/// Панель инструментов: всё, что умеет программа, ВИДНО списком, а не спрятано
/// в меню.
///
/// Меню нужно, когда знаешь, что ищешь. Пока не знаешь — нужен список, в
/// котором видно и название, и значок, и то, что команда сейчас недоступна.
/// Строится из того же реестра, что панель и меню: третьего описания команд
/// не появляется.
///
/// Порядок команд задаёт пользователь перетаскиванием — своя пятёрка нужных
/// инструментов у каждого разная, и listать до неё каждый раз не должно быть
/// работой.
/// </summary>
public sealed partial class ToolsPanel : ObservableObject, IDropTarget
{
    private readonly UxCommandHub _hub;
    private readonly Action<string> _save;

    public ToolsPanel(UxCommandHub hub, string? savedLayout, Action<string> save)
    {
        _hub = hub;
        _save = save;

        var layout = ToolsLayout.Sanitize(
            ToolsLayout.FromSetting(savedLayout), id => hub.Registry.Find(id) != null);

        Groups = new ObservableCollection<ToolGroup>(layout.Select(g => new ToolGroup
        {
            Key = g.TitleKey,
            Title = Loc.Get(g.TitleKey),
            Items = new ObservableCollection<QuickPanelItem>(
                g.Commands.Select(id => new QuickPanelItem(hub, id))),
        }));
    }

    public ObservableCollection<ToolGroup> Groups { get; }

    /// <summary>
    /// Строка поиска по панели. Шестьдесят инструментов невозможно охватить
    /// глазом, а прокручивать до нужного — работа: набранное слово оставляет
    /// на экране только подходящее.
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            var query = (value ?? "").Trim();
            if (_filter == query) return;
            _filter = query;
            foreach (var group in Groups)
                group.ApplyFilter(query);
            OnPropertyChanged(nameof(Filter));
            OnPropertyChanged(nameof(HasFilter));
            OnPropertyChanged(nameof(NothingFound));
        }
    }

    private string _filter = "";

    public bool HasFilter => _filter.Length > 0;

    /// <summary>Ничего не нашлось — сказать об этом прямо, а не показывать пустоту.</summary>
    public bool NothingFound => HasFilter && Groups.All(g => !g.IsVisible);

    /// <summary>Сброс к раскладке по умолчанию.</summary>
    public void Reset()
    {
        var layout = ToolsLayout.Sanitize(null, id => _hub.Registry.Find(id) != null);
        Groups.Clear();
        foreach (var group in layout)
        {
            Groups.Add(new ToolGroup
            {
                Key = group.TitleKey,
                Title = Loc.Get(group.TitleKey),
                Items = new ObservableCollection<QuickPanelItem>(
                    group.Commands.Select(id => new QuickPanelItem(_hub, id))),
            });
        }
        SaveLayout();
    }

    private void SaveLayout() => _save(ToolsLayout.ToSetting(
        Groups.Select(g => new ToolsGroupLayout(g.Key, g.Items.Select(i => i.Id).ToList())).ToList()));

    // ----- Перетаскивание -----

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        if (dropInfo.Data is not QuickPanelItem) return;
        dropInfo.Effects = System.Windows.DragDropEffects.Move;
        dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        if (dropInfo.Data is not QuickPanelItem item) return;
        if (dropInfo.TargetCollection is not ObservableCollection<QuickPanelItem> target) return;

        var source = Groups.FirstOrDefault(g => g.Items.Contains(item))?.Items;
        if (source == null) return;

        var index = Math.Clamp(dropInfo.InsertIndex, 0, target.Count);
        if (ReferenceEquals(source, target))
        {
            var from = source.IndexOf(item);
            if (from < 0 || from == index) return;
            // Удаление сдвигает всё, что после, — цель считается уже без него.
            if (index > from) index--;
            source.Move(from, index);
        }
        else
        {
            source.Remove(item);
            target.Insert(index, item);
        }
        SaveLayout();
    }
}
