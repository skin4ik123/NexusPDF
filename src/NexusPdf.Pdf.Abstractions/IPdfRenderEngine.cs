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

    /// <summary>Создаёт новый PDF, где каждая страница — одно изображение, растянутое на всю страницу.</summary>
    Task CreateImageDocumentAsync(IReadOnlyList<ImagePageSpec> pages, string targetPath, CancellationToken ct);

    /// <summary>
    /// Копия файла с пересжатыми изображениями: картинки с эффективным DPI выше
    /// целевого уменьшаются и кодируются в JPEG (кодек даёт вызывающий).
    /// Прозрачные и факсовые (CCITT/JBIG2/JPX) изображения пропускаются.
    /// </summary>
    Task<ImageRecompressStats> RecompressImagesAsync(
        string sourcePath, string? password, string targetPath, double targetDpi,
        Func<byte[], int, int, byte[]> encodeJpeg, CancellationToken ct);
}

public interface IPdfDocumentHandle : IAsyncDisposable
{
    string FilePath { get; }
    PdfDocumentInfo Info { get; }

    /// <param name="extraQuarterTurns">добавочный поворот при отрисовке: 0..3 четверти по часовой</param>
    Task<RenderedPageImage> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns, CancellationToken ct);

    /// <summary>Рендер ТОЛЬКО содержимого страницы — без аннотаций и полей форм (растр для OCR).</summary>
    Task<RenderedPageImage> RenderPageContentOnlyAsync(int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns, CancellationToken ct);

    /// <summary>Извлекает весь текст страницы (UTF-16, в порядке текстовых объектов).</summary>
    Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct);

    /// <summary>Метаданные документа: версия PDF и словарь /Info.</summary>
    Task<PdfDocumentMetadata> GetMetadataAsync(CancellationToken ct);

    /// <summary>Прямоугольники, покрывающие диапазон символов страницы (для подсветки найденного).</summary>
    Task<IReadOnlyList<PdfTextRect>> GetTextRectsAsync(int pageIndex, int startCharIndex, int charCount, CancellationToken ct);

    /// <summary>
    /// Индекс символа в точке (отображаемые пункты от левого верхнего угла)
    /// или -1, если в точке текста нет. Нужен для выделения текста мышью.
    /// </summary>
    Task<int> GetCharIndexAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>Ссылка PDF в точке (отображаемые пункты) или null.</summary>
    Task<PdfLinkInfo?> GetLinkAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>
    /// Все ссылки страницы с рамками в координатах страницы PDF. Читаются один
    /// раз на страницу: попадание курсора проверяется без обращения к движку.
    /// </summary>
    Task<IReadOnlyList<PdfPageLink>> GetPageLinksAsync(int pageIndex, CancellationToken ct);

    /// <summary>Существующие аннотации страницы (без Link/Popup) — для панели комментариев.</summary>
    Task<IReadOnlyList<PdfAnnotationInfo>> GetAnnotationsAsync(int pageIndex, CancellationToken ct);

    // ----- Интерактивные формы (AcroForm) -----

    /// <summary>0 — форм нет, 1 — AcroForm, 2/3 — XFA (не поддерживается для заполнения).</summary>
    Task<int> GetFormTypeAsync(CancellationToken ct);

    /// <summary>Включает окружение заполнения форм. false — формы отсутствуют/не поддерживаются.</summary>
    Task<bool> InitFormsAsync(CancellationToken ct);

    /// <summary>Клик по странице в отображаемых координатах (пункты от левого верхнего угла).</summary>
    Task FormClickAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>Выпадающий список/список в точке (null — обычное поле): опции для собственного попапа.</summary>
    Task<PdfComboInfo?> GetFormComboAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>Выбор пункта выпадающего списка в точке (фокус кликом + выбор индекса).</summary>
    Task SetFormComboSelectionAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, int optionIndex, CancellationToken ct);

    /// <summary>Ввод символа в сфокусированное поле (Backspace — символ 8).</summary>
    Task FormCharAsync(char character, CancellationToken ct);

    /// <summary>Клавиша (Windows VK-код: стрелки, Delete, Home/End) в сфокусированное поле.</summary>
    Task FormKeyDownAsync(int virtualKeyCode, CancellationToken ct);

    /// <summary>Снять фокус с поля (фиксирует введённое значение).</summary>
    Task FormKillFocusAsync(CancellationToken ct);

    /// <summary>
    /// Завершает форм-окружение: фиксирует значения, убирает подсветку полей
    /// из последующих рендеров (включая печать). Повторный InitFormsAsync
    /// создаёт окружение заново.
    /// </summary>
    Task FormEndAsync(CancellationToken ct);

    /// <summary>
    /// Прямое сохранение ТЕКУЩЕГО документа (включая значения форм, закладки и
    /// всю неизменённую структуру) без перекомпоновки страниц.
    /// </summary>
    Task SaveCurrentAsync(string targetPath, CancellationToken ct);
}
