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

/// <summary>Структурные операции над файлами PDF (линеаризация, проверка, восстановление). Планируемая реализация — qpdf.</summary>
public interface IPdfStructureEngine : IEngineFeature
{
    Task<PdfValidationResult> CheckAsync(string filePath, CancellationToken ct);
    Task LinearizeAsync(string sourcePath, string targetPath, CancellationToken ct);
}

/// <summary>Шифрование/снятие защиты. Планируемая реализация — qpdf.</summary>
public interface IPdfSecurityEngine : IEngineFeature
{
    Task EncryptAsync(string sourcePath, string targetPath, string userPassword, string? ownerPassword, CancellationToken ct);
    Task DecryptAsync(string sourcePath, string targetPath, string password, CancellationToken ct);
}

/// <summary>Проверка результата сохранения независимым инструментом.</summary>
public interface IPdfValidationEngine : IEngineFeature
{
    Task<PdfValidationResult> ValidateAsync(string filePath, CancellationToken ct);
}

/// <summary>Редактирование существующего содержимого (текстовые объекты, изображения).</summary>
public interface IPdfContentEditEngine : IEngineFeature { }

/// <summary>Аннотации и комментарии.</summary>
public interface IPdfAnnotationEngine : IEngineFeature { }

/// <summary>Интерактивные формы (AcroForm).</summary>
public interface IPdfFormEngine : IEngineFeature { }

/// <summary>Криптографические цифровые подписи.</summary>
public interface IPdfSignatureEngine : IEngineFeature { }

public sealed record OcrPageResult(string Text, double MeanConfidence);

/// <summary>Локальное распознавание текста. Планируемая реализация — Tesseract.</summary>
public interface IOcrEngine : IEngineFeature
{
    Task<OcrPageResult> RecognizeAsync(RenderedPageImage page, IReadOnlyList<string> languages, CancellationToken ct);
}

/// <summary>Импорт/экспорт других форматов.</summary>
public interface IDocumentConversionEngine : IEngineFeature { }
