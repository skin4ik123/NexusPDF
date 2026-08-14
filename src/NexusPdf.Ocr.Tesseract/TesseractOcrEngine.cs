using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Ocr.Tesseract;

/// <summary>
/// Адаптер локального OCR на Tesseract (этап 7 плана). Пока движок и языковые
/// пакеты не поставляются — честно недоступен, функции OCR в UI не показываются.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;

    public string? UnavailableReason =>
        "OCR появится на этапе 7: Tesseract и языковые пакеты (rus, eng, ukr, kat) ещё не поставляются.";

    public Task<OcrPageResult> RecognizeAsync(RenderedPageImage page, IReadOnlyList<string> languages, CancellationToken ct) =>
        throw new PdfFeatureUnavailableException("OCR", UnavailableReason!);
}
