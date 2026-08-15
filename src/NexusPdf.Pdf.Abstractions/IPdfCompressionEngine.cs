namespace NexusPdf.Pdf.Abstractions;

/// <summary>Что и как жать.</summary>
/// <param name="TargetDpi">Целевое разрешение изображений.</param>
/// <param name="Quality">Качество кодирования с потерями (10–95).</param>
/// <param name="StructureOnly">Изображения не трогать вовсе.</param>
/// <param name="SubsetFonts">Урезать встроенные шрифты до используемых глифов.</param>
public readonly record struct PdfCompressionRequest(
    double TargetDpi, int Quality, bool StructureOnly, bool SubsetFonts);

/// <param name="BytesBefore">Размер исходника.</param>
/// <param name="BytesAfter">Размер результата.</param>
/// <param name="Recompressed">Сколько изображений пересжато (0 — если движок не считает).</param>
/// <param name="Skipped">Сколько пропущено.</param>
/// <param name="KeptOriginal">Результат оказался не меньше исходника, поэтому оставлен исходник.</param>
public readonly record struct PdfCompressionResult(
    long BytesBefore, long BytesAfter, int Recompressed, int Skipped, bool KeptOriginal);

/// <summary>
/// Сжатие готового файла целиком: изображения, шрифты и структура за один
/// проход. Отдельный интерфейс от <see cref="IPdfRenderEngine"/> потому, что
/// движок сжатия и движок отрисовки — разные библиотеки, и подменять их нужно
/// независимо.
/// </summary>
public interface IPdfCompressionEngine
{
    /// <summary>Доступен ли движок (нативные библиотеки на месте).</summary>
    bool IsAvailable { get; }

    /// <summary>Почему недоступен — для честного сообщения пользователю.</summary>
    string UnavailableReason { get; }

    /// <summary>Сжать <paramref name="sourcePath"/> в <paramref name="targetPath"/>.</summary>
    Task<PdfCompressionResult> CompressAsync(
        string sourcePath, string targetPath, PdfCompressionRequest request, CancellationToken ct);
}
