using NexusPdf.Domain;
using NexusPdf.Ocr;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

public sealed record OcrProgress(int PagesDone, int TotalPages, int WordsSoFar);

public sealed record OcrRunResult(
    int PagesRecognized,
    int PagesSkippedWithText,
    int WordCount,
    double MeanConfidence,
    bool Cancelled);

/// <summary>
/// Распознавание текста сканов: страница рендерится в высоком разрешении,
/// распознаётся Tesseract (rus+eng), и на неё накладывается невидимый
/// текстовый слой (<see cref="OcrTextLayerOverlay"/>) — поиск и копирование
/// начинают работать после сохранения. Страницы, где текст уже есть
/// (настоящий или ранее распознанный), честно пропускаются.
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

    // Столько непробельных символов уже считается «текст на странице есть».
    private const int ExistingTextThreshold = 8;

    private readonly TesseractOcrEngine _ocr;

    public OcrService(TesseractOcrEngine ocr) => _ocr = ocr;

    public bool IsAvailable => _ocr.IsAvailable;
    public string? UnavailableReason => _ocr.UnavailableReason;

    /// <summary>
    /// Распознаёт перечисленные логические страницы документа.
    /// null — все страницы. Вызывать с UI-потока: операции сессии
    /// применяются в контексте вызывающего.
    /// </summary>
    public async Task<OcrRunResult> RecognizeAsync(
        OpenedDocument document,
        IReadOnlyList<int>? logicalIndices,
        IProgress<OcrProgress>? progress,
        CancellationToken ct)
    {
        var targets = logicalIndices ?? Enumerable.Range(0, document.Session.Model.Pages.Count).ToList();
        var recognized = 0;
        var skipped = 0;
        var totalWords = 0;
        double confidenceSum = 0;
        var cancelled = false;

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

                var image = await document.RenderLogicalPageAsync(logicalIndex, pixelWidth, pixelHeight, ct);
                var result = await _ocr.RecognizeAsync(image, dpi, ct);

                // Пиксели растра → отображаемые пункты текущей рамки страницы.
                var ptPerPxX = size.WidthPoints / pixelWidth;
                var ptPerPxY = size.HeightPoints / pixelHeight;
                var words = result.Words
                    .Where(w => w.Confidence >= MinWordConfidence)
                    .Select(w => new OcrWordBox(
                        w.Text,
                        w.X * ptPerPxX,
                        w.Y * ptPerPxY,
                        w.Width * ptPerPxX,
                        w.Height * ptPerPxY))
                    .ToList();

                if (words.Count > 0)
                {
                    document.Session.Apply(new AddOverlayOperation(
                        logicalIndex, new OcrTextLayerOverlay(words)));
                    recognized++;
                    totalWords += words.Count;
                    confidenceSum += result.MeanConfidence;
                }

                progress?.Report(new OcrProgress(i + 1, targets.Count, totalWords));
            }
        }
        catch (OperationCanceledException)
        {
            // Уже распознанные страницы остаются (каждая — отдельный Undo).
            cancelled = true;
        }

        return new OcrRunResult(
            recognized, skipped, totalWords,
            recognized > 0 ? confidenceSum / recognized : 0,
            cancelled);
    }

    /// <summary>Есть ли на странице текст — настоящий или уже добавленный слой OCR.</summary>
    public async Task<bool> PageAlreadyHasTextAsync(OpenedDocument document, int logicalIndex, CancellationToken ct)
    {
        var page = document.Session.Model.Pages[logicalIndex];
        if (page.OverlayList is { } overlays && overlays.Any(o => o is OcrTextLayerOverlay))
            return true;
        var text = await document.Handles[page.SourceId]
            .GetPageTextAsync(page.SourcePageIndex, ct);
        return text.Count(c => !char.IsWhiteSpace(c)) >= ExistingTextThreshold;
    }
}
