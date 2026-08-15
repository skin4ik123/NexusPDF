using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Видимость панелей. Скрыть можно любую панель, а вернуть их обратно нужно
/// такими же, какими они были, — иначе «посмотреть страницу целиком» стоит
/// пользователю его настроек.
/// </summary>
public sealed class PanelLayoutTests
{
    [Fact]
    public void Every_Panel_Can_Be_Hidden()
    {
        var layout = PanelLayout.Default;
        foreach (var panel in Enum.GetValues<UiPanel>())
        {
            var hidden = layout.With(panel, false);
            Assert.False(hidden.IsVisible(panel));
            Assert.True(hidden.With(panel, true).IsVisible(panel));
        }
    }

    [Fact]
    public void Toggle_Flips_Only_The_Named_Panel()
    {
        var layout = PanelLayout.Default.Toggle(UiPanel.ToolRail);
        Assert.False(layout.ToolRail);
        Assert.True(layout.QuickPanel);
        Assert.True(layout.SidePanel);
        Assert.True(layout.StatusBar);
    }

    [Fact]
    public void Page_Only_Hides_Everything()
    {
        Assert.True(PanelLayout.PageOnly.IsPageOnly);
        foreach (var panel in Enum.GetValues<UiPanel>())
            Assert.False(PanelLayout.PageOnly.IsVisible(panel));
    }

    [Fact]
    public void Returning_From_Page_Only_Restores_The_Users_Own_Layout()
    {
        // Своя раскладка: рельс убран, комментарии открыты.
        var mine = PanelLayout.Default with { ToolRail = false, Comments = true };

        var (hidden, saved) = mine.TogglePageOnly(null);
        Assert.True(hidden.IsPageOnly);
        Assert.Equal(mine, saved);

        var (restored, cleared) = hidden.TogglePageOnly(saved);
        Assert.Equal(mine, restored);
        Assert.Null(cleared);
    }

    [Fact]
    public void Returning_Without_A_Saved_Layout_Gives_The_Default()
    {
        var (restored, _) = PanelLayout.PageOnly.TogglePageOnly(null);
        Assert.Equal(PanelLayout.Default, restored);
    }

    [Fact]
    public void Layout_Survives_A_Round_Trip_Through_Settings()
    {
        var layout = PanelLayout.Default with { QuickPanel = false, Comments = true };
        Assert.Equal(layout, PanelLayout.FromSetting(layout.ToSetting()));
    }

    [Fact]
    public void Missing_Setting_Gives_The_Default_But_Empty_Means_All_Hidden()
    {
        // Настройки ещё нет — берём набор по умолчанию.
        Assert.Equal(PanelLayout.Default, PanelLayout.FromSetting(null));
        // Пустая строка — это осознанный выбор «спрятать всё», а не отсутствие
        // настройки, и подменять её умолчанием нельзя.
        Assert.True(PanelLayout.FromSetting("").IsPageOnly);
    }

    [Fact]
    public void Unknown_Names_In_Settings_Are_Ignored()
    {
        var layout = PanelLayout.FromSetting("QuickPanel,НетТакойПанели,StatusBar");
        Assert.True(layout.QuickPanel);
        Assert.True(layout.StatusBar);
        Assert.False(layout.ToolRail);
    }
}
