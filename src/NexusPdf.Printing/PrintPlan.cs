namespace NexusPdf.Printing;

/// <summary>Какую область PDF-страницы печатать.</summary>
public enum PageBoxKind
{
    MediaBox,
    CropBox,
    TrimBox,
    BleedBox,
    ArtBox,
    Custom,
}

/// <summary>Что делать с аннотациями в печатном потоке.</summary>
public enum AnnotationPolicy
{
    /// <summary>Только содержимое страницы, без аннотаций.</summary>
    DocumentOnly,

    /// <summary>Аннотации с установленным флагом Print — поведение по умолчанию и требование PDF.</summary>
    PrintableAnnotations,

    /// <summary>Все видимые аннотации, даже без флага Print — только по явному выбору.</summary>
    AllVisibleAnnotations,
}

/// <summary>Что делать с полями форм.</summary>
public enum FormPolicy
{
    /// <summary>Документ вместе со значениями полей.</summary>
    WithValues,

    /// <summary>
    /// Пустой бланк: аннотации-виджеты не рисуются, поэтому на бумагу не
    /// попадают ни значения, ни подсветка полей. Разлиновка бланка при этом
    /// остаётся: в подавляющем большинстве форм рамки и подписи полей лежат в
    /// содержимом страницы, а виджет добавляет поверх только значение.
    /// </summary>
    BlankForm,

    /// <summary>
    /// Документ без элементов формы вовсе. Отрисовывается так же, как
    /// <see cref="BlankForm"/>: разница между «без значений» и «без полей»
    /// существует в намерении, но не в том, что умеет отдать движок, — и
    /// придумывать её показом несуществующих рамок было бы обманом. В окне
    /// печати предлагается только «пустой бланк»; значение сохранено ради
    /// профилей, созданных прежними версиями.
    /// </summary>
    WithoutFields,
}

/// <summary>Какое состояние слоёв OCG печатать.</summary>
public enum LayerPolicy
{
    /// <summary>Как сейчас видно на экране.</summary>
    CurrentView,

    /// <summary>Состояние по умолчанию из документа.</summary>
    DocumentDefault,

    /// <summary>Все слои, у которых PrintState разрешает печать.</summary>
    AllPrintable,

    /// <summary>Явно перечисленный набор.</summary>
    Explicit,
}

/// <summary>Цветовой режим печатного потока.</summary>
public enum ColorMode
{
    /// <summary>Цвет как в документе.</summary>
    Color,

    /// <summary>Оттенки серого рассчитываются программой.</summary>
    Grayscale,

    /// <summary>Один чёрный без полутонов.</summary>
    Monochrome,

    /// <summary>Решение оставлено драйверу принтера.</summary>
    PrinterDefault,
}

/// <summary>Двусторонняя печать.</summary>
public enum DuplexMode
{
    Simplex,
    LongEdge,
    ShortEdge,

    /// <summary>Две стороны печатаются двумя заданиями с переворотом стопки вручную.</summary>
    Manual,
}

/// <summary>Порядок раскладки ячеек при печати нескольких страниц на листе.</summary>
public enum NUpOrder
{
    RowsLeftToRight,
    RowsRightToLeft,
    ColumnsTopToBottom,
    ColumnsBottomToTop,
}

/// <summary>Причина, по которой страница печатается растром, а не как есть.</summary>
public enum RasterReason
{
    None,

    /// <summary>Пользователь выбрал «печатать как изображение».</summary>
    UserRequested,

    /// <summary>Прозрачность, которую выбранный backend не передаёт корректно.</summary>
    Transparency,

    /// <summary>Недоступный или проблемный шрифт.</summary>
    Font,

    /// <summary>Предыдущая попытка обычной печати не удалась.</summary>
    PreviousFailure,

    /// <summary>Разрешения PDF допускают только печать низкого качества.</summary>
    LowQualityPermission,
}

/// <summary>
/// Одна PDF-страница, размещённая на физическом листе. Хранит и результат
/// (координаты на листе), и всё, из чего он получен, — иначе предпросмотр,
/// печать и тесты рассуждали бы о разных вещах.
/// </summary>
public sealed record PlacedPage
{
    /// <summary>Идентификатор исходного документа: в пакетной печати их несколько.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Физический номер страницы в файле, с нуля.</summary>
    public required int SourcePageIndex { get; init; }

    /// <summary>Логическая метка страницы, если у документа своя нумерация.</summary>
    public string? PageLabel { get; init; }

    /// <summary>Выбранная область исходной страницы.</summary>
    public required PageBoxKind Box { get; init; }

    /// <summary>Область исходной страницы в её собственных координатах.</summary>
    public required RectPt SourceRectPt { get; init; }

    /// <summary>Место на листе, куда эта область попадает.</summary>
    public required RectPt TargetRectPt { get; init; }

    /// <summary>Видимая часть на листе: то же, что TargetRect, если ничего не обрезано.</summary>
    public required RectPt ClipRectPt { get; init; }

    /// <summary>Масштаб содержимого, 1.0 — фактический размер.</summary>
    public required double Scale { get; init; }

    /// <summary>Дополнительный поворот в градусах, кратный 90.</summary>
    public required int RotationDegrees { get; init; }

