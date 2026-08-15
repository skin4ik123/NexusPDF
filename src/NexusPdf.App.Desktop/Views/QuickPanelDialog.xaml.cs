using System.Windows;
using System.Windows.Controls;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services.Ux;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Что выбрал пользователь в настройке панели.</summary>
public sealed record QuickPanelSetup(IReadOnlyList<string> Commands, bool ShowLabels);

/// <summary>
/// Настройка быстрой панели: слева всё, что умеет программа, справа —
/// то, что вынесено на панель. Список слева строится из реестра команд,
/// поэтому новая команда появляется здесь сама, без правки этого окна.
/// </summary>
public partial class QuickPanelDialog : Window
{
    /// <summary>Строка списка: показывает название, а хранит идентификатор.</summary>
    private sealed record Row(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private readonly QuickPanel _panel;

    private QuickPanelDialog(QuickPanel panel, bool showLabels)
    {
        InitializeComponent();
        _panel = panel;
        LabelsBox.IsChecked = showLabels;

        AvailableList.ItemsSource = panel.Available()
            .Select(c => new Row(c.Id, Loc.Get(c.TitleKey)))
            .ToList();
        Fill(panel.Save());
    }

    public static QuickPanelSetup? Configure(Window? owner, QuickPanel panel, bool showLabels)
    {
        var dialog = new QuickPanelDialog(panel, showLabels);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        return dialog.ShowDialog() == true
            ? new QuickPanelSetup(
                dialog.ChosenList.Items.OfType<Row>().Select(r => r.Id).ToList(),
                dialog.LabelsBox.IsChecked == true)
            : null;
    }

    private void Fill(IEnumerable<string> ids)
    {
        ChosenList.Items.Clear();
        foreach (var id in ids)
            ChosenList.Items.Add(ToRow(id));
    }

    private Row ToRow(string id) => id == QuickPanelItem.SeparatorId
        ? new Row(id, Loc.Get("UxQuickPanelSeparatorItem"))
        : new Row(id, Loc.Get(_panel.Available().First(c => c.Id == id).TitleKey));

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (AvailableList.SelectedItem is not Row row) return;
        // Одну и ту же команду дважды на панель класть незачем.
        if (ChosenList.Items.OfType<Row>().Any(r => r.Id == row.Id)) return;
        var index = ChosenList.SelectedIndex;
        ChosenList.Items.Insert(index >= 0 ? index + 1 : ChosenList.Items.Count, row);
    }

    private void OnAddSeparator(object sender, RoutedEventArgs e)
    {
        var index = ChosenList.SelectedIndex;
        var row = ToRow(QuickPanelItem.SeparatorId);
        ChosenList.Items.Insert(index >= 0 ? index + 1 : ChosenList.Items.Count, row);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ChosenList.SelectedIndex < 0) return;
        ChosenList.Items.RemoveAt(ChosenList.SelectedIndex);
    }

    private void OnUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        var index = ChosenList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ChosenList.Items.Count) return;
        var item = ChosenList.Items[index];
        ChosenList.Items.RemoveAt(index);
        ChosenList.Items.Insert(target, item);
        ChosenList.SelectedIndex = target;
    }

    private void OnReset(object sender, RoutedEventArgs e) => Fill(QuickPanel.Default);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // Пустая панель — не настройка, а потерянные кнопки: молча заменять её
        // нельзя, но и выпускать пустую тоже.
        if (ChosenList.Items.OfType<Row>().All(r => r.Id == QuickPanelItem.SeparatorId))
        {
            InfoDialog.Show(this, Loc.Get("UxQuickPanelTitle"), Loc.Get("UxQuickPanelEmpty"));
            return;
        }
        DialogResult = true;
    }
}
