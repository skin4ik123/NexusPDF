using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services.Ux;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Главное меню программы: разделы с подменю вместо простыни из двадцати пяти
/// пунктов подряд.
///
/// Собирается из реестра команд и <see cref="AppMenuTree"/>, поэтому пункт
/// меню не может разойтись с одноимённой кнопкой панели, а новая команда
/// появляется здесь вместе со своим разделом, а не отдельной строчкой в конце.
/// </summary>
public static class AppMenuFactory
{
    public static ContextMenu Build(MainViewModel main)
    {
        var hub = main.Ux;
        var context = hub.Snapshot();
        var menu = new ContextMenu { MinWidth = 260 };

        foreach (var section in AppMenuTree.Sections)
        {
            var item = new MenuItem { Header = Loc.Get(section.TitleKey) };
            foreach (var id in section.CommandIds)
            {
                if (hub.Registry.Find(id) is not { } command) continue;
                item.Items.Add(CommandItem(hub, command, context));
            }
            if (item.Items.Count > 0)
                menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(ViewMenu(main));
        menu.Items.Add(SettingsMenu(main));
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem(hub, hub.Registry.Require(CommandIds.Support), context));
        menu.Items.Add(CommandItem(hub, hub.Registry.Require(CommandIds.About), context));
        return menu;
    }

    private static MenuItem CommandItem(
        UxCommandHub hub, CommandDescriptor command, SelectionContext context)
    {
        var availability = command.Evaluate(context);
        var item = new MenuItem
        {
            Header = UxCommandHub.Title(command, context),
            InputGestureText = command.Shortcut,
            IsEnabled = availability.IsAvailable,
        };
        if (command.Glyph.Length > 0)
            item.Icon = new TextBlock
            {
                Text = command.Glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
            };
        if (!availability.IsAvailable)
        {
            item.ToolTip = UxCommandHub.Reason(availability.ReasonKey);
            ToolTipService.SetShowOnDisabled(item, true);
        }

        var id = command.Id;
        item.Click += (_, _) => hub.Invoke(id, new UxTarget
        {
            Context = hub.Snapshot(),
            Document = hub.ActiveDocument,
        });
        return item;
    }

    /// <summary>«Вид»: панели, рабочие пространства, размеры элементов.</summary>
    private static MenuItem ViewMenu(MainViewModel main)
    {
        var view = new MenuItem { Header = Loc.Get(AppMenuTree.ViewSectionKey) };

        var panels = new MenuItem { Header = Loc.Get("MenuPanels") };
        foreach (var (panel, key) in new (UiPanel, string)[]
                 {
                     (UiPanel.QuickPanel, "PanelQuick"),
                     (UiPanel.ToolRail, "PanelRail"),
                     (UiPanel.SidePanel, "PanelSide"),
                     (UiPanel.Tools, "PanelTools"),
                     (UiPanel.Comments, "PanelComments"),
                     (UiPanel.Properties, "PanelProperties"),
                     (UiPanel.StatusBar, "PanelStatus"),
                 })
        {
            var current = panel;
            panels.Items.Add(new MenuItem
            {
                Header = Loc.Get(key),
                IsCheckable = true,
                IsChecked = main.Panels.IsVisible(current),
                StaysOpenOnClick = true,
            }.WithClick(item => main.TogglePanel(current)));
        }
        panels.Items.Add(new Separator());
        panels.Items.Add(new MenuItem
        {
            Header = Loc.Get("PanelOnlyPage"),
            InputGestureText = "Ctrl+F11",
            IsCheckable = true,
            IsChecked = main.Panels.IsPageOnly,
        }.WithClick(_ => main.TogglePageOnlyCommand.Execute(null)));
        view.Items.Add(panels);

        var workspaces = new MenuItem { Header = Loc.Get("UxWorkspace") };
        foreach (var profile in WorkspaceProfile.All)
        {
            var id = profile.Id;
            workspaces.Items.Add(new MenuItem
            {
                Header = Loc.Get(profile.TitleKey),
                IsCheckable = true,
                IsChecked = string.Equals(main.CurrentWorkspace, id, StringComparison.Ordinal),
            }.WithClick(_ => main.ApplyWorkspaceCommand.Execute(id)));
        }
        view.Items.Add(workspaces);

        view.Items.Add(new Separator());
        foreach (var id in new[]
                 {
                     CommandIds.ZoomIn, CommandIds.ZoomOut, CommandIds.ZoomActual,
                     CommandIds.FitWidth, CommandIds.FitPage,
                 })
        {
            if (main.Ux.Registry.Find(id) is { } command)
                view.Items.Add(CommandItem(main.Ux, command, main.Ux.Snapshot()));
        }
        return view;
    }

