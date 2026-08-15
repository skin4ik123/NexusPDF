using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>
/// Кнопка быстрой панели. Название, значок, подсказка и доступность берутся из
/// того же дескриптора, что и одноимённый пункт меню, — панель не может
/// разойтись с меню, потому что описание у них одно.
/// </summary>
public sealed partial class QuickPanelItem : ObservableObject
{
    private readonly UxCommandHub _hub;
    private readonly CommandDescriptor? _command;

    /// <summary>Разделитель между группами кнопок.</summary>
    public const string SeparatorId = QuickPanelLayout.Separator;

    public QuickPanelItem(UxCommandHub hub, string id)
    {
        _hub = hub;
        Id = id;
        IsSeparator = id == SeparatorId;
        _command = IsSeparator ? null : hub.Registry.Find(id);
        Invoke = new RelayCommandAdapter(this);
        Refresh();
    }

    public string Id { get; }
    public bool IsSeparator { get; }
    public bool IsKnown => IsSeparator || _command != null;

    public string Glyph => _command?.Glyph ?? "";
    public string Title => _command == null ? Id : Loc.Get(_command.TitleKey);

    public ICommand Invoke { get; }

    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _tooltip = "";

    /// <summary>
    /// Пересчёт доступности. Вызывается по общему сигналу WPF о том, что
    /// состояние могло измениться, — отдельного «обнови панель» в двадцати
    /// местах быть не должно.
    /// </summary>
    public void Refresh()
    {
        if (_command == null) return;
        var context = _hub.Snapshot();
        var availability = _command.Evaluate(context);
        IsAvailable = availability.IsAvailable;

        var hint = UxCommandHub.Title(_command, context);
        if (_command.Shortcut.Length > 0)
            hint += $" ({_command.Shortcut})";
        // Выключенная кнопка обязана объяснять себя: молчащая — худший вид
        // интерфейса, потому что непонятно, что исправить.
        if (!availability.IsAvailable)
            hint += "\n" + UxCommandHub.Reason(availability.ReasonKey);
        Tooltip = hint;
    }

    private void Execute()
    {
        if (_command == null) return;
        _hub.Invoke(_command.Id, new UxTarget
        {
            Context = _hub.Snapshot(),
            Document = _hub.ActiveDocument,
        });
    }

    /// <summary>Обёртка ICommand: доступность спрашивается у реестра, а не хранится копией.</summary>
    private sealed class RelayCommandAdapter : ICommand
    {
        private readonly QuickPanelItem _item;

        public RelayCommandAdapter(QuickPanelItem item) => _item = item;

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter)
        {
            _item.Refresh();
            return _item.IsAvailable;
        }

        public void Execute(object? parameter) => _item.Execute();
    }
}

/// <summary>
/// Быстрая панель: набор команд, который пользователь складывает сам.
///
/// Смысл ровно один — у разных людей разные пять команд, которыми они
/// пользуются весь день. Панель, которую нельзя сложить под себя, всегда
/// содержит чужие кнопки.
/// </summary>
public sealed class QuickPanel
{
    private readonly UxCommandHub _hub;

    public QuickPanel(UxCommandHub hub)
    {
        _hub = hub;
        Items = new ObservableCollection<QuickPanelItem>();
    }

    public ObservableCollection<QuickPanelItem> Items { get; }

    /// <summary>Панель по умолчанию — то, чем пользуются в первый день.</summary>
    public static IReadOnlyList<string> Default => QuickPanelLayout.Default;

    /// <summary>
    /// Читает список из настроек. Чистка (неизвестные команды, повторы,
    /// висящие разделители) живёт в <see cref="QuickPanelLayout"/> и покрыта
    /// тестами: именно она спасает панель после обновления программы.
    /// </summary>
    public void Load(IEnumerable<string>? ids)
    {
        Items.Clear();
        var clean = QuickPanelLayout.Sanitize(ids, id => _hub.Registry.Find(id) != null);
        foreach (var id in clean)
            Items.Add(new QuickPanelItem(_hub, id));
    }

    public List<string> Save() => Items.Select(i => i.Id).ToList();

    /// <summary>Команды, которые имеет смысл вынести на панель.</summary>
    public IReadOnlyList<CommandDescriptor> Available() => _hub.Registry.All
        .OrderBy(c => c.Category)
        .ThenBy(c => Loc.Get(c.TitleKey), StringComparer.CurrentCulture)
        .ToList();
}
