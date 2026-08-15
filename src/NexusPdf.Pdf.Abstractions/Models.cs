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

/// <summary>Распознанное OCR слово: рамка в отображаемых пунктах (от левого верхнего угла на момент распознавания).</summary>
public sealed record OcrWordBox(
    string Text,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt);

/// <summary>
/// Невидимый текстовый слой поверх скана (результат OCR): каждое слово
/// запекается невидимым текстовым объектом, растянутым по своей рамке —
/// поиск/копирование работают, изображение страницы не меняется.
/// </summary>
public sealed record OcrTextLayerOverlay(IReadOnlyList<OcrWordBox> Words) : PageOverlay;

/// <summary>Выпадающий список/список формы в точке клика: опции, выбор и рамка поля в отображаемых пунктах.</summary>
public sealed record PdfComboInfo(
    IReadOnlyList<string> Options,
    int SelectedIndex,
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt,
    bool IsListBox);

/// <summary>Существующая аннотация документа (для панели комментариев; только чтение). Value — /V для виджетов форм.</summary>
public sealed record PdfAnnotationInfo(
    int AnnotIndex,
    int Subtype,
    string Contents,
    string Author,
    string Value = "");

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