    private static MenuItem SettingsMenu(MainViewModel main)
    {
        var settings = new MenuItem { Header = Loc.Get(AppMenuTree.SettingsSectionKey) };

        var density = new MenuItem { Header = Loc.Get("UxDensity") };
        foreach (var (value, key) in new[]
                 {
                     ("auto", "UxDensityAuto"), ("compact", "UxDensityCompact"),
                     ("comfortable", "UxDensityComfortable"), ("touch", "UxDensityTouch"),
                 })
        {
            var setting = value;
            density.Items.Add(new MenuItem
            {
                Header = Loc.Get(key),
                IsCheckable = true,
                IsChecked = string.Equals(main.CurrentDensity, setting, StringComparison.Ordinal),
            }.WithClick(_ => main.SetDensityCommand.Execute(setting)));
        }
        settings.Items.Add(density);

        var theme = new MenuItem { Header = Loc.Get("Theme") };
        foreach (var (value, key) in new[]
                 {
                     ("light", "ThemeLight"), ("dark", "ThemeDark"), ("system", "ThemeSystem"),
                 })
        {
            var setting = value;
            theme.Items.Add(new MenuItem
            {
                Header = Loc.Get(key),
                IsCheckable = true,
                IsChecked = string.Equals(main.CurrentTheme, setting, StringComparison.Ordinal),
            }.WithClick(_ => main.SetThemeCommand.Execute(setting)));
        }
        settings.Items.Add(theme);

        var language = new MenuItem { Header = Loc.Get("Language") };
        // Название языка пишется НА НЁМ САМОМ: человек, попавший в чужой
        // интерфейс, ищет знакомое слово, а не перевод названия своего языка.
        foreach (var (value, title) in new[] { ("en", "English"), ("ru", "Русский"), ("uk", "Українська") })
        {
            var setting = value;
            language.Items.Add(new MenuItem
            {
                Header = title,
                IsCheckable = true,
                IsChecked = string.Equals(Loc.CurrentLanguage, setting, StringComparison.Ordinal),
            }.WithClick(_ => main.SetLanguageCommand.Execute(setting)));
        }
        settings.Items.Add(language);

        settings.Items.Add(new Separator());
        // Обе настройки уже были в файле настроек и влияли на поведение, но
        // менять их можно было только руками в JSON — для пользователя их
        // попросту не существовало.
        settings.Items.Add(new MenuItem
        {
            Header = Loc.Get("SettingKeepBackup"),
            ToolTip = Loc.Get("SettingKeepBackupHint"),
            IsCheckable = true,
            IsChecked = main.KeepBackupOnSave,
        }.WithClick(_ => main.ToggleKeepBackupCommand.Execute(null)));
        settings.Items.Add(new MenuItem
        {
            Header = Loc.Get("SettingSingleInstance"),
            ToolTip = Loc.Get("SettingSingleInstanceHint"),
            IsCheckable = true,
            IsChecked = main.SingleInstance,
        }.WithClick(_ => main.ToggleSingleInstanceCommand.Execute(null)));

        settings.Items.Add(new Separator());
        settings.Items.Add(new MenuItem { Header = Loc.Get("UxQuickPanelMenu") }
            .WithClick(_ => main.ConfigureQuickPanelCommand.Execute(null)));
        return settings;
    }

    private static MenuItem WithClick(this MenuItem item, Action<MenuItem> action)
    {
        item.Click += (_, _) => action(item);
        return item;
    }
}
