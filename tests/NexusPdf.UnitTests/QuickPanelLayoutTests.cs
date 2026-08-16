using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Разбор состава быстрой панели. Проверяется то, что ломает панель после
/// обновления программы: исчезнувшая команда, повторы, лишние разделители,
/// пустой список.
/// </summary>
public sealed class QuickPanelLayoutTests
{
    private static readonly CommandRegistry Registry = AppCommands.Build();

    private static bool Known(string id) => Registry.Find(id) != null;

    [Fact]
    public void Empty_Setting_Gives_The_Default_Panel()
    {
        Assert.Equal(QuickPanelLayout.Default, QuickPanelLayout.Sanitize(null, Known));
        Assert.Equal(QuickPanelLayout.Default, QuickPanelLayout.Sanitize(Array.Empty<string>(), Known));
    }

    /// <summary>
    /// Новая кнопка обязана дойти до того, кто панель уже настраивал: иначе
    /// добавленная в программу команда для него просто не существует.
    /// </summary>
    [Fact]
    public void A_Panel_Set_Up_Long_Ago_Gets_The_Buttons_Added_Since()
    {
        var saved = new[] { CommandIds.Open, CommandIds.Save, CommandIds.Print };

        var (ids, generation) = QuickPanelLayout.Upgrade(saved, 0);

        Assert.Equal(QuickPanelLayout.Generation, generation);
        Assert.Contains(CommandIds.OptimizeDocument, ids);
        Assert.Contains(CommandIds.ToggleOrganize, ids);
        // Прежний порядок не тронут: новое дописано в конец.
        Assert.Equal(saved, ids.Take(saved.Length));
    }

    /// <summary>
    /// Убранная вручную кнопка не должна возвращаться на каждом запуске: как
    /// только поколение дотянуто, доливать больше нечего.
    /// </summary>
    [Fact]
    public void An_Up_To_Date_Panel_Is_Left_Exactly_As_It_Is()
    {
        var saved = new[] { CommandIds.Open, CommandIds.Save };

        var (ids, generation) = QuickPanelLayout.Upgrade(saved, QuickPanelLayout.Generation);

        Assert.Equal(saved, ids);
        Assert.Equal(QuickPanelLayout.Generation, generation);
    }

    /// <summary>Кнопка, которая уже есть, вторым экземпляром не добавляется.</summary>
    [Fact]
    public void Already_Present_Buttons_Are_Not_Doubled()
    {
        var saved = new[] { CommandIds.Open, CommandIds.OptimizeDocument, CommandIds.ToggleOrganize };

        var (ids, _) = QuickPanelLayout.Upgrade(saved, 0);

        Assert.Equal(saved, ids);
    }

    /// <summary>Пустая настройка — это «умолчание», и доливать в неё нечего.</summary>
    [Fact]
    public void An_Untouched_Panel_Simply_Takes_The_Default()
    {
        var (ids, generation) = QuickPanelLayout.Upgrade(Array.Empty<string>(), 0);
        Assert.Equal(QuickPanelLayout.Default, ids);
        Assert.Equal(QuickPanelLayout.Generation, generation);
    }

    /// <summary>Систематизация и оптимизация — в панели по умолчанию.</summary>
    [Fact]
    public void The_Default_Panel_Offers_Organize_And_Optimize()
    {
        Assert.Contains(CommandIds.ToggleOrganize, QuickPanelLayout.Default);
        Assert.Contains(CommandIds.OptimizeDocument, QuickPanelLayout.Default);
        Assert.All(QuickPanelLayout.Default,
            id => Assert.True(id == QuickPanelLayout.Separator || Known(id), id));
    }

    [Fact]
    public void Command_Removed_From_The_Program_Disappears_From_The_Panel()
    {
        // Настройка пережила версию, где команда была: панель обязана
        // открыться без неё, а не сломаться.
        var result = QuickPanelLayout.Sanitize(
            new[] { CommandIds.Open, "команда.которой.нет", CommandIds.Print }, Known);
        Assert.Equal(new[] { CommandIds.Open, CommandIds.Print }, result);
    }

    [Fact]
    public void The_Same_Command_Is_Never_Placed_Twice()
    {
        var result = QuickPanelLayout.Sanitize(
            new[] { CommandIds.Save, CommandIds.Save, CommandIds.Print }, Known);
        Assert.Equal(new[] { CommandIds.Save, CommandIds.Print }, result);
    }

    [Fact]
    public void Separators_Do_Not_Double_Up_Or_Hang_At_The_Edges()
    {
        var result = QuickPanelLayout.Sanitize(
            new[] { "|", "|", CommandIds.Open, "|", "|", CommandIds.Print, "|", "|" }, Known);
        Assert.Equal(new[] { CommandIds.Open, "|", CommandIds.Print }, result);
    }

    [Fact]
    public void A_Panel_Of_Only_Separators_Falls_Back_To_The_Default()
    {
        // Пустая полоса без кнопок не бывает чьим-то осознанным выбором.
        Assert.Equal(QuickPanelLayout.Default,
            QuickPanelLayout.Sanitize(new[] { "|", "|", "|" }, Known));
    }

    [Fact]
    public void Every_Default_Command_Really_Exists()
    {
        var missing = QuickPanelLayout.Default
            .Where(id => id != QuickPanelLayout.Separator && !Known(id))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Users_Own_Order_Is_Kept_Exactly()
    {
        var custom = new[] { CommandIds.Ocr, "|", CommandIds.RotateRight, CommandIds.Print };
        Assert.Equal(custom, QuickPanelLayout.Sanitize(custom, Known));
    }
}
