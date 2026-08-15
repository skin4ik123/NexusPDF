namespace NexusPdf.Ux;

/// <summary>Панель интерфейса, которую можно скрыть.</summary>
public enum UiPanel
{
    /// <summary>Верхняя быстрая панель с кнопками.</summary>
    QuickPanel,

    /// <summary>Левый рельс групп инструментов.</summary>
    ToolRail,

    /// <summary>Боковая панель: миниатюры или оглавление.</summary>
    SidePanel,

    /// <summary>Список комментариев справа.</summary>
    Comments,

    /// <summary>Свойства выбранного объекта справа.</summary>
    Properties,

    /// <summary>Строка состояния внизу.</summary>
    StatusBar,
}

/// <summary>
/// Видимость панелей. Правило простое: скрыть можно ЛЮБУЮ панель, включая
/// верхнюю, а страница остаётся всегда — она и есть документ.
///
/// Состояние живёт здесь, а не в разметке, чтобы «спрятать всё лишнее» и
/// возврат обратно были одной понятной операцией, а не десятью переключателями
/// в разных местах.
/// </summary>
public sealed record PanelLayout
{
    public bool QuickPanel { get; init; } = true;
    public bool ToolRail { get; init; } = true;
    public bool SidePanel { get; init; } = true;
    public bool Comments { get; init; }
    public bool Properties { get; init; } = true;
    public bool StatusBar { get; init; } = true;

    public static readonly PanelLayout Default = new();

    /// <summary>Только страница: спрятано всё, что можно спрятать.</summary>
    public static readonly PanelLayout PageOnly = new()
    {
        QuickPanel = false, ToolRail = false, SidePanel = false,
        Comments = false, Properties = false, StatusBar = false,
    };

    public bool IsVisible(UiPanel panel) => panel switch
    {
        UiPanel.QuickPanel => QuickPanel,
        UiPanel.ToolRail => ToolRail,
        UiPanel.SidePanel => SidePanel,
        UiPanel.Comments => Comments,
        UiPanel.Properties => Properties,
        UiPanel.StatusBar => StatusBar,
        _ => true,
    };

    public PanelLayout With(UiPanel panel, bool visible) => panel switch
    {
        UiPanel.QuickPanel => this with { QuickPanel = visible },
        UiPanel.ToolRail => this with { ToolRail = visible },
        UiPanel.SidePanel => this with { SidePanel = visible },
        UiPanel.Comments => this with { Comments = visible },
        UiPanel.Properties => this with { Properties = visible },
        UiPanel.StatusBar => this with { StatusBar = visible },
        _ => this,
    };

    public PanelLayout Toggle(UiPanel panel) => With(panel, !IsVisible(panel));

    /// <summary>Спрятано ли всё, что можно спрятать.</summary>
    public bool IsPageOnly => !QuickPanel && !ToolRail && !SidePanel &&
                              !Comments && !Properties && !StatusBar;

    /// <summary>
    /// Переключатель «только страница». Возврат отдаёт НЕ набор по умолчанию, а
    /// то, что было до скрытия: иначе пользователь теряет свою раскладку каждый
    /// раз, когда захотел посмотреть страницу целиком.
    /// </summary>
    public (PanelLayout Layout, PanelLayout? Saved) TogglePageOnly(PanelLayout? saved)
    {
        if (IsPageOnly)
            return (saved ?? Default, null);
        return (PageOnly, this);
    }

    /// <summary>Строка для настроек: «1101» читается плохо, поэтому список имён.</summary>
    public string ToSetting() => string.Join(",", new[]
    {
        QuickPanel ? nameof(QuickPanel) : null,
        ToolRail ? nameof(ToolRail) : null,
        SidePanel ? nameof(SidePanel) : null,
        Comments ? nameof(Comments) : null,
        Properties ? nameof(Properties) : null,
        StatusBar ? nameof(StatusBar) : null,
    }.Where(n => n != null));

    public static PanelLayout FromSetting(string? setting)
    {
        if (setting == null) return Default;
        var parts = setting.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PanelLayout
        {
            QuickPanel = parts.Contains(nameof(QuickPanel)),
            ToolRail = parts.Contains(nameof(ToolRail)),
            SidePanel = parts.Contains(nameof(SidePanel)),
            Comments = parts.Contains(nameof(Comments)),
            Properties = parts.Contains(nameof(Properties)),
            StatusBar = parts.Contains(nameof(StatusBar)),
        };
    }
}
