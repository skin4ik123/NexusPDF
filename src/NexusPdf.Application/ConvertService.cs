using System.Text;
using NexusPdf.Export;
using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Конвертация форматов: экспорт страниц в изображения, извлечение текста,
/// сборка PDF из изображений и объединение PDF-файлов. Кодирование растров
/// (PNG/JPEG) — забота вызывающего слоя (WPF-кодеки в приложении и CLI):
/// сервис отдаёт готовые BGRA-растры страниц.
/// </summary>
public sealed class ConvertService
{
    // Экспортный рендер: длинная сторона ограничена, чтобы гигантская
    // страница при 300 DPI не съела память процесса.
    private const int MaxExportSide = 8000;

    private readonly IPdfRenderEngine _engine;

    public ConvertService(IPdfRenderEngine engine) => _engine = engine;

    /// <summary>
    /// Рендерит перечисленные логические страницы (null — все) в растры
    /// заданного DPI и отдаёт каждый в <paramref name="saveAsync"/>
    /// (логический индекс страницы, ФАКТИЧЕСКИЙ DPI растра — он ниже
    /// запрошенного, если гигантскую страницу урезал предел стороны).
    /// Возвращает число страниц.
    /// </summary>
    public async Task<int> ExportImagesAsync(
        OpenedDocument document,
        IReadOnlyList<int>? logicalIndices,
        double dpi,
        Func<RenderedPageImage, int, double, CancellationToken, Task> saveAsync,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        if (dpi is < 24 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(dpi), "DPI должен быть в пределах 24–1200.");
        var targets = logicalIndices ?? Enumerable.Range(0, document.Session.Model.Pages.Count).ToList();
        var done = 0;
        foreach (var logicalIndex in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (logicalIndex < 0 || logicalIndex >= document.Session.Model.Pages.Count)
                continue;
            var size = document.GetLogicalPageSize(logicalIndex);
            var scale = Math.Min(dpi / 72.0,
                MaxExportSide / Math.Max(size.WidthPoints, size.HeightPoints));
            var width = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
            var height = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));
            var image = await document.RenderLogicalPageAsync(logicalIndex, width, height, ct).ConfigureAwait(false);
            await saveAsync(image, logicalIndex, scale * 72.0, ct).ConfigureAwait(false);
            done++;
            progress?.Report((done, targets.Count));
        }
        return done;
    }

    /// <summary>Весь текст документа в логическом порядке страниц, с разделителями страниц.</summary>
    public async Task<string> ExtractTextAsync(OpenedDocument document, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var pages = document.Session.Model.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Текст берётся со страницы С ПРАВКАМИ: экспорт сразу после
            // распознавания не должен молча отдавать пустой исходный лист.
            var (handle, pageIndex) = await document.ResolveTextPageAsync(i, ct).ConfigureAwait(false);
            var text = await handle.GetPageTextAsync(pageIndex, ct).ConfigureAwait(false);
            if (i > 0)
                builder.AppendLine().AppendLine($"===== Страница {i + 1} =====");
            builder.Append(text.ReplaceLineEndings());
            builder.AppendLine();
        }
        return builder.ToString();
    }

    /// <summary>
    /// Документ → книга Excel: таблицы таблицами, числа числами, ссылки
    /// ссылками.
    ///
    /// Разбор идёт по странице С ПРАВКАМИ — по той же, что видит пользователь:
    /// экспорт сразу после распознавания или правки не должен молча отдавать
    /// исходный лист.
    /// </summary>
    public async Task<ExcelExportSummary> ExportToExcelAsync(
        OpenedDocument document,
        string targetPath,
        IReadOnlyList<int>? logicalIndices,
        ExcelExportOptions options,
        PageAnalysisOptions analysis,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var targets = logicalIndices ??
                      Enumerable.Range(0, document.Session.Model.Pages.Count).ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("Нет страниц для экспорта.");

        var pages = new List<ExportPage>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var logical = targets[i];
            var (handle, pageIndex) = await document.ResolveTextPageAsync(logical, ct).ConfigureAwait(false);
            var descriptor = handle.Info.Pages[pageIndex];

            var layout = PageAnalyzer.Analyze(
                logical,
                descriptor.WidthPoints,
                descriptor.HeightPoints,
                await handle.GetTextWordsAsync(pageIndex, ct).ConfigureAwait(false),
                await handle.GetRulingLinesAsync(pageIndex, ct).ConfigureAwait(false),
                analysis.IncludeFormValues
                    ? await handle.GetFormFieldValuesAsync(pageIndex, ct).ConfigureAwait(false)
                    : Array.Empty<PdfFormFieldValue>(),
                analysis);

            var links = options.KeepLinks
                ? await handle.GetPageLinksAsync(pageIndex, ct).ConfigureAwait(false)
                : Array.Empty<PdfPageLink>();

            pages.Add(new ExportPage(layout, links));
            progress?.Report((i + 1, targets.Count));
        }

        return XlsxExporter.Write(targetPath, pages, options);
    }

    /// <summary>
    /// Документ → документ Word: абзацы абзацами, таблицы таблицами, ссылки
    /// ссылками, аннотации примечаниями.
    ///
    /// Страницы обрабатываются и записываются по одной: растр страницы-скана
    /// занимает десятки мегабайт, и собирать весь документ в памяти нельзя.
    /// </summary>
    public async Task<WordExportSummary> ExportToWordAsync(
        OpenedDocument document,
        string targetPath,
        IReadOnlyList<int>? logicalIndices,
        WordExportOptions options,
        PageAnalysisOptions analysis,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var targets = logicalIndices ??
                      Enumerable.Range(0, document.Session.Model.Pages.Count).ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("Нет страниц для экспорта.");

        using var writer = new DocxExporter(targetPath, options);
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var logical = targets[i];
            var (handle, pageIndex) = await document.ResolveTextPageAsync(logical, ct).ConfigureAwait(false);
            var descriptor = handle.Info.Pages[pageIndex];

            var layout = PageAnalyzer.Analyze(
                logical,
                descriptor.WidthPoints,
                descriptor.HeightPoints,
                await handle.GetTextWordsAsync(pageIndex, ct).ConfigureAwait(false),
                await handle.GetRulingLinesAsync(pageIndex, ct).ConfigureAwait(false),
                analysis.IncludeFormValues
                    ? await handle.GetFormFieldValuesAsync(pageIndex, ct).ConfigureAwait(false)
                    : Array.Empty<PdfFormFieldValue>(),
                analysis);

            var links = options.KeepLinks
                ? await handle.GetPageLinksAsync(pageIndex, ct).ConfigureAwait(false)
                : Array.Empty<PdfPageLink>();
            var notes = options.KeepComments
                ? await handle.GetAnnotationsAsync(pageIndex, ct).ConfigureAwait(false)
                : Array.Empty<PdfAnnotationInfo>();
            var images = options.KeepImages
                ? await handle.GetPageImagesAsync(pageIndex, MaxImagePixelsPerPage, ct).ConfigureAwait(false)
                : Array.Empty<PdfPageImage>();

            writer.AddPage(layout, links, notes, images, targets.Count);
            progress?.Report((i + 1, targets.Count));
        }

        writer.Finish();
        return writer.Summary;
    }

    /// <summary>
    /// Предел площади картинок одной страницы. 40 мегапикселей — это скан A4
    /// при 600 dpi; больше на странице не бывает ничего осмысленного, а память
    /// такой растр съедает по 160 МБ.
    /// </summary>
    private const long MaxImagePixelsPerPage = 40_000_000;

    /// <summary>
    /// Объединяет PDF-файлы в один (страницы в порядке перечисления файлов).
    /// Защищённые паролем исходники дают честную ошибку с именем файла.
    /// </summary>
    public async Task<int> MergeAsync(IReadOnlyList<string> sourcePaths, string targetPath, CancellationToken ct)
    {
        if (sourcePaths.Count < 2)
            throw new ArgumentException("Для объединения нужно минимум два файла.");
        var fullTarget = Path.GetFullPath(targetPath);
        if (sourcePaths.Any(s => string.Equals(Path.GetFullPath(s), fullTarget, StringComparison.OrdinalIgnoreCase)))
            throw new PdfEngineException("Файл результата не может совпадать с одним из исходных файлов.");

        var handles = new List<IPdfDocumentHandle>();
        try
        {
            foreach (var path in sourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    handles.Add(await _engine.OpenAsync(path, null, ct).ConfigureAwait(false));
                }
                catch (PdfPasswordRequiredException)
                {
                    throw new PdfEngineException(
                        $"«{Path.GetFileName(path)}» защищён паролем — сначала снимите защиту (Файл → Защитить/Открыть с паролем и сохранить копию).");
                }
            }

            var composition = handles
                .SelectMany(h => Enumerable.Range(0, h.Info.PageCount)
                    .Select(p => new ComposedPage(h, p, 0)))
                .ToList();

            await SafeFileReplace.WriteAndReplaceAsync(
                targetPath,
                tempPath => _engine.ComposeAsync(composition, tempPath, ct),
                async tempPath =>
                {
                    var check = await _engine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);
                    await using (check.ConfigureAwait(false))
                    {
                        if (check.Info.PageCount != composition.Count)
                            throw new PdfEngineException("Проверка объединённого файла: число страниц не совпало.");
                    }
                },
                keepBackup: false,
                ct).ConfigureAwait(false);

            return composition.Count;
        }
        finally
        {
            foreach (var handle in handles)
                await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Собирает PDF из изображений (каждое — страница) с проверкой результата.</summary>
    public async Task CreateFromImagesAsync(IReadOnlyList<ImagePageSpec> images, string targetPath, CancellationToken ct)
    {
        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            tempPath => _engine.CreateImageDocumentAsync(images, tempPath, ct),
            async tempPath =>
            {
                var check = await _engine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);
                await using (check.ConfigureAwait(false))
                {
                    if (check.Info.PageCount != images.Count)
                        throw new PdfEngineException("Проверка собранного PDF: число страниц не совпало.");
                }
            },
            keepBackup: false,
            ct).ConfigureAwait(false);
    }
}
