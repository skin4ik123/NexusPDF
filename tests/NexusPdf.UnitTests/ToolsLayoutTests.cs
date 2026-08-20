using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Расположение инструментов, которое пользователь собрал сам. Главное здесь —
/// что оно переживает обновление программы: команда, появившаяся в новой
/// версии, обязана показаться сама, а исчезнувшая — не сломать панель.
/// </summary>
public sealed class ToolsLayoutTests
{
    private static readonly CommandRegistry Registry = AppCommands.Build();

    private static bool Known(string id) => Registry.Find(id) != null;

    private static IReadOnlyList<string> Ids(IReadOnlyList<ToolsGroupLayout> groups) =>
        groups.SelectMany(g => g.Commands).ToList();

    [Fact]
    public void Without_A_Saved_Layout_The_Default_Is_Used()
    {
        var layout = ToolsLayout.Sanitize(null, Known);
        Assert.Equal(ToolsLayout.Default.Count, layout.Count);
        Assert.Equal(Ids(ToolsLayout.Default), Ids(layout));
    }

    [Fact]
    public void Users_Own_Order_Is_Kept()
    {
        var mine = new[]
        {
            new ToolsGroupLayout("MenuPages", new[] { CommandIds.DeletePages, CommandIds.RotateRight }),
        };
        var layout = ToolsLayout.Sanitize(mine, Known);
        var pages = layout.First(g => g.TitleKey == "MenuPages").Commands;
        Assert.Equal(CommandIds.DeletePages, pages[0]);
        Assert.Equal(CommandIds.RotateRight, pages[1]);
    }

    [Fact]
    public void A_Command_Added_In_A_New_Version_Appears_By_Itself()
    {
        // Сохранённая раскладка старой версии знает только один инструмент.
        var old = new[] { new ToolsGroupLayout("MenuPages", new[] { CommandIds.RotateRight }) };
        var layout = ToolsLayout.Sanitize(old, Known);

        var pages = layout.First(g => g.TitleKey == "MenuPages").Commands;
        Assert.Equal(CommandIds.RotateRight, pages[0]);   // выбор пользователя первым
        Assert.Contains(CommandIds.DeletePages, pages);   // новое дописано следом
    }

    [Fact]
    public void A_Command_Removed_From_The_Program_Disappears_Quietly()
    {
        var saved = new[]
        {
            new ToolsGroupLayout("MenuPages", new[] { CommandIds.RotateRight, "команды.больше.нет" }),
        };
        Assert.DoesNotContain("команды.больше.нет", Ids(ToolsLayout.Sanitize(saved, Known)));
    }

    [Fact]
    public void The_Same_Command_Never_Lands_In_Two_Sections()
    {
        var saved = new[]
        {
            new ToolsGroupLayout("MenuPages", new[] { CommandIds.Print }),
            new ToolsGroupLayout("MenuPrint", new[] { CommandIds.Print }),
        };
        var ids = Ids(ToolsLayout.Sanitize(saved, Known));
        Assert.Single(ids, id => id == CommandIds.Print);
    }

    [Fact]
    public void Moving_A_Command_To_Another_Section_Is_Remembered()
    {
        // Пользователь перетащил печать в раздел «Страницы».
        var moved = new[]
        {
            new ToolsGroupLayout("MenuPages", new[] { CommandIds.Print, CommandIds.RotateRight }),
        };
        var layout = ToolsLayout.Sanitize(moved, Known);
        Assert.Contains(CommandIds.Print, layout.First(g => g.TitleKey == "MenuPages").Commands);
        Assert.DoesNotContain(CommandIds.Print,
            layout.FirstOrDefault(g => g.TitleKey == "MenuPrint")?.Commands ?? Array.Empty<string>());
    }

    [Fact]
    public void Layout_Survives_A_Round_Trip_Through_Settings()
    {
        var layout = ToolsLayout.Sanitize(null, Known);
        var restored = ToolsLayout.Sanitize(
            ToolsLayout.FromSetting(ToolsLayout.ToSetting(layout)), Known);
        Assert.Equal(Ids(layout), Ids(restored));
    }

    [Fact]
    public void Broken_Setting_Does_Not_Break_The_Panel()
    {
        Assert.Null(ToolsLayout.FromSetting(""));
        Assert.Null(ToolsLayout.FromSetting("мусор без двоеточия"));
        Assert.Equal(Ids(ToolsLayout.Default),
            Ids(ToolsLayout.Sanitize(ToolsLayout.FromSetting("мусор"), Known)));
    }

    [Fact]
    public void Every_Default_Command_Exists_In_The_Registry()
    {
        var missing = ToolsLayout.Default.SelectMany(g => g.Commands).Where(id => !Known(id)).ToList();
        Assert.Empty(missing);
    }
}
