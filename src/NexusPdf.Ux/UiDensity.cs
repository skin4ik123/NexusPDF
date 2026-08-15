namespace NexusPdf.Ux;

/// <summary>Плотность интерфейса — размер кликабельных мест.</summary>
public enum UiDensity
{
    /// <summary>Плотно: мышь, много документов на экране, малый экран.</summary>
    Compact,

    /// <summary>Обычно: мышь или тачпад на обычном ноутбуке.</summary>
    Comfortable,

    /// <summary>Пальцем: цели не меньше 44×44, увеличенные отступы.</summary>
    Touch,
}

/// <summary>
/// Размеры интерфейса для выбранной плотности. Все значения — в аппаратно
/// независимых точках WPF (1/96 дюйма).
///
/// Числа не «на глаз»: 44 — минимальная цель для пальца по рекомендациям
/// Microsoft и Apple (примерно 9 мм), 32 — привычный размер кнопки панели для
/// мыши, 24 — плотный режим для тех, кому важнее уместить больше.
/// </summary>
public sealed record UiMetrics
{
    public required UiDensity Density { get; init; }

    /// <summary>Минимальная сторона кликабельного места.</summary>
    public required double TouchTarget { get; init; }

    /// <summary>Высота строки списка и пункта меню.</summary>
    public required double RowHeight { get; init; }

    /// <summary>Размер значка на кнопке панели.</summary>
    public required double GlyphSize { get; init; }

    /// <summary>Базовый размер шрифта интерфейса.</summary>
    public required double FontSize { get; init; }

    /// <summary>Внутренний отступ кнопки панели: слева-справа, сверху-снизу.</summary>
    public required double PaddingX { get; init; }
    public required double PaddingY { get; init; }

    /// <summary>Зазор между соседними целями — палец промахивается по соседней кнопке.</summary>
    public required double Gap { get; init; }

    public static readonly UiMetrics Compact = new()
    {
        Density = UiDensity.Compact,
        TouchTarget = 24, RowHeight = 24, GlyphSize = 14, FontSize = 12,
        PaddingX = 6, PaddingY = 3, Gap = 1,
    };

    public static readonly UiMetrics Comfortable = new()
    {
        Density = UiDensity.Comfortable,
        TouchTarget = 32, RowHeight = 30, GlyphSize = 15, FontSize = 13,
        PaddingX = 8, PaddingY = 5, Gap = 2,
    };

    public static readonly UiMetrics Touch = new()
    {
        Density = UiDensity.Touch,
        TouchTarget = 44, RowHeight = 44, GlyphSize = 19, FontSize = 15,
        PaddingX = 12, PaddingY = 10, Gap = 6,
    };

    public static UiMetrics For(UiDensity density) => density switch
    {
        UiDensity.Compact => Compact,
        UiDensity.Touch => Touch,
        _ => Comfortable,
    };
}

/// <summary>
/// Выбор плотности. Правило одно: пользователь главнее автоматики. Пока он
/// ничего не выбрал, плотность идёт за тем, чем он реально работает —
/// коснулся пальцем экрана, значит цели должны стать крупнее немедленно, а не
/// после похода в настройки.
/// </summary>
public static class DensityPolicy
{
    /// <summary>Значение настройки, означающее «решай сама».</summary>
    public const string Auto = "auto";

    public static UiDensity Resolve(string? setting, bool touchUsedRecently, bool hasTouchScreen)
    {
        var explicitChoice = Parse(setting);
        if (explicitChoice != null)
            return explicitChoice.Value;

        // Наличие сенсорного экрана само по себе ничего не значит: у ноутбука-
        // трансформера он есть всегда, а работают на нём обычно мышью.
        if (touchUsedRecently && hasTouchScreen)
            return UiDensity.Touch;

        return UiDensity.Comfortable;
    }

    public static UiDensity? Parse(string? setting) => setting?.ToLowerInvariant() switch
    {
        "compact" => UiDensity.Compact,
        "comfortable" => UiDensity.Comfortable,
        "touch" => UiDensity.Touch,
        _ => null,
    };

    public static string ToSetting(UiDensity density) => density switch
    {
        UiDensity.Compact => "compact",
        UiDensity.Touch => "touch",
        _ => "comfortable",
    };
}
