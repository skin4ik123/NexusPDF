namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Кодирование изображения выбранным способом. Реализует слой интерфейса
/// (у него есть кодеки системы), а РЕШЕНИЕ, чем кодировать, принимает движок
/// по самой картинке.
/// </summary>
public delegate byte[] EncodeImage(byte[] bgra, int width, int height, ImageEncodingChoice choice);

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

    /// <summary>
    /// Растр страницы С ПРИМЕНЁННЫМИ правками — тем же кодом запекания, что и
    /// при сохранении. Нужен, чтобы экран показывал именно то, что попадёт в
    /// файл: рисовать правки приближениями поверх страницы значит показывать
    /// не результат, а его имитацию (а для части правок — вообще ничего).
    /// </summary>
    /// <summary>
    /// Дескриптор ОДНОЙ страницы с применёнными правками (документ из одной
    /// страницы в памяти). Через него работают обычные операции с текстом,
    /// поэтому распознанный или добавленный текст ищется и выделяется сразу,
    /// без сохранения файла. Освобождать через DisposeAsync.
    /// </summary>
    Task<IPdfDocumentHandle> CreateBakedPageAsync(
        IPdfDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, CancellationToken ct);

    /// <remarks>
    /// Удаление существующих аннотаций здесь НЕ применяется: при сохранении оно
    /// разворачивает связанные popup-объекты, и повторять эту логику ради
    /// картинки значит держать две копии тонкого кода. Удалённые комментарии
    /// пропадают из панели комментариев сразу — там пользователь их и видит.
    /// </remarks>
    Task<RenderedPageImage> RenderPageWithOverlaysAsync(
        IPdfDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, int pixelWidth, int pixelHeight, CancellationToken ct);

    /// <summary>
    /// ТОЛЬКО правки страницы — на прозрачном фоне, в той же рамке и том же
    /// размере, что и обычный растр страницы. Накладывается поверх готового
    /// растра: добавленная пометка не требует перерисовывать содержимое
    /// страницы, которое на тяжёлом чертеже рисуется секундами.
    /// Годится для правок, которые содержимое не меняют (см.
    /// <see cref="OverlayLayers.IsDrawnOnTop"/>).
    /// </summary>
    Task<RenderedPageImage> RenderOverlayLayerAsync(
        IPdfDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, int pixelWidth, int pixelHeight, CancellationToken ct);

    /// <summary>
    /// Сохранение набора страниц как ПРАВКИ копии базового документа (а не
    /// сборкой нового): страницы базы переиспользуются, лишние удаляются,
    /// вставленные импортируются, порядок выстраивается перестановкой. Пароль,
    /// закладки, форма, вложения и свойства документа остаются. Копия
    /// <paramref name="workingCopy"/> одноразовая и после вызова не годится.
    /// </summary>
    Task SaveCompositionInPlaceAsync(
        IPdfDocumentHandle workingCopy, IPdfDocumentHandle baseSource,
        IReadOnlyList<ComposedPage> composition, string targetPath,
        InPlaceSaveOptions options, CancellationToken ct);

    /// <summary>Создаёт новый PDF, где каждая страница — одно изображение, растянутое на всю страницу.</summary>
    Task CreateImageDocumentAsync(IReadOnlyList<ImagePageSpec> pages, string targetPath, CancellationToken ct);

    /// <summary>
    /// Копия файла с пересжатыми изображениями: картинки с эффективным DPI выше
    /// целевого уменьшаются и кодируются заново (кодек даёт вызывающий).
    /// Прозрачные и факсовые (CCITT/JBIG2/JPX) изображения пропускаются.
    /// </summary>
    Task<ImageRecompressStats> RecompressImagesAsync(
        string sourcePath, string? password, string targetPath, double targetDpi,
        EncodeImage encode, CancellationToken ct);

    /// <summary>
    /// То же с явным качеством JPEG: способ кодирования выбирается для каждого
    /// изображения отдельно, и качество нужно знать до вызова кодека.
    /// </summary>
    Task<ImageRecompressStats> RecompressImagesAsync(
        string sourcePath, string? password, string targetPath, double targetDpi,
        int quality, EncodeImage encode, CancellationToken ct);

    /// <summary>
    /// Копия файла с выровненными и вычищенными сканами. Наклон исправляется
    /// поворотом содержимого страницы, поэтому текстовый слой распознавания и
    /// качество не теряются.
    /// </summary>
    Task<ScanEnhanceStats> EnhanceScansAsync(
        string sourcePath, string? password, string targetPath,
        ScanEnhanceOptions options, IProgress<int>? progress, CancellationToken ct);

    /// <summary>Найденный наклон страниц БЕЗ изменения файла — для предпросмотра.</summary>
    Task<IReadOnlyList<PageSkew>> MeasureSkewAsync(
        string sourcePath, string? password, IReadOnlyList<int>? pages, CancellationToken ct);
}

