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
    private readonly OcrService? _ocr;

    /// <param name="ocr">
    /// Распознавание для страниц-сканов при экспорте. Без него скан честно
    /// считается сканом, но текста из него не будет.
    /// </param>
    public ConvertService(IPdfRenderEngine engine, OcrService? ocr = null)
    {
        _engine = engine;
        _ocr = ocr;
    }

    /// <summary>
    /// Рендерит перечисленные логические страницы (null — все) в растры
    /// заданного DPI и отдаёт каждый в <paramref name="saveAsync"/>
    /// (логический индекс страницы, ФАКТИЧЕСКИЙ DPI растра — он ниже
    /// запрошенного, если гигантскую страницу урезал предел стороны).
    /// Возвращает число страниц.
    ///
    /// Страницы идут ПО ПОРЯДКУ и по одной. Попытка вести сжатие внахлёст с
    /// рендером была измерена и отвергнута: на настоящих кодеках Windows она
    /// дала 0–5 % (в пределах шума), потому что время уходит не на сжатие, а
    /// на сам рендер и запись на диск. Платить за такой выигрыш требованием
    /// потокобезопасности к обработчику и потерей порядка страниц незачем.
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
        var scans = 0;
        var recognized = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ReadPageAsync(document, targets[i], analysis, options.KeepLinks,
                withImages: false, withNotes: false, ct).ConfigureAwait(false);
            if (page.WasScan) scans++;
            if (page.Recognized) recognized++;

            pages.Add(new ExportPage(page.Layout, page.Links));
            progress?.Report((i + 1, targets.Count));
        }

        return XlsxExporter.Write(targetPath, pages, options) with
        {
            ScannedPages = scans,
            RecognizedPages = recognized,
        };
    }

    /// <summary>Разобранная страница вместе со всем, что нужно писателям.</summary>
    private sealed record ReadPage(
        PageLayout Layout,
        IReadOnlyList<PdfPageLink> Links,
        IReadOnlyList<PdfAnnotationInfo> Notes,
        IReadOnlyList<PdfPageImage> Images,
        bool WasScan,
        bool Recognized);

    /// <summary>
    /// Чтение одной страницы для экспорта: геометрия приводится к тому виду, в
    /// каком страницу видит человек, а страница-скан при необходимости
    /// распознаётся.
    /// </summary>
    private async Task<ReadPage> ReadPageAsync(
        OpenedDocument document, int logical, PageAnalysisOptions analysis,
        bool keepLinks, bool withImages, bool withNotes, CancellationToken ct)
    {
        var (handle, pageIndex) = await document.ResolveTextPageAsync(logical, ct).ConfigureAwait(false);
        var descriptor = handle.Info.Pages[pageIndex];

        // Размер движок отдаёт уже с учётом /Rotate, а координаты объектов —
        // нет. Поэтому размер разворачивается обратно в «сырой», объекты
        // поворачиваются вперёд, и всё сходится в одной системе координат.
        var ownRotation = await handle.GetPageRotationAsync(pageIndex, ct).ConfigureAwait(false);
        var baked = document.GetOverlaySignature(logical) != 0;
        var extra = baked ? 0 : document.Session.Model.Pages[logical].RotationOffset;
        var rotation = PageRotation.Normalize(ownRotation + extra);

        var (rawWidth, rawHeight) = PageRotation.Size(
            descriptor.WidthPoints, descriptor.HeightPoints, ownRotation);
        var (width, height) = PageRotation.Size(rawWidth, rawHeight, rotation);

        var words = (await handle.GetTextWordsAsync(pageIndex, ct).ConfigureAwait(false))
            .Select(w => PageRotation.Word(w, rotation, rawWidth, rawHeight)).ToList();
        var rulings = (await handle.GetRulingLinesAsync(pageIndex, ct).ConfigureAwait(false))
            .Select(r => PageRotation.Ruling(r, rotation, rawWidth, rawHeight)).ToList();
        var fields = analysis.IncludeFormValues
            ? (await handle.GetFormFieldValuesAsync(pageIndex, ct).ConfigureAwait(false))
                .Select(f => PageRotation.Field(f, rotation, rawWidth, rawHeight)).ToList()
            : new List<PdfFormFieldValue>();
        var links = keepLinks
            ? (await handle.GetPageLinksAsync(pageIndex, ct).ConfigureAwait(false))
                .Select(l => PageRotation.Link(l, rotation, rawWidth, rawHeight)).ToList()
            : new List<PdfPageLink>();

        var images = withImages
            ? (await handle.GetPageImagesAsync(pageIndex, MaxImagePixelsPerPage, ct).ConfigureAwait(false))
                .Select(i => PageRotation.Image(i, rotation, rawWidth, rawHeight)).ToList()
            : new List<PdfPageImage>();
        var notes = withNotes
            ? (await handle.GetAnnotationsAsync(pageIndex, ct).ConfigureAwait(false))
                .Select(n => PageRotation.Annotation(n, rotation, rawWidth, rawHeight)).ToList()
            : new List<PdfAnnotationInfo>();

        // Рамки картинок нужны всегда: по ним отличают скан от по-настоящему
        // пустой страницы. Пиксели для этого не декодируются.
        var bounds = withImages
            ? images.Select(i => i.RectPt).ToList()
            : (await handle.GetPageImageBoundsAsync(pageIndex, ct).ConfigureAwait(false))
                .Select(r => PageRotation.Rect(r, rotation, rawWidth, rawHeight)).ToList();

        var kind = ScannedPageDetector.Classify(words, bounds, width, height);
        var recognized = false;
        if (kind.IsScan && analysis.RecognizeScans && _ocr is { IsAvailable: true })
        {
            var boxes = await _ocr.RecognizePageWordsAsync(document, logical, ct).ConfigureAwait(false);
            var fromScan = ScannedPageDetector.FromRecognized(boxes, height);
            if (fromScan.Count > 0)
            {
                words = fromScan.ToList();
                recognized = true;
            }
        }

        var layout = PageAnalyzer.Analyze(logical, width, height, words, rulings, fields, analysis);
        return new ReadPage(layout, links, notes, images, kind.IsScan, recognized);
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
        var scans = 0;
        var recognized = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ReadPageAsync(document, targets[i], analysis, options.KeepLinks,
                options.KeepImages, options.KeepComments, ct).ConfigureAwait(false);
            if (page.WasScan) scans++;
            if (page.Recognized) recognized++;

            writer.AddPage(page.Layout, page.Links, page.Notes, page.Images, targets.Count);
            progress?.Report((i + 1, targets.Count));
        }

        writer.Finish();
        return writer.Summary with { ScannedPages = scans, RecognizedPages = recognized };
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
