namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Базовый движок: открытие документов, растеризация, текстовый слой и
/// компоновка нового PDF из страниц открытых документов.
/// Все операции асинхронные; реализация обязана быть потокобезопасной для вызывающего.
/// </summary>
public interface IPdfRenderEngine : IAsyncDisposable
{
    string EngineName { get; }

    /// <exception cref="PdfPasswordRequiredException">нужен пароль или пароль неверен</exception>
    /// <exception cref="PdfCorruptedException">файл не открывается как PDF</exception>
    Task<IPdfDocumentHandle> OpenAsync(string filePath, string? password, CancellationToken ct);

    /// <summary>
    /// Собирает новый PDF из перечисленных страниц (в заданном порядке, с добавочными поворотами)
    /// и записывает его в <paramref name="targetPath"/>. Исходные документы не изменяются.
    /// </summary>
    Task ComposeAsync(IReadOnlyList<ComposedPage> pages, string targetPath, CancellationToken ct);
}

public interface IPdfDocumentHandle : IAsyncDisposable
{
    string FilePath { get; }
    PdfDocumentInfo Info { get; }

    /// <param name="extraQuarterTurns">добавочный поворот при отрисовке: 0..3 четверти по часовой</param>
    Task<RenderedPageImage> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns, CancellationToken ct);

    /// <summary>Извлекает весь текст страницы (UTF-16, в порядке текстовых объектов).</summary>
    Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct);

    /// <summary>Прямоугольники, покрывающие диапазон символов страницы (для подсветки найденного).</summary>
    Task<IReadOnlyList<PdfTextRect>> GetTextRectsAsync(int pageIndex, int startCharIndex, int charCount, CancellationToken ct);

    /// <summary>Существующие аннотации страницы (без Link/Popup) — для панели комментариев.</summary>
    Task<IReadOnlyList<PdfAnnotationInfo>> GetAnnotationsAsync(int pageIndex, CancellationToken ct);
}