public interface IPdfDocumentHandle : IAsyncDisposable
{
    string FilePath { get; }
    PdfDocumentInfo Info { get; }

    /// <param name="extraQuarterTurns">добавочный поворот при отрисовке: 0..3 четверти по часовой</param>
    Task<RenderedPageImage> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns, CancellationToken ct);

    /// <summary>Рендер ТОЛЬКО содержимого страницы — без аннотаций и полей форм (растр для OCR).</summary>
    Task<RenderedPageImage> RenderPageContentOnlyAsync(int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns, CancellationToken ct);

    /// <summary>
    /// Рендер для ПЕЧАТИ: состав содержимого задаёт вызывающий.
    ///
    /// Отдельно от обычного рендера, потому что на бумаге правила другие:
    /// комментарий, помеченный автором как экранный, туда не идёт, а пустой
    /// бланк печатают без введённых значений.
    /// </summary>
    Task<RenderedPageImage> RenderPageForPrintAsync(
        int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns,
        PrintContentOptions options, CancellationToken ct);

    /// <summary>Извлекает весь текст страницы (UTF-16, в порядке текстовых объектов).</summary>
    Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct);

    /// <summary>
    /// Что за документ: скан или вёрстка. Смотрит первые <paramref name="maxPages"/>
    /// страниц — дальше выводы уже не меняются.
    /// </summary>
    Task<PdfImageSummary> GetImageSummaryAsync(int maxPages, CancellationToken ct);

    /// <summary>Метаданные документа: версия PDF и словарь /Info.</summary>
    Task<PdfDocumentMetadata> GetMetadataAsync(CancellationToken ct);

    /// <summary>
    /// Активное содержимое (JavaScript, вложения, Launch-действия). Программа
    /// его не выполняет; сведения нужны, чтобы честно предупредить пользователя.
    /// </summary>
    Task<PdfActiveContent> GetActiveContentAsync(CancellationToken ct);

    /// <summary>Прямоугольники, покрывающие диапазон символов страницы (для подсветки найденного).</summary>
    Task<IReadOnlyList<PdfTextRect>> GetTextRectsAsync(int pageIndex, int startCharIndex, int charCount, CancellationToken ct);

    /// <summary>
    /// Слова страницы с рамками и начертанием — для экспорта в Word и Excel.
    /// Собираются из символов: в PDF слов как таковых нет.
    /// </summary>
    Task<IReadOnlyList<PdfTextWord>> GetTextWordsAsync(int pageIndex, CancellationToken ct);

    /// <summary>
    /// Горизонтальные и вертикальные линии страницы — нарисованные границы
    /// таблиц. По ним таблица восстанавливается точно, без догадок по пробелам.
    /// </summary>
    Task<IReadOnlyList<PdfRulingLine>> GetRulingLinesAsync(int pageIndex, CancellationToken ct);

    /// <summary>
    /// Заполненные поля формы с их рамками. В текст страницы значения полей не
    /// входят, поэтому без этого экспорт анкеты вышел бы пустым бланком.
    /// </summary>
    Task<IReadOnlyList<PdfFormFieldValue>> GetFormFieldValuesAsync(int pageIndex, CancellationToken ct);

    /// <summary>
    /// Все видимые отрезки векторной графики страницы (кривые разложены на
    /// отрезки) в отображаемых пунктах — для привязки курсора при измерении.
    /// На чертеже из САПР их сотни тысяч; читаются один раз на страницу.
    /// </summary>
    Task<IReadOnlyList<PdfSegment>> GetPageSegmentsAsync(int pageIndex, int extraQuarterTurns, CancellationToken ct);

    /// <summary>
    /// Картинки страницы с их местом — для экспорта в Word. Крошечные
    /// отбрасываются, суммарная площадь ограничена <paramref name="maxPixels"/>.
    /// </summary>
    Task<IReadOnlyList<PdfPageImage>> GetPageImagesAsync(int pageIndex, long maxPixels, CancellationToken ct);

    /// <summary>
    /// Рамки картинок страницы без самих пикселей — по ним отличают скан от
    /// пустой страницы, не декодируя многомегапиксельный растр.
    /// </summary>
    Task<IReadOnlyList<PdfTextRect>> GetPageImageBoundsAsync(int pageIndex, CancellationToken ct);

    /// <summary>
    /// Поворот страницы (/Rotate) в четвертях по часовой. Размер страницы
    /// движок отдаёт уже повёрнутым, а координаты объектов — нет, поэтому
    /// экспорту нужно знать поворот отдельно.
    /// </summary>
    Task<int> GetPageRotationAsync(int pageIndex, CancellationToken ct);

    /// <summary>
    /// Индекс символа в точке (отображаемые пункты от левого верхнего угла)
    /// или -1, если в точке текста нет. Нужен для выделения текста мышью.
    /// </summary>
    Task<int> GetCharIndexAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>Ссылка PDF в точке (отображаемые пункты) или null.</summary>
    Task<PdfLinkInfo?> GetLinkAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>
    /// Растровое изображение страницы под точкой (отображаемые пункты) вместе
    /// с его пикселями — для правки во внешнем редакторе. null, если в точке
    /// изображения нет.
    /// </summary>
    Task<PdfImageObject?> GetImageObjectAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>Вложенные в документ файлы (имя, описание, размер). Пустой список — вложений нет.</summary>
    Task<IReadOnlyList<PdfAttachment>> GetAttachmentsAsync(CancellationToken ct);

    /// <summary>
    /// Содержимое вложения. Файл на диск программа не пишет и не открывает —
    /// это решает вызывающий код после явного согласия пользователя.
    /// </summary>
    Task<byte[]> ReadAttachmentAsync(int index, CancellationToken ct);

    /// <summary>Текстовый объект страницы под точкой (отображаемые пункты) или null.</summary>
    Task<PdfTextObject?> GetTextObjectAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>
    /// Может ли шрифт указанного текстового объекта нарисовать переданный
    /// текст. Проверяется НЕ по таблицам, а рисованием пробного объекта тем же
    /// шрифтом: у встроенного подмножества нужных букв может не оказаться, и
    /// тогда правка молча дала бы пустое место.
    /// </summary>
    Task<bool> CanFontRenderTextAsync(
        int pageIndex, IReadOnlyList<int> objectPath, string text, CancellationToken ct);

    /// <summary>
    /// Все ссылки страницы с рамками в координатах страницы PDF. Читаются один
    /// раз на страницу: попадание курсора проверяется без обращения к движку.
    /// </summary>
    Task<IReadOnlyList<PdfPageLink>> GetPageLinksAsync(int pageIndex, CancellationToken ct);

    /// <summary>
    /// Дерево закладок (оглавление) документа. Пустой список — оглавления нет.
    /// Номера страниц — индексы в ИСХОДНОМ документе.
    /// </summary>
    Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken ct);

    /// <summary>Существующие аннотации страницы (без Link/Popup) — для панели комментариев.</summary>
    Task<IReadOnlyList<PdfAnnotationInfo>> GetAnnotationsAsync(int pageIndex, CancellationToken ct);

    // ----- Разрешения документа -----

    /// <summary>
    /// Флаги разрешений из словаря шифрования PDF. Значение 0xFFFFFFFF означает,
    /// что документ не защищён и разрешено всё. Используется, чтобы соблюдать
    /// запрет печати, а не обходить его.
    /// </summary>
    Task<uint> GetPermissionsAsync(CancellationToken ct);

    // ----- Интерактивные формы (AcroForm) -----

    /// <summary>
    /// Тип формы (FPDF_GetFormType). 0 — форм нет, 1 — AcroForm.
    /// 2 — целиком динамическая XFA: не отображается, на страницах только то,
    /// что в них нарисовано без XFA. 3 — гибридная (AcroForm + XFA):
    /// показывается, печатается и заполняется как AcroForm, а при сохранении
    /// изменённые значения записываются и в XFA-данные (их читает Adobe Reader).
    /// </summary>
    Task<int> GetFormTypeAsync(CancellationToken ct);

    /// <summary>
    /// Включает режим заполнения форм. false — форм нет, форма динамическая XFA
    /// или XFA-часть гибридной формы не читается.
    /// </summary>
    Task<bool> InitFormsAsync(CancellationToken ct);

    /// <summary>
    /// Клик по странице в отображаемых координатах (пункты от левого верхнего
    /// угла). Возвращает вид поля, по которому щёлкнули (Unknown — мимо полей):
    /// щелчок по флажку меняет значение, а по текстовому полю — ещё нет.
    /// </summary>
    Task<PdfFormFieldKind> FormClickAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>
    /// Поля формы страницы с рамками в отображаемых пунктах. Скрытые поля и
    /// поля подписи не входят: щёлкать по ним незачем.
    /// </summary>
    Task<IReadOnlyList<PdfFormFieldArea>> GetFormFieldAreasAsync(int pageIndex, int extraQuarterTurns, CancellationToken ct);

    /// <summary>Выпадающий список/список в точке (null — обычное поле): опции для собственного попапа.</summary>
    Task<PdfComboInfo?> GetFormComboAtAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct);

    /// <summary>Выбор пункта выпадающего списка в точке (фокус кликом + выбор индекса).</summary>
    Task SetFormComboSelectionAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, int optionIndex, CancellationToken ct);

    /// <summary>Ввод символа в сфокусированное поле (Backspace — символ 8).</summary>
    Task FormCharAsync(char character, CancellationToken ct);

    /// <summary>
    /// Клавиша (Windows VK-код: стрелки, Delete, Home/End, Tab) в
    /// сфокусированное поле; shift — для Shift+Tab, перехода к предыдущему полю.
    /// </summary>
    Task FormKeyDownAsync(int virtualKeyCode, bool shift, CancellationToken ct);

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

    /// <param name="incremental">
    /// Дописать изменения к исходным байтам файла: так сохраняются цифровые
    /// подписи, которые полная перезапись делает недействительными.
    /// </param>
    Task SaveCurrentAsync(string targetPath, bool incremental, CancellationToken ct);

    /// <summary>
    /// В открытом документе есть изменения, которых нет в его файле: значения
    /// полей формы, введённые в этой сессии. Сохранение берёт копию документа
    /// из памяти только тогда, когда это действительно нужно.
    /// </summary>
    bool HasUnsavedFormChanges { get; }

    /// <summary>Растёт при каждом изменении значения поля формы в этом документе.</summary>
    long FormChangeStamp { get; }

    /// <summary>
    /// То же прямое сохранение, но с наложением правок на страницы ЭТОГО
    /// документа.
    ///
    /// Зачем отдельный путь: перекомпоновка собирает новый файл из импортов
    /// страниц и не переносит то, что живёт в каталоге документа, — форму
    /// AcroForm, оглавление, вложения. Стоило добавить на бланк одну надпись,
    /// и заполняемая форма превращалась в картинку с мёртвыми полями. Здесь
    /// правки ложатся на страницы уже открытого документа, и всё остальное
    /// остаётся нетронутым.
    ///
    /// ВАЖНО: документ в памяти после этого ИЗМЕНЁН. Вызывающий обязан либо
    /// переоткрыть его из сохранённого файла, либо вернуть к исходному —
    /// иначе повторное сохранение наложит те же правки второй раз.
    /// </summary>
    /// <param name="overlaysByPage">Правки по номеру страницы; страницы без правок можно не перечислять.</param>
    Task SaveWithOverlaysAsync(
        IReadOnlyDictionary<int, IReadOnlyList<PageOverlay>> overlaysByPage,
        string targetPath, CancellationToken ct);
}
