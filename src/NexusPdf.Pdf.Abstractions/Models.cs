namespace NexusPdf.Pdf.Abstractions;

/// <summary>Размер страницы в пунктах PDF (1/72 дюйма) с учётом /Rotate.</summary>
public sealed record PdfPageDescriptor(double WidthPoints, double HeightPoints);

public sealed record PdfDocumentInfo(int PageCount, IReadOnlyList<PdfPageDescriptor> Pages);

/// <summary>Готовый растр страницы в формате BGRA32, построчно сверху вниз.</summary>
public sealed record RenderedPageImage(int PixelWidth, int PixelHeight, int Stride, byte[] Bgra);

/// <summary>Прямоугольник текста в координатах страницы PDF (начало координат — левый нижний угол, Top &gt; Bottom).</summary>
public sealed record PdfTextRect(double Left, double Top, double Right, double Bottom);

/// <summary>
/// Накладываемый на страницу новый контент. Координаты — в пунктах PDF от
/// ЛЕВОГО ВЕРХНЕГО угла страницы в её ОТОБРАЖАЕМОЙ ориентации НА МОМЕНТ
/// РАЗМЕЩЕНИЯ (<see cref="PlacedRotation"/> — добавочный поворот страницы в
/// этот момент). Если страницу повернули после размещения, движок при
/// сохранении пересчитывает координаты в итоговую рамку.
/// </summary>
public abstract record PageOverlay
{
    /// <summary>Добавочный поворот страницы (в четвертях) на момент размещения оверлея.</summary>
    public int PlacedRotation { get; init; }
}

/// <summary>Новый текстовый блок. Позиция — верхний левый угол первой строки; поворот — против часовой, в отображаемых координатах.</summary>
public sealed record TextOverlay(
    string Text,
    double XPt,
    double YPt,
    double FontSizePt,
    uint ColorArgb,
    double RotationDegrees) : PageOverlay;

/// <summary>Новое изображение (BGRA32, построчно сверху вниз), вписанное в прямоугольник отображаемой страницы.</summary>
public sealed record ImageOverlay(
    byte[] Bgra,
    int PixelWidth,
    int PixelHeight,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt) : PageOverlay;

/// <summary>Черновик заметки-комментария (значок с текстом). Точка — в отображаемых координатах на момент размещения.</summary>
public sealed record NoteAnnotationDraft(
    double XPt,
    double YPt,
    string Contents,
    string Author) : PageOverlay;

/// <summary>Черновик фигурной аннотации: рамка/овал/маркер-выделение (прямоугольник с полупрозрачной заливкой).</summary>
public sealed record ShapeAnnotationDraft(
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt,
    uint StrokeArgb,
    uint FillArgb,
    double BorderWidthPt,
    bool IsEllipse,
    string Contents,
    string Author) : PageOverlay;

/// <summary>Точка рукописного штриха в отображаемых пунктах (от левого верхнего угла на момент рисования).</summary>
public readonly record struct InkPoint(double XPt, double YPt);

/// <summary>
/// Черновик рисунка от руки: карандаш, линия или стрелка. Хранится
/// Ink-аннотацией PDF, поэтому нарисованное можно удалить и оно не портит
/// содержимое страницы. Наконечник стрелки — отдельные штрихи в том же
/// объекте: так стрелка выглядит одинаково в любом просмотрщике.
/// </summary>
public sealed record InkAnnotationDraft(
    IReadOnlyList<IReadOnlyList<InkPoint>> Strokes,
    uint StrokeArgb,
    double WidthPt,
    string Contents,
    string Author) : PageOverlay;

/// <summary>
/// Найденный на странице текстовый объект: его содержимое и всё, что нужно
/// показать пользователю перед правкой. IsEmbeddedFont важен: у встроенного
/// подмножества шрифта может просто не быть нужных букв.
/// </summary>
public sealed record PdfTextObject(
    int ObjectIndex,
    string Text,
    double FontSizePt,
    uint ColorArgb,
    string FontName,
    bool IsEmbeddedFont,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt);

/// <summary>
/// Замена содержимого СУЩЕСТВУЮЩЕГО текстового объекта. Шрифт, размер, цвет
/// и матрица объекта остаются его собственными, поэтому правленый текст
/// выглядит как исходный, а не как наклейка поверх.
/// </summary>
public sealed record TextObjectReplacement(int ObjectIndex, string Text) : PageOverlay;

