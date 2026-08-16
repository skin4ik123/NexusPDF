namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Что включать в растр страницы при печати.
///
/// Отдельная запись, а не пара флагов в вызове, потому что решения тут
/// связанные: поля формы — это тоже аннотации, и «без аннотаций» обязано
/// означать и «без полей», иначе на бумаге появится то, чего пользователь
/// просил не печатать.
/// </summary>
/// <param name="IncludeAnnotations">Рисовать аннотации вообще.</param>
/// <param name="OnlyPrintableAnnotations">
/// Только те, у которых установлен флаг Print. Так требует PDF: комментарий,
/// помеченный автором как экранный, на бумагу не идёт. Снимается лишь по явному
/// выбору «печатать все видимые».
/// </param>
/// <param name="IncludeFormFields">
/// Рисовать поля формы (аннотации подтипа Widget) вместе со значениями.
/// </param>
public readonly record struct PrintContentOptions(
    bool IncludeAnnotations = true,
    bool OnlyPrintableAnnotations = true,
    bool IncludeFormFields = true)
{
    /// <summary>Только содержимое страницы: ни аннотаций, ни полей.</summary>
    public static PrintContentOptions DocumentOnly { get; } = new(false, true, false);

    /// <summary>
    /// Поведение по умолчанию: печатные аннотации и заполненные поля.
    ///
    /// Значения перечислены явно, и это не многословие: у record struct
    /// <c>new()</c> вызывает НЕ первичный конструктор, а неявный пустой, и
    /// значения по умолчанию из его параметров не применяются — поля просто
    /// обнуляются. «По умолчанию» тихо означало бы «без аннотаций».
    /// </summary>
    public static PrintContentOptions Default { get; } = new(true, true, true);

    /// <summary>Нужно ли трогать флаги аннотаций перед отрисовкой.</summary>
    public bool NeedsFiltering => IncludeAnnotations && (OnlyPrintableAnnotations || !IncludeFormFields);
}
