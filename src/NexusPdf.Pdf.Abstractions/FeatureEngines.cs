namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Общий контракт «необязательного» движка. Если IsAvailable == false, интерфейс
/// обязан честно сообщать причину, а UI — скрывать/отключать соответствующие функции.
/// Запрещено имитировать работу недоступной функции.
/// </summary>
public interface IEngineFeature
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
}

/// <summary>Структурные операции над файлами PDF (линеаризация, оптимизация, проверка). Реализация — qpdf.</summary>
public interface IPdfStructureEngine : IEngineFeature
{
    Task<PdfValidationResult> CheckAsync(string filePath, string? password, CancellationToken ct);
    Task LinearizeAsync(string sourcePath, string targetPath, CancellationToken ct);

    /// <summary>Структурная оптимизация без потери качества: объектные потоки, пересжатие потоков.</summary>
    Task OptimizeAsync(string sourcePath, string targetPath, bool linearize, CancellationToken ct);

    /// <summary>Нормализация в предсказуемый текстовый вид (QDF): нужна для инкрементального подписания.</summary>
    Task NormalizeAsync(string sourcePath, string targetPath, CancellationToken ct);
}

/// <summary>Шифрование/снятие защиты. Реализация — qpdf (AES-256).</summary>
public interface IPdfSecurityEngine : IEngineFeature
{
    Task EncryptAsync(string sourcePath, string targetPath, string userPassword, string? ownerPassword, CancellationToken ct);
    Task DecryptAsync(string sourcePath, string targetPath, string password, CancellationToken ct);
}

/// <summary>Проверка результата сохранения независимым инструментом.</summary>
public interface IPdfValidationEngine : IEngineFeature
{
    Task<PdfValidationResult> ValidateAsync(string filePath, string? password, CancellationToken ct);
}

// Здесь намеренно нет интерфейсов-маркеров для аннотаций, форм, подписей, OCR
// и конвертации. Эти функции реализованы конкретными типами
// (PdfiumDocumentHandle, NexusPdf.Ocr.TesseractOcrEngine, PdfIncrementalSigner,
// ConvertService), а пустой интерфейс «на будущее» создаёт ложное впечатление
// абстракции и, как показал аудит, расходится с реальностью: единственная
// реализация IOcrEngine сообщала «OCR недоступен», когда OCR уже работал.
