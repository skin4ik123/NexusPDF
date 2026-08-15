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

    private readonly TesseractOcrEngine _ocr;

    public OcrService(TesseractOcrEngine ocr) => _ocr = ocr;

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
                var kept = result.Words.Where(w => w.Confidence >= MinWordConfidence).ToList();
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
                        // Слова собираются в строки, а цвета берутся с самого
                        // скана: заплатка должна совпасть с бумагой, а буквы —
                        // с чернилами оригинала.
                        var lines = OcrLineBuilder.BuildLines(words)
                            .Select(line =>
                            {
                                var background = OcrLineBuilder.SampleBackground(
                                    image, pixelWidth / size.WidthPoints, pixelHeight / size.HeightPoints, line);
                                var ink = OcrLineBuilder.SampleInk(
                                    image, pixelWidth / size.WidthPoints, pixelHeight / size.HeightPoints,
                                    line, background);
                                return line with { BackgroundArgb = background, InkArgb = ink };
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
