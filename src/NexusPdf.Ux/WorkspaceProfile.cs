namespace NexusPdf.Ux;

/// <summary>Группа инструментов на боковом рельсе.</summary>
public enum ToolRail
{
    None,
    Pages,
    Comment,
    Edit,
    Forms,
    Protect,
}

/// <summary>
/// Профиль рабочего пространства — набор состояний интерфейса под задачу.
///
/// Смысл в том, что одна и та же программа используется по-разному: читать,
/// править и рецензировать удобно с РАЗНЫМИ открытыми панелями. Переключать
/// их по одной каждый раз — работа, которой быть не должно.
/// </summary>
public sealed record WorkspaceProfile
{
    public required string Id { get; init; }

    /// <summary>Ключ локализации названия.</summary>
    public required string TitleKey { get; init; }

    public ToolRail Rail { get; init; } = ToolRail.None;

    /// <summary>Панель комментариев справа.</summary>
    public bool CommentsPanel { get; init; }

    /// <summary>Показывать оглавление вместо миниатюр (если оглавление есть).</summary>
    public bool Outline { get; init; }

    /// <summary>Режим систематизации страниц.</summary>
    public bool Organize { get; init; }

    /// <summary>Вписывать страницу целиком (иначе — по ширине).</summary>
    public bool FitWholePage { get; init; }

    /// <summary>Чтение: ничего лишнего, страница целиком.</summary>
    public static readonly WorkspaceProfile Reading = new()
    {
        Id = "reading", TitleKey = "UxWorkspaceReading",
        Rail = ToolRail.None, CommentsPanel = false, Outline = true, FitWholePage = true,
    };

    /// <summary>Правка: инструменты содержимого, панели не мешают странице.</summary>
    public static readonly WorkspaceProfile Editing = new()
    {
        Id = "editing", TitleKey = "UxWorkspaceEditing",
        Rail = ToolRail.Edit, CommentsPanel = false, Outline = false, FitWholePage = false,
    };

    /// <summary>Рецензирование: инструменты комментариев и их список.</summary>
    public static readonly WorkspaceProfile Reviewing = new()
    {
        Id = "reviewing", TitleKey = "UxWorkspaceReviewing",
        Rail = ToolRail.Comment, CommentsPanel = true, Outline = false, FitWholePage = false,
    };

    /// <summary>Страницы: систематизация, миниатюры во всю ширину.</summary>
    public static readonly WorkspaceProfile Pages = new()
    {
        Id = "pages", TitleKey = "UxWorkspacePages",
        Rail = ToolRail.Pages, CommentsPanel = false, Outline = false, Organize = true,
    };

    public static IReadOnlyList<WorkspaceProfile> All { get; } =
        new[] { Reading, Editing, Reviewing, Pages };

    /// <summary>Профиль по идентификатору; неизвестный — чтение.</summary>
    public static WorkspaceProfile ById(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Reading;

    /// <summary>Имя группы инструментов так, как его ждёт интерфейс.</summary>
    public string? RailName => Rail == ToolRail.None ? null : Rail.ToString();
}