    /// <summary>Зеркальное отражение по горизонтали (нужно для work-and-turn и плёнок).</summary>
    public bool MirrorHorizontal { get; init; }

    public AnnotationPolicy Annotations { get; init; } = AnnotationPolicy.PrintableAnnotations;
    public FormPolicy Forms { get; init; } = FormPolicy.WithValues;
    public LayerPolicy Layers { get; init; } = LayerPolicy.CurrentView;

    public RasterReason Raster { get; init; } = RasterReason.None;

    /// <summary>Обрезано ли содержимое: TargetRect не помещается в ClipRect.</summary>
    public bool IsClipped =>
        ClipRectPt.WidthPt + 0.01 < TargetRectPt.WidthPt ||
        ClipRectPt.HeightPt + 0.01 < TargetRectPt.HeightPt;
}

/// <summary>Типографская метка, нанесённая поверх листа.</summary>
public sealed record SheetMark(string Kind, RectPt AreaPt, string? Text = null);

/// <summary>
/// Один физический лист задания. Лицевая и обратная стороны — разные листы
/// плана со ссылкой друг на друга: так дуплекс проверяется тестами.
/// </summary>
public sealed record SheetPlan
{
    public required int SheetIndex { get; init; }

    /// <summary>Физический размер бумаги.</summary>
    public required SizePt PaperSizePt { get; init; }

    /// <summary>Область, в которую принтер физически может печатать.</summary>
    public required RectPt PrintableAreaPt { get; init; }

    /// <summary>Непечатаемые поля вокруг printable area.</summary>
    public required MarginsPt HardMarginsPt { get; init; }

    /// <summary>true — лицевая сторона листа, false — обратная.</summary>
    public bool IsFront { get; init; } = true;

    /// <summary>Индекс парного листа при двусторонней печати; null — односторонний.</summary>
    public int? PairedSheetIndex { get; init; }

    public string? PaperSourceName { get; init; }
    public string? MediaTypeName { get; init; }

    public ColorMode Color { get; init; } = ColorMode.Color;

    /// <summary>Размещённые страницы; пустой список — намеренно пустой лист.</summary>
    public required IReadOnlyList<PlacedPage> Pages { get; init; }

    public IReadOnlyList<SheetMark> Marks { get; init; } = Array.Empty<SheetMark>();

    /// <summary>Лист вставлен планом, а не взят из документа (добор буклета до кратности четырём).</summary>
    public bool IsInsertedBlank => Pages.Count == 0;

    /// <summary>Есть ли на листе обрезанное содержимое — это выносится в предупреждения.</summary>
    public bool HasClippedContent => Pages.Any(p => p.IsClipped);
}

/// <summary>Сортировка копий.</summary>
public enum CollationMode
{
    /// <summary>1,2,3 / 1,2,3 — комплектами.</summary>
    Collated,

    /// <summary>1,1 / 2,2 / 3,3 — стопками одинаковых страниц.</summary>
    Uncollated,
}

/// <summary>Кто выполняет сортировку копий.</summary>
public enum CollationExecutor
{
    /// <summary>Копии размножает принтер: задание отправляется один раз.</summary>
    Printer,

    /// <summary>Копии раскладывает программа: spool больше, зато работает везде.</summary>
    Application,
}

/// <summary>
/// Полностью рассчитанное задание печати. И предпросмотр, и отправка в очередь
/// работают ТОЛЬКО с этим объектом: отдельной упрощённой логики для preview
/// не существует — иначе они неизбежно расходятся.
/// </summary>
public sealed record PrintJobPlan
{
    public required string JobName { get; init; }
    public required string PrinterName { get; init; }

    /// <summary>Возможности принтера на момент расчёта: план не должен «уплывать» под изменившимся драйвером.</summary>
    public required PrinterCapabilities Capabilities { get; init; }

    public required IReadOnlyList<SheetPlan> Sheets { get; init; }

    public int Copies { get; init; } = 1;
    public CollationMode Collation { get; init; } = CollationMode.Collated;
    public CollationExecutor CollationBy { get; init; } = CollationExecutor.Printer;
    public DuplexMode Duplex { get; init; } = DuplexMode.Simplex;
    public bool ReverseOrder { get; init; }

    /// <summary>Предупреждения и ошибки предварительной проверки.</summary>
    public IReadOnlyList<PreflightIssue> Issues { get; init; } = Array.Empty<PreflightIssue>();

    /// <summary>Число физических листов бумаги с учётом копий и дуплекса.</summary>
    public int SheetCount
    {
        get
        {
            // При дуплексе две стороны — один лист бумаги.
            var sides = Sheets.Count;
            var physical = Duplex == DuplexMode.Simplex ? sides : (sides + 1) / 2;
            return physical * Copies;
        }
    }

    /// <summary>Число печатаемых сторон с учётом копий.</summary>
    public int SideCount => Sheets.Count * Copies;

    /// <summary>Сколько страниц документа реально попадёт на бумагу.</summary>
    public int PlacedPageCount => Sheets.Sum(s => s.Pages.Count);

    public bool HasBlockingIssues => Issues.Any(i => i.Level == PreflightLevel.Critical);
}
