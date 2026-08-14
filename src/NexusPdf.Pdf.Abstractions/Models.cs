namespace NexusPdf.Pdf.Abstractions;

/// <summary>Размер страницы в пунктах PDF (1/72 дюйма) с учётом /Rotate.</summary>
public sealed record PdfPageDescriptor(double WidthPoints, double HeightPoints);

public sealed record PdfDocumentInfo(int PageCount, IReadOnlyList<PdfPageDescriptor> Pages);

/// <summary>Готовый растр страницы в формате BGRA32, построчно сверху вниз.</summary>
public sealed record RenderedPageImage(int PixelWidth, int PixelHeight, int Stride, byte[] Bgra);

/// <summary>Прямоугольник текста в координатах страницы PDF (начало координат — левый нижний угол, Top &gt; Bottom).</summary>
public sealed record PdfTextRect(double Left, double Top, double Right, double Bottom);

/// <summary>Одна страница будущего документа: источник, номер страницы в источнике и добавочный поворот (в четвертях оборота по часовой).</summary>
public sealed record ComposedPage(IPdfDocumentHandle Source, int SourcePageIndex, int ExtraQuarterTurns);

public sealed record PdfValidationResult(bool IsValid, int PageCount, IReadOnlyList<string> Problems);
