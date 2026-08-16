namespace NexusPdf.Ux;

/// <summary>Рабочий режим программы.</summary>
public enum WorkMode
{
    View,
    Edit,
    Pages,
    Comments,
    Forms,
    Protect,
    Ocr,
    Compare,
    Print,
}

/// <summary>Что именно выделено сейчас.</summary>
public enum SelectionKind
{
    Nothing,
    Text,
    TextObject,
    Image,
    Shape,
    Annotation,
    FormField,
    Link,
    Signature,
    Page,
    Bookmark,
    Layer,
    Attachment,
    SearchResult,

    /// <summary>Вкладка открытого документа.</summary>
    Tab,
}

/// <summary>
/// Единая модель выделения. Панели, контекстные меню и сами команды обязаны
/// смотреть в ОДИН экземпляр: иначе панель показывает свойства одного объекта,
/// меню относится к другому, а команда выполняется над третьим — это прямо
/// запрещено требованиями и ровно так ломаются интерфейсы.
/// </summary>
public sealed record SelectionContext
{
    public static readonly SelectionContext Empty = new();

    public bool HasDocument { get; init; }
    public WorkMode Mode { get; init; } = WorkMode.View;
    public SelectionKind Kind { get; init; } = SelectionKind.Nothing;

    /// <summary>Число выделенных однородных объектов: 12 страниц, 3 аннотации.</summary>
    public int SelectedCount { get; init; }

    /// <summary>Номера выделенных страниц с единицы — для названий вроде «Повернуть 12 страниц».</summary>
    public IReadOnlyList<int> SelectedPageNumbers { get; init; } = Array.Empty<int>();

    public int CurrentPageNumber { get; init; }
    public int PageCount { get; init; }

    /// <summary>Сколько документов открыто: переносить страницы некуда, если он один.</summary>
    public int OpenDocumentCount { get; init; } = 1;

    public bool HasTextSelection { get; init; }
    public bool IsReadOnly { get; init; }
    public bool HasUnsavedChanges { get; init; }
    public bool CanUndo { get; init; }
    public bool CanRedo { get; init; }

    public bool IsSigned { get; init; }
    public bool IsEncrypted { get; init; }
    public bool AllowsPrinting { get; init; } = true;
    public bool AllowsEditing { get; init; } = true;

    /// <summary>Доступность внешних инструментов: qpdf, распознавание, редактор растра.</summary>
    public bool HasQpdf { get; init; } = true;
    public bool HasOcr { get; init; } = true;
    public bool HasImageEditor { get; init; } = true;

    /// <summary>Идёт длительная операция — опасные команды на это время запрещены.</summary>
    public bool IsBusy { get; init; }

    /// <summary>
    /// На странице выбран наложенный объект. Отдельно от <see cref="Kind"/>:
    /// команда может прийти из палитры или горячей клавиши, где вида
    /// выделения нет, а объект на странице выбран.
    /// </summary>
    public bool HasSelectedObject { get; init; }

    public bool HasMultipleSelection => SelectedCount > 1;

    /// <summary>Выделен хоть какой-то объект внутри страницы.</summary>
    public bool HasObjectSelection => HasSelectedObject || Kind is
        SelectionKind.TextObject or SelectionKind.Image or SelectionKind.Shape or
        SelectionKind.Annotation or SelectionKind.FormField or SelectionKind.Link or
        SelectionKind.Signature;
}
