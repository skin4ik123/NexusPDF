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

    /// <summary>Кому сообщить, что раздел свернули или раскрыли.</summary>
    public Action? ExpansionChanged { get; set; }

    /// <summary>Сколько кнопок в разделе — видно и на свёрнутом заголовке.</summary>
    public int Count => Items.Count;

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

    /// <summary>
    /// Раскрыт ли раздел. По умолчанию НЕТ: восемь раскрытых списков подряд —
    /// это полсотни кнопок в узкой колонке, по которой надо прокручивать до
    /// нужного. Свёрнутые заголовки видны целиком, и до раздела доходишь
    /// одним взглядом.
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        // Во время поиска раскрытие временное и запоминать его нельзя.
        if (_filtering) return;
        _userExpanded = value;
        ExpansionChanged?.Invoke();
    }

    private bool _filtering;
    private bool _userExpanded;

    public void ApplyFilter(string query)
    {
        _filtering = true;
        try
        {
            if (query.Length == 0)
            {
                View.Filter = null;
                IsVisible = true;
                IsExpanded = _userExpanded; // возвращаем то, что выбрал человек
                return;
            }
            View.Filter = o => o is QuickPanelItem item && item.Matches(query);
            IsVisible = Items.Any(i => i.Matches(query));
            // Искать в свёрнутом разделе бессмысленно: найденное надо показать.
            if (IsVisible) IsExpanded = true;
        }
        finally
        {
            _filtering = false;
        }
    }

    /// <summary>Восстановление сохранённого состояния без записи его же обратно.</summary>
    public void RestoreExpanded(bool expanded)
    {
        _filtering = true;
        try
        {
            _userExpanded = expanded;
            IsExpanded = expanded;
        }
        finally
        {
            _filtering = false;
        }
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
    private readonly Action<IReadOnlyList<string>>? _saveRecent;
    private readonly Action<string>? _saveExpanded;
    private IReadOnlyList<string> _recent = Array.Empty<string>();
    private HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>Раздел «Недавние» живёт отдельно от раскладки: его не переставляют руками.</summary>
    private ToolGroup? _recentGroup;

    public ToolsPanel(UxCommandHub hub, string? savedLayout, Action<string> save,
        IReadOnlyList<string>? recent = null, Action<IReadOnlyList<string>>? saveRecent = null,
        string? expandedGroups = null, Action<string>? saveExpanded = null)
    {
        _hub = hub;
        _save = save;
        _saveRecent = saveRecent;
        _saveExpanded = saveExpanded;
        _recent = RecentCommands.Sanitize(recent, id => hub.Registry.Find(id) != null);
        _expanded = new HashSet<string>(
            (expandedGroups ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        var layout = ToolsLayout.Sanitize(
            ToolsLayout.FromSetting(savedLayout), id => hub.Registry.Find(id) != null);

        Groups = new ObservableCollection<ToolGroup>(layout.Select(g => Build(g.TitleKey, g.Commands)));
        BuildRecentGroup();
    }

    private ToolGroup Build(string key, IEnumerable<string> commands)
    {
        var group = new ToolGroup
        {
            Key = key,
            Title = Loc.Get(key),
            Items = new ObservableCollection<QuickPanelItem>(
                commands.Select(id => new QuickPanelItem(_hub, id))),
        };
        group.RestoreExpanded(_expanded.Contains(key));
        group.ExpansionChanged = () => SaveExpanded(group);
        return group;
    }

    private void SaveExpanded(ToolGroup group)
    {
        if (group.IsExpanded) _expanded.Add(group.Key);
        else _expanded.Remove(group.Key);
        _saveExpanded?.Invoke(string.Join(";", _expanded));
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

    /// <summary>
    /// Отметить использование инструмента. Зовётся из общей точки исполнения
    /// команд, поэтому в «недавнее» попадает и то, что вызвано горячей
    /// клавишей или из меню, а не только клик по панели.
    /// </summary>
    public void NoteUsed(string commandId)
    {
        if (_hub.Registry.Find(commandId) == null) return;
        var updated = RecentCommands.Use(_recent, commandId);
        if (updated.SequenceEqual(_recent)) return;
        _recent = updated;
        _saveRecent?.Invoke(_recent);
        BuildRecentGroup();
    }

    private void BuildRecentGroup()
    {
        if (!RecentCommands.WorthShowing(_recent))
        {
            if (_recentGroup != null)
            {
                Groups.Remove(_recentGroup);
                _recentGroup = null;
            }
            return;
        }

        if (_recentGroup == null)
        {
            _recentGroup = Build("PanelToolsRecent", _recent);
            Groups.Insert(0, _recentGroup);
            return;
        }
        _recentGroup.Items.Clear();
        foreach (var id in _recent) _recentGroup.Items.Add(new QuickPanelItem(_hub, id));
    }

    /// <summary>Сброс к раскладке по умолчанию.</summary>
    public void Reset()
    {
        var layout = ToolsLayout.Sanitize(null, id => _hub.Registry.Find(id) != null);
        Groups.Clear();
        _recentGroup = null;
        foreach (var group in layout)
            Groups.Add(Build(group.TitleKey, group.Commands));
        BuildRecentGroup();
        SaveLayout();
    }

    private void SaveLayout() => _save(ToolsLayout.ToSetting(
        Groups.Where(g => !ReferenceEquals(g, _recentGroup))
            .Select(g => new ToolsGroupLayout(g.Key, g.Items.Select(i => i.Id).ToList()))
            .ToList()));

    /// <summary>
    /// Перестановка самого РАЗДЕЛА. Порядок разделов — это порядок работы: у
    /// кого-то каждый день сканы, у кого-то комментарии, и держать нужное внизу
    /// списка неудобно одинаково для всех.
    /// </summary>
    public void MoveGroup(ToolGroup group, int insertIndex)
    {
        if (ReferenceEquals(group, _recentGroup)) return;

        var from = Groups.IndexOf(group);
        if (from < 0) return;
        var index = Math.Clamp(insertIndex, 0, Groups.Count);
        // «Недавнее» приколото сверху: выше него ничего не встаёт.
        var floor = _recentGroup != null ? 1 : 0;
        index = Math.Max(index, floor);
        if (index > from) index--;
        if (index == from) return;

        Groups.Move(from, index);
        SaveLayout();
    }

    // ----- Перетаскивание -----

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        switch (dropInfo.Data)
        {
            // «Недавнее» — зеркало последних действий, а не раздел раскладки:
            // его место наверху, и переставлять его бессмысленно.
            case ToolGroup group when !ReferenceEquals(group, _recentGroup):
            case QuickPanelItem:
                dropInfo.Effects = System.Windows.DragDropEffects.Move;
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                break;
        }
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        if (dropInfo.Data is ToolGroup dragged)
        {
            MoveGroup(dragged, dropInfo.InsertIndex);
            return;
        }
        if (dropInfo.Data is not QuickPanelItem item) return;
        if (dropInfo.TargetCollection is not ObservableCollection<QuickPanelItem> target) return;

        var sourceGroup = Groups.FirstOrDefault(g => g.Items.Contains(item));
        // «Недавнее» — зеркало, а не раскладка: тащить из него и в него нечего.
        if (sourceGroup == null || ReferenceEquals(sourceGroup, _recentGroup) ||
            ReferenceEquals(target, _recentGroup?.Items))
            return;
        var source = sourceGroup.Items;

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