/// <summary>Найденное на странице растровое изображение: индекс объекта, его растр и рамка в отображаемых пунктах.</summary>
/// <summary>
/// Сводка по изображениям начала документа: на её основе выбирается режим
/// сжатия. Считается по метаданным и матрицам, БЕЗ декодирования пикселей —
/// иначе «посмотреть, что за файл» стоило бы столько же, сколько само сжатие.
/// </summary>
/// <param name="SampledPages">Сколько страниц просмотрено.</param>
/// <param name="Images">Изображений на них.</param>
/// <param name="TextLength">Символов текста на них же.</param>
/// <param name="AverageImageDpi">Среднее фактическое разрешение изображений.</param>
public sealed record PdfImageSummary(
    int SampledPages, int Images, int TextLength, double AverageImageDpi);

public sealed record PdfImageObject(
    int ObjectIndex,
    byte[] Bgra,
    int PixelWidth,
    int PixelHeight,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt);

/// <summary>
/// Замена ОДНОГО изображения страницы (правка выбранной картинки во внешнем
/// редакторе). Растр подменяется у существующего объекта, поэтому его матрица
/// сохраняется целиком: положение, масштаб, поворот, обрезка, прозрачность,
/// порядок отрисовки и связь с остальным содержимым страницы не меняются.
/// </summary>
public sealed record ImageObjectReplacement(
    int ObjectIndex,
    byte[] Bgra,
    int PixelWidth,
    int PixelHeight) : PageOverlay;

/// <summary>
/// Замена ВИЗУАЛЬНОГО СОДЕРЖИМОГО страницы растром (результат правки во
/// внешнем редакторе). Удаляются только объекты содержимого страницы;
/// аннотации, ссылки и поля форм живут в /Annots и сохраняются.
/// Размер страницы, её рамки и поворот не меняются.
/// </summary>
public sealed record PageRasterReplacement(
    byte[] Bgra,
    int PixelWidth,
    int PixelHeight) : PageOverlay;

/// <summary>
/// Черновик ВЫМАРЫВАНИЯ: прямоугольник (в отображаемых пунктах), содержимое
/// под которым при сохранении УНИЧТОЖАЕТСЯ. Страница с вымарками
/// растеризуется целиком с закрашенными областями — скрытые данные физически
/// отсутствуют в результате (это не «чёрный прямоугольник поверх текста»).
/// </summary>
public sealed record RedactionDraft(
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt) : PageOverlay;

/// <summary>Вид разметки выделенного текста.</summary>
public enum TextMarkupKind
{
    /// <summary>Маркер: полупрозрачная заливка поверх строк.</summary>
    Highlight,

    /// <summary>Подчёркивание под базовой линией.</summary>
    Underline,

    /// <summary>Зачёркивание по середине строк.</summary>
    StrikeOut,
}

/// <summary>Одна строка выделенного текста: рамка в отображаемых пунктах.</summary>
public sealed record TextMarkupRect(double XPt, double YPt, double WidthPt, double HeightPt);

/// <summary>
/// Разметка ВЫДЕЛЕННОГО текста: маркер, подчёркивание, зачёркивание.
///
/// Хранится настоящей текстовой аннотацией PDF (Highlight/Underline/StrikeOut)
/// с quadpoints по строкам выделения, а не прямоугольником «на глаз»: такую
/// разметку любая программа показывает как разметку текста, её видно в списке
/// комментариев и она снимается без следа. Рамки идут по строкам, поэтому
/// выделение из нескольких строк не превращается в один большой блок.
/// </summary>
public sealed record TextMarkupDraft(
    TextMarkupKind Kind,
    IReadOnlyList<TextMarkupRect> Rects,
    uint ColorArgb,
    string Contents,
    string Author) : PageOverlay;

/// <summary>Распознанное OCR слово: рамка в отображаемых пунктах (от левого верхнего угла на момент распознавания).</summary>
public sealed record OcrWordBox(
    string Text,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt);

/// <summary>Строка распознанного текста: рамка в отображаемых пунктах и цвета для замены.</summary>
public sealed record OcrTextLine(
    string Text,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt,
    uint BackgroundArgb = 0xFFFFFFFF,
    uint InkArgb = 0xFF000000);

