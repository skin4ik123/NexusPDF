using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services.Ux;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Строка палитры.</summary>
public sealed record PaletteRow(
    string Id,
    string Glyph,
    string Title,
    string Subtitle,
    string Shortcut,
    bool IsAvailable)
{
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool HasShortcut => Shortcut.Length > 0;

    /// <summary>Недоступное показывается бледнее, но остаётся читаемым и выбираемым.</summary>
    public double RowOpacity => IsAvailable ? 1.0 : 0.55;
}

/// <summary>
/// Палитра команд (Ctrl+K): поиск по всем командам программы с русскими
/// синонимами. Нужна ровно затем, чтобы не искать команду по вкладкам —
/// пользователь пишет «перевернуть» и получает «Повернуть вправо».
///
/// Недоступные команды НЕ прячутся: их показывают с причиной, потому что чаще
/// всего команду ищут именно тогда, когда она не работает.
/// </summary>
public partial class CommandPaletteDialog : Window
{
    private readonly UxCommandHub _hub;
    private readonly SelectionContext _context;

    private CommandPaletteDialog(UxCommandHub hub, SelectionContext context)
    {
        InitializeComponent();
        _hub = hub;
        _context = context;
        Loaded += (_, _) =>
        {
            Refresh();
            QueryBox.Focus();
        };
    }

    /// <summary>Показывает палитру и возвращает выбранный идентификатор команды или null.</summary>
    public static string? Pick(Window? owner, UxCommandHub hub, SelectionContext context)
    {
        var dialog = new CommandPaletteDialog(hub, context);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog._chosenId : null;
    }

    private string? _chosenId;

    private void Refresh()
    {
        var matches = _hub.Registry.Search(QueryBox.Text, _context, Loc.Get, limit: 60);
        var rows = matches.Select(m => new PaletteRow(
            m.Command.Id,
            m.Command.Glyph,
            UxCommandHub.Title(m.Command, _context),
            m.Availability.IsAvailable
                ? Loc.Get(m.Command.DescriptionKey) == m.Command.DescriptionKey
                    ? ""                                   // пояснения нет — строка остаётся короткой
                    : Loc.Get(m.Command.DescriptionKey)
                : UxCommandHub.Reason(m.Availability.ReasonKey),
            m.Command.Shortcut,
            m.Availability.IsAvailable)).ToList();

        ResultList.ItemsSource = rows;
        if (rows.Count > 0)
            ResultList.SelectedIndex = 0;

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = Loc.F("UxPaletteCount", rows.Count);
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Refresh();

    /// <summary>Стрелки управляют списком, не уводя курсор из поля ввода.</summary>
    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (ResultList.Items.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down:
                ResultList.SelectedIndex = Math.Min(ResultList.SelectedIndex + 1, ResultList.Items.Count - 1);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                ResultList.SelectedIndex = Math.Max(ResultList.SelectedIndex - 1, 0);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
        }
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Accept();
        e.Handled = true;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }

    /// <summary>Щелчок мимо окна закрывает палитру — как в любом быстром поиске.</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsLoaded && DialogResult == null)
            DialogResult = false;
    }

    private void Accept()
    {
        if (ResultList.SelectedItem is not PaletteRow row) return;
        _chosenId = row.Id;
        DialogResult = true;
    }
}
