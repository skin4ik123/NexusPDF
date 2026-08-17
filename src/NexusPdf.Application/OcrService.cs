using NexusPdf.Domain;
using NexusPdf.Ocr;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

public sealed record OcrProgress(int PagesDone, int TotalPages, int WordsSoFar);

public sealed record OcrRunResult(
    int PagesRecognized,
    int PagesSkippedWithText,
    int PagesWithoutWords,
    int WordCount,
    double MeanConfidence,
    bool Cancelled,
    string? Error);

/// <summary>
/// Распознавание текста сканов: страница рендерится в высоком разрешении
/// (только содержимое — без аннотаций и полей форм), распознаётся Tesseract
/// (rus+eng), и на неё накладывается невидимый текстовый слой
/// (<see cref="OcrTextLayerOverlay"/>) — поиск и копирование начинают работать
/// после сохранения. Страницы с ЛЮБЫМ уже имеющимся текстом пропускаются:
/// повторное распознавание наложило бы невидимый дубль поверх настоящего
/// текста, и копирование возвращало бы каждое слово дважды.
/// Каждая страница — отдельная операция сессии, отменяемая Ctrl+Z.
/// </summary>
public sealed class OcrService
{
    // Рендер под распознавание: целимся в 300 DPI, но длинную сторону
    // ограничиваем, чтобы гигантские страницы не съедали память.
    private const double TargetDpi = 300.0;
    private const int MaxRenderSide = 3500;

    // Слова с уверенностью ниже порога — почти всегда шум сканирования.
    private const float MinWordConfidence = 35f;

    // Для РЕДАКТИРУЕМОГО текста порог выше, и это не придирчивость. Невидимый
    // слой поиска ничего не портит: неверно распознанное слово просто не
    // найдётся. А редактируемый текст ЗАКРЫВАЕТ оригинал — и строка, угаданная
    // наполовину, встаёт вместо того, что под ней было. Выше не поднимаем:
    // на обычном сканe уверенность держится около 70–90, и слишком строгий
    // порог оставил бы страницу почти нетронутой.
    private const float MinEditableConfidence = 60f;

    private readonly ITextRecognizer _ocr;

    public OcrService(ITextRecognizer ocr) => _ocr = ocr;

    /// <summary>Название работающего движка — для журнала и интерфейса.</summary>
    public string EngineName => _ocr.DisplayName;

    public bool IsAvailable => _ocr.IsAvailable;
    public string? UnavailableReason => _ocr.UnavailableReason;

