namespace NexusPdf.Ux;

/// <summary>Насколько разрушительна команда.</summary>
public enum CommandDanger
{
    /// <summary>Ничего не меняет: масштаб, переход, копирование.</summary>
    Safe,

    /// <summary>Меняет документ, но отменяется Ctrl+Z.</summary>
    Undoable,

    /// <summary>Отменить нельзя: вымарывание, flatten, перезапись оригинала.</summary>
    Irreversible,
}

/// <summary>Пригодность для пальца.</summary>
public enum TouchSuitability
{
    /// <summary>Работает пальцем без оговорок.</summary>
    Good,

    /// <summary>Работает, но требует точности — на touch укрупняется.</summary>
    NeedsPrecision,

    /// <summary>Пальцем не выполнить: нужна клавиатура или мышь.</summary>
    Unsuitable,
}

/// <summary>Раздел, в который команда попадает в палитре и в меню «Все инструменты».</summary>
public enum CommandCategory
{
    File,
    Edit,
    View,
    Pages,
    Content,
    Comments,
    Forms,
    Security,
    Recognition,
    Convert,
    Print,
    Window,
    Help,
}

/// <summary>
/// Место команды в контекстном меню. Порядок значений задаёт порядок групп:
/// главное действие сверху, опасное — всегда внизу, чтобы «Удалить» не
/// оказалось между «Копировать» и «Вставить».
/// </summary>
public enum MenuGroup
{
    Primary,
    Quick,
    Clipboard,
    Editing,
    Arrange,
    Special,
    Properties,
    Dangerous,
}

/// <summary>
/// Описание команды. Одна запись на команду для ВСЕХ точек интерфейса:
/// панели, контекстного меню, палитры, горячей клавиши, сенсорной панели.
/// Отдельные обработчики одного действия в разных местах запрещены — именно
/// из-за них команды расходятся в поведении и названиях.
/// </summary>
public sealed record CommandDescriptor
{
    public required string Id { get; init; }

    /// <summary>Ключ локализации названия.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Ключ локализации краткого пояснения; пустой — пояснения нет.</summary>
    public string DescriptionKey { get; init; } = "";

    /// <summary>Символ Segoe MDL2 Assets; пустой — значка нет.</summary>
    public string Glyph { get; init; } = "";

    public CommandCategory Category { get; init; } = CommandCategory.Edit;
    public MenuGroup Group { get; init; } = MenuGroup.Editing;
    public CommandDanger Danger { get; init; } = CommandDanger.Undoable;
    public TouchSuitability Touch { get; init; } = TouchSuitability.Good;

    /// <summary>Сочетание клавиш в человеческой записи: «Ctrl+Shift+S».</summary>
    public string Shortcut { get; init; } = "";

    /// <summary>Команда открывает диалог — в названии обязано быть многоточие.</summary>
    public bool OpensDialog { get; init; }

    /// <summary>Команда осмысленна для нескольких выделенных объектов сразу.</summary>
    public bool SupportsMultiSelection { get; init; }

    /// <summary>Слова для поиска в палитре, включая синонимы и опечатки.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Когда команда доступна. Возвращает причину недоступности или null.
    /// Причина обязательна: выключенная кнопка, которая молчит, — худший вид
    /// интерфейса, потому что пользователь не понимает, что исправить.
    /// </summary>
    public Func<SelectionContext, string?>? Unavailable { get; init; }

    /// <summary>Проверка доступности с учётом общих правил.</summary>
    public CommandAvailability Evaluate(SelectionContext context)
    {
        // Общие запреты идут раньше частных: их нельзя забыть в каждой команде.
        if (context.IsBusy && Danger != CommandDanger.Safe)
            return CommandAvailability.No("UxBusy");

        if (context.IsReadOnly && Danger != CommandDanger.Safe)
            return CommandAvailability.No("UxReadOnly");

        var reason = Unavailable?.Invoke(context);
        return reason == null ? CommandAvailability.Yes : CommandAvailability.No(reason);
    }
}

/// <summary>Доступна ли команда и почему нет.</summary>
public sealed record CommandAvailability(bool IsAvailable, string? ReasonKey)
{
    public static readonly CommandAvailability Yes = new(true, null);

    public static CommandAvailability No(string reasonKey) => new(false, reasonKey);
}