/// <summary>
/// РЕДАКТИРУЕМЫЙ текст вместо скана: под каждой строкой закрашивается
/// прямоугольник цветом бумаги, а поверх ставится НАСТОЯЩИЙ видимый текст.
/// В отличие от невидимого слоя такой текст можно править как обычный —
/// ценой того, что начертание оригинала заменяется системным шрифтом.
/// </summary>
public sealed record OcrEditableTextOverlay(IReadOnlyList<OcrTextLine> Lines) : PageOverlay;

/// <summary>
/// Невидимый текстовый слой поверх скана (результат OCR): каждое слово
/// запекается невидимым текстовым объектом, растянутым по своей рамке —
/// поиск/копирование работают, изображение страницы не меняется.
/// </summary>
public sealed record OcrTextLayerOverlay(IReadOnlyList<OcrWordBox> Words) : PageOverlay;

/// <summary>
/// Ссылка PDF в точке: либо внешний адрес (Uri), либо переход на страницу
/// документа (TargetPageIndex >= 0). Оба поля пустыми не бывают.
/// </summary>
public sealed record PdfLinkInfo(string? Uri, int TargetPageIndex);

/// <summary>
/// Активное и потенциально небезопасное содержимое документа. Программа его
/// НЕ выполняет и не открывает, но обязана честно показать пользователю.
/// </summary>
public sealed record PdfActiveContent(
    int JavaScriptCount,
    IReadOnlyList<string> JavaScriptNames,
    int AttachmentCount,
    IReadOnlyList<string> AttachmentNames,
    int LaunchActionCount)
{
    public bool HasAny => JavaScriptCount > 0 || AttachmentCount > 0 || LaunchActionCount > 0;
}

/// <summary>
/// Закладка оглавления PDF: заголовок, целевая страница (-1 — цель не
/// разрешается) и вложенные закладки. Дерево читается целиком при открытии.
/// </summary>
public sealed record PdfBookmark(
    string Title,
    int TargetPageIndex,
    IReadOnlyList<PdfBookmark> Children);

/// <summary>
/// Вложенный в документ файл. Программа умеет только показать его и сохранить
/// на диск по явной команде пользователя — открывать вложения она не будет
/// никогда: это классический способ доставки вредоносного содержимого.
/// </summary>
/// <remarks>
/// Описания у вложения нет намеренно: FPDFAttachment_GetStringValue в
/// поставляемой сборке pdfium возвращает пустую строку и для /Desc, и для
/// ключей /Params (проверено тестом), поэтому поле было бы вечно пустым.
/// </remarks>
public sealed record PdfAttachment(
    int Index,
    string Name,
    long SizeBytes);

/// <summary>Ссылка страницы вместе с её рамкой в координатах страницы PDF (для подсветки и наведения).</summary>
public sealed record PdfPageLink(PdfTextRect RectPt, string? Uri, int TargetPageIndex);

/// <summary>
/// Слово страницы вместе с рамкой и начертанием — сырьё для экспорта в Word и
/// Excel. Рамка в координатах PDF (начало — левый нижний угол, Top &gt; Bottom).
/// В PDF нет ни слов, ни строк, ни таблиц: есть только символы с координатами,
/// поэтому всё остальное приходится восстанавливать по расположению.
/// </summary>
/// <param name="FontSizePt">Кегль в пунктах (уже с учётом матрицы текста).</param>
/// <param name="FontWeight">Вес шрифта: 400 — обычный, 700 — полужирный.</param>
/// <param name="ColorArgb">Цвет заливки текста.</param>
/// <param name="RotationQuarters">
/// Поворот текста в четвертях против часовой стрелки: 0 — обычный, 1 — снизу
/// вверх, 3 — сверху вниз. Без него повёрнутую подпись невозможно прочитать в
/// правильном порядке: слова идут не туда, куда растут координаты.
/// </param>
/// <param name="FontName">
/// Имя шрифта без служебного префикса подмножества и без суффикса начертания —
/// «Times New Roman», а не «ABCDEF+TimesNewRoman,Bold». Пусто, если неизвестно.
/// </param>
public sealed record PdfTextWord(
    string Text,
    PdfTextRect RectPt,
    double FontSizePt,
    int FontWeight,
    uint ColorArgb,
    int RotationQuarters = 0,
    string FontName = "")
{
    /// <summary>Повёрнут ли текст (не горизонтальная строка слева направо).</summary>
    public bool IsRotated => RotationQuarters != 0;

    public bool IsBold => FontWeight >= 600;
    public double Width => RectPt.Right - RectPt.Left;
    public double Height => RectPt.Top - RectPt.Bottom;
    /// <summary>Середина по вертикали — по ней слова собираются в строки.</summary>
    public double CenterY => (RectPt.Top + RectPt.Bottom) / 2.0;
}

