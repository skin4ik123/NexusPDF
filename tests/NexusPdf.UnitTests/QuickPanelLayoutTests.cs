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
