using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Ocr;

/// <summary>
/// Движок распознавания текста. Реализации взаимозаменяемы: PaddleOCR точнее,
/// Tesseract быстрее, и выбор остаётся за пользователем.
/// </summary>
public interface ITextRecognizer : IDisposable
{
    /// <summary>Короткое имя для настроек и журнала.</summary>
    string Id { get; }

    /// <summary>Название для интерфейса.</summary>
    string DisplayName { get; }

    bool IsAvailable { get; }

    /// <summary>Почему движок недоступен. null, когда всё в порядке.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Возвращает ли движок сразу ЦЕЛЫЕ строки, а не отдельные слова.
    ///
    /// Разница принципиальна для режима редактируемого текста: рамки слов
    /// нужно собирать в строки, а готовые строки собирать НЕЛЬЗЯ — соседние
    /// колонки документа склеятся в одну строку через всю страницу, и под неё
    /// ляжет заплатка соответствующего размера. PaddleOCR отдаёт строки,
    /// Tesseract — слова.
    /// </summary>
    bool ReturnsWholeLines { get; }

    /// <summary>
    /// Распознаёт растр страницы. Рамки — в пикселях ИСХОДНОГО растра от
    /// левого верхнего угла; пустые и «мусорные» слова уже отфильтрованы.
    /// </summary>
    Task<OcrPageResult> RecognizeAsync(RenderedPageImage image, int renderDpi, CancellationToken ct);
}