/// <summary>
/// Растровое изображение страницы вместе с местом, где оно нарисовано —
/// в координатах PDF. Нужно экспорту: документ без картинок это не документ,
/// а его пересказ.
/// </summary>
public sealed record PdfPageImage(
    byte[] Bgra,
    int PixelWidth,
    int PixelHeight,
    PdfTextRect RectPt);

/// <summary>
/// Заполненное поле формы: имя, значение и рамка на странице.
///
/// Имя берётся из самого виджета; у полей, разложенных на группу (переключатели),
/// оно хранится в родительском объекте и здесь окажется пустым — на перенос
/// значения это не влияет.
/// </summary>
public sealed record PdfFormFieldValue(string Name, string Value, PdfTextRect RectPt);

/// <summary>
/// Нарисованная на странице линия — горизонтальная или вертикальная граница
/// таблицы. В PDF таблицу рисуют либо тонкими штрихами, либо тонкими
/// заливками, поэтому берутся оба случая.
/// </summary>
/// <param name="IsHorizontal">true — горизонтальная, false — вертикальная.</param>
/// <param name="Position">Y для горизонтальной, X для вертикальной (в пунктах PDF).</param>
/// <param name="Start">Начало линии по второй оси.</param>
/// <param name="End">Конец линии по второй оси.</param>
/// <param name="ThicknessPt">Толщина: очень толстые линии — уже не границы, а заливка.</param>
public sealed record PdfRulingLine(
    bool IsHorizontal,
    double Position,
    double Start,
    double End,
    double ThicknessPt)
{
    public double Length => End - Start;
}

/// <summary>Выпадающий список/список формы в точке клика: опции, выбор и рамка поля в отображаемых пунктах.</summary>
public sealed record PdfComboInfo(
    IReadOnlyList<string> Options,
    int SelectedIndex,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt,
    bool IsListBox);

/// <summary>
/// Существующая аннотация документа (для панели комментариев; только чтение).
/// Value — /V для виджетов форм. RectPt — рамка на странице, по ней экспорт
/// понимает, к какому месту текста относится примечание.
/// </summary>
public sealed record PdfAnnotationInfo(
    int AnnotIndex,
    int Subtype,
    string Contents,
    string Author,
    string Value = "",
    PdfTextRect? RectPt = null);

/// <summary>Метаданные документа: версия PDF, шифрование и строки словаря /Info (пустые, если не заданы).</summary>
public sealed record PdfDocumentMetadata(
    string PdfVersion,
    bool IsEncrypted,
    string Title,
    string Author,
    string Subject,
    string Creator,
    string Producer,
    string CreationDate,
    string ModDate);

/// <summary>Итог пересжатия изображений: сколько картинок пересжато и сколько пропущено (прозрачность, факсовые кодеки, низкий DPI).</summary>
public sealed record ImageRecompressStats(int Recompressed, int Skipped);

/// <summary>Страница будущего PDF из изображения: растр BGRA32 и итоговый размер страницы в пунктах.</summary>
public sealed record ImagePageSpec(
    byte[] Bgra,
    int PixelWidth,
    int PixelHeight,
    double WidthPoints,
    double HeightPoints);

/// <summary>Одна страница будущего документа: источник, номер страницы в источнике, добавочный поворот (в четвертях оборота по часовой), накладываемый контент и аннотации источника, помеченные к удалению.</summary>
public sealed record ComposedPage(
    IPdfDocumentHandle Source,
    int SourcePageIndex,
    int ExtraQuarterTurns,
    IReadOnlyList<PageOverlay>? Overlays = null,
    IReadOnlyList<int>? RemovedAnnotations = null);

public sealed record PdfValidationResult(bool IsValid, int PageCount, IReadOnlyList<string> Problems);
