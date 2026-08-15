using System.Windows;
using System.Windows.Controls;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services.Ux;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Сборка контекстного меню WPF из реестра команд.
///
/// Меню не пишутся в разметке: тогда они неизбежно расходятся с панелями в
/// названиях, доступности и горячих клавишах. Здесь пункт получает название,
/// значок, сочетание клавиш и причину недоступности из ОДНОГО дескриптора.
/// </summary>
public static class UxContextMenu
{
    /// <summary>Меню для текущего выделения или null, если показывать нечего.</summary>
    public static ContextMenu? Build(UxCommandHub hub, UxTarget target)
    {
        var items = hub.Menus.Compose(target.Context);
        if (items.Count == 0)
            return null;

        var menu = new ContextMenu();
        foreach (var item in items)
        {
            if (item.IsSeparatorBefore)
                menu.Items.Add(new Separator());

            var command = item.Command;
            var entry = new MenuItem
            {
                Header = UxCommandHub.Title(command, target.Context),
                InputGestureText = command.Shortcut,
                IsEnabled = item.Availability.IsAvailable,
            };

            if (command.Glyph.Length > 0)
                entry.Icon = new TextBlock
                {
                    Text = command.Glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                };

            // Выключенный пункт обязан объяснять себя, а подсказка на
            // выключенном элементе по умолчанию не показывается вовсе.
            var hint = item.Availability.IsAvailable
                ? Describe(command.DescriptionKey)
                : UxCommandHub.Reason(item.Availability.ReasonKey);
            if (hint.Length > 0)
            {
                entry.ToolTip = hint;
                ToolTipService.SetShowOnDisabled(entry, true);
            }

            var id = command.Id;
            entry.Click += (_, _) => hub.Invoke(id, target);
            menu.Items.Add(entry);
        }
        return menu;
    }

    /// <summary>
    /// Показывает меню у элемента; ничего не делает, если меню пустое.
    /// </summary>
    /// <param name="at">
    /// Точка внутри <paramref name="placementTarget"/>. Задаётся для касания и
    /// пера: у них нет положения мыши, и меню иначе выскакивает там, где в
    /// последний раз был курсор.
    /// </param>
    public static bool Show(
        UxCommandHub hub, UxTarget target, FrameworkElement placementTarget, Point? at = null)
    {
        var menu = Build(hub, target);
        if (menu == null)
            return false;
        menu.PlacementTarget = placementTarget;
        if (at is { } point)
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
            menu.HorizontalOffset = point.X;
            menu.VerticalOffset = point.Y;
        }
        menu.IsOpen = true;
        return true;
    }

    private static string Describe(string descriptionKey)
    {
        if (descriptionKey.Length == 0) return "";
        var text = Loc.Get(descriptionKey);
        // Loc возвращает сам ключ, когда строки нет: показывать латинский
        // идентификатор пользователю нельзя.
        return text == descriptionKey ? "" : text;
    }
}