    /// <summary>
    /// Распознаёт перечисленные логические страницы документа.
    /// null — все страницы. Вызывать с UI-потока: операции сессии
    /// применяются в контексте вызывающего. Исключения середины прогона не
    /// теряют частичный результат — он возвращается вместе с текстом ошибки.
    /// </summary>
    /// <param name="editableText">
    /// false — невидимый слой поверх скана: вид страницы не меняется, текст
    /// доступен для поиска и копирования. true — распознанное заменяет текст
    /// скана НАСТОЯЩИМ видимым текстом, который можно править; начертание
    /// оригинала при этом теряется.
    /// </param>
    /// <summary>
    /// Распознаёт страницу и отдаёт слова, НИЧЕГО не меняя в документе.
    ///
    /// Нужно экспорту: чтобы выгрузить скан в Word или Excel, распознанный
    /// текст требуется здесь и сейчас, а вот право дописать в чужой документ
    /// невидимый слой экспорт не имеет — это отдельное решение пользователя.
    ///
    /// Рамки — в отображаемых пунктах текущей рамки страницы, от левого
    /// ВЕРХНЕГО угла.
    /// </summary>
    public async Task<IReadOnlyList<OcrWordBox>> RecognizePageWordsAsync(
        OpenedDocument document, int logicalIndex, CancellationToken ct)
    {
        var size = document.GetLogicalPageSize(logicalIndex);
        var scale = Math.Min(TargetDpi / 72.0,
            MaxRenderSide / Math.Max(size.WidthPoints, size.HeightPoints));
        var pixelWidth = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));
        var dpi = (int)Math.Round(scale * 72.0);

        var image = await document.RenderLogicalPageContentOnlyAsync(
            logicalIndex, pixelWidth, pixelHeight, ct).ConfigureAwait(false);
        var result = await _ocr.RecognizeAsync(image, dpi, ct).ConfigureAwait(false);

        var ptPerPxX = size.WidthPoints / pixelWidth;
        var ptPerPxY = size.HeightPoints / pixelHeight;
        return result.Words
            .Where(w => w.Confidence >= MinWordConfidence)
            .Select(w => new OcrWordBox(
                w.Text, w.X * ptPerPxX, w.Y * ptPerPxY, w.Width * ptPerPxX, w.Height * ptPerPxY))
            .ToList();
    }

    public async Task<OcrRunResult> RecognizeAsync(
        OpenedDocument document,
        IReadOnlyList<int>? logicalIndices,
        IProgress<OcrProgress>? progress,
        CancellationToken ct,
        bool editableText = false)
    {
        var targets = logicalIndices ?? Enumerable.Range(0, document.Session.Model.Pages.Count).ToList();
        var recognized = 0;
        var skipped = 0;
        var withoutWords = 0;
        var totalWords = 0;
        double confidenceSum = 0;
        var cancelled = false;
        string? error = null;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var logicalIndex = targets[i];
                if (logicalIndex < 0 || logicalIndex >= document.Session.Model.Pages.Count)
                    continue;

                if (await PageAlreadyHasTextAsync(document, logicalIndex, ct))
                {
                    skipped++;
                    progress?.Report(new OcrProgress(i + 1, targets.Count, totalWords));
                    continue;
                }

                var size = document.GetLogicalPageSize(logicalIndex);
                var scale = Math.Min(TargetDpi / 72.0,
                    MaxRenderSide / Math.Max(size.WidthPoints, size.HeightPoints));
                var pixelWidth = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
                var pixelHeight = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));
                var dpi = (int)Math.Round(scale * 72.0);

                var image = await document.RenderLogicalPageContentOnlyAsync(logicalIndex, pixelWidth, pixelHeight, ct);
                var result = await _ocr.RecognizeAsync(image, dpi, ct);

                // Пиксели растра → отображаемые пункты текущей рамки страницы.
                var ptPerPxX = size.WidthPoints / pixelWidth;
                var ptPerPxY = size.HeightPoints / pixelHeight;
                var minConfidence = editableText ? MinEditableConfidence : MinWordConfidence;
                var kept = result.Words.Where(w => w.Confidence >= minConfidence).ToList();
                var words = kept
                    .Select(w => new OcrWordBox(
                        w.Text,
                        w.X * ptPerPxX,
                        w.Y * ptPerPxY,
                        w.Width * ptPerPxX,
                        w.Height * ptPerPxY))
                    .ToList();

                if (words.Count > 0)
                {
                    // Отмена, запрошенная во время распознавания страницы,
                    // не должна добавить её слой уже после нажатия «Отмена».
                    ct.ThrowIfCancellationRequested();

                    PageOverlay layer;
                    if (editableText)
                    {
                        // Слова собираются в строки, а всё остальное строка
                        // берёт с самого скана: цвет бумаги и чернил, гарнитуру
                        // под начертание оригинала и кусочек фона, которым
                        // закроется прежний текст.
                        var pxPerPtX = pixelWidth / size.WidthPoints;
                        var pxPerPtY = pixelHeight / size.HeightPoints;
                        var lines = OcrLineBuilder.BuildLines(words, _ocr.ReturnsWholeLines)
                            .Select(line =>
                            {
                                var background = OcrLineBuilder.SampleBackground(
                                    image, pxPerPtX, pxPerPtY, line);
                                var ink = OcrLineBuilder.SampleInk(
                                    image, pxPerPtX, pxPerPtY, line, background);
                                var measured = line with { BackgroundArgb = background, InkArgb = ink };

                                // Заплатка строится первой: попутно она находит
                                // фактическую полосу букв, и уже по ней (а не по
                                // завышенной рамке) определяется начертание.
                                var patch = OcrLinePatchBuilder.Build(image, pxPerPtX, pxPerPtY, measured);
                                var guess = OcrFontGuesser.Of(
                                    image, pxPerPtX, pxPerPtY, measured, patch?.YPt, patch?.HeightPt);

                                // Рамка от распознавания по вертикали смещена и
                                // завышена, а найденная полоса букв — это ровно
                                // то место, где текст стоял. По ней и ставим
                                // замену: иначе строка садится ниже прежней и
                                // набирается не своим кеглем.
                                if (patch != null)
                                    measured = measured with { YPt = patch.YPt, HeightPt = patch.HeightPt };

                                return measured with
                                {
                                    FontFamily = guess.Family,
                                    Bold = guess.Bold,
                                    Patch = patch,
                                };
                            })
                            .ToList();
                        layer = new OcrEditableTextOverlay(lines);
                    }
                    else
                    {
                        layer = new OcrTextLayerOverlay(words);
                    }

                    document.Session.Apply(new AddOverlayOperation(logicalIndex, layer));
                    recognized++;
                    totalWords += words.Count;
                    confidenceSum += kept.Sum(w => (double)w.Confidence);
                }
                else
                {
                    withoutWords++; // пустой скан/фото: честно отдельный счётчик
                }

                progress?.Report(new OcrProgress(i + 1, targets.Count, totalWords));
            }
        }
        catch (OperationCanceledException)
        {
            // Уже распознанные страницы остаются (каждая — отдельный Undo).
            cancelled = true;
        }
        catch (Exception ex)
        {
            // Частичный результат не теряется: вызывающий покажет и ошибку,
            // и сколько страниц уже получили слой.
            error = ex.Message;
        }

        return new OcrRunResult(
            recognized, skipped, withoutWords, totalWords,
            totalWords > 0 ? confidenceSum / totalWords : 0,
            cancelled, error);
    }

    /// <summary>
    /// Есть ли на странице текст — настоящий (любой длины) или уже добавленный
    /// слой OCR. Порога нет намеренно: даже короткий настоящий текст получил бы
    /// невидимый дубль с двойным копированием/поиском.
    /// </summary>
    public async Task<bool> PageAlreadyHasTextAsync(OpenedDocument document, int logicalIndex, CancellationToken ct)
    {
        var page = document.Session.Model.Pages[logicalIndex];
        if (page.OverlayList is { } overlays &&
            overlays.Any(o => o is OcrTextLayerOverlay or TextOverlay))
            return true;
        var text = await document.Handles[page.SourceId]
            .GetPageTextAsync(page.SourcePageIndex, ct);
        return text.Any(c => !char.IsWhiteSpace(c));
    }
}
