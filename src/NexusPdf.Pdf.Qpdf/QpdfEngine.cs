using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Pdf.Qpdf;

/// <summary>
/// Адаптер qpdf (структурная проверка, линеаризация, шифрование).
/// Работает через официальный qpdf.exe, который кладётся в подпапку tools\qpdf
/// рядом с приложением (см. tools/fetch-qpdf.ps1 — версия и SHA-256 пина).
/// Пока бинарный файл не установлен, движок честно объявляет себя недоступным,
/// а UI обязан скрывать зависящие от него функции.
/// </summary>
public sealed class QpdfEngine : IPdfStructureEngine, IPdfSecurityEngine, IPdfValidationEngine
{
    private readonly string? _qpdfPath;

    public QpdfEngine(string appBaseDirectory)
    {
        var candidate = Path.Combine(appBaseDirectory, "tools", "qpdf", "qpdf.exe");
        _qpdfPath = File.Exists(candidate) ? candidate : null;
    }

    public bool IsAvailable => _qpdfPath != null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "qpdf.exe не установлен. Запустите tools/fetch-qpdf.ps1 или переустановите приложение с компонентом qpdf.";

    public Task<PdfValidationResult> CheckAsync(string filePath, CancellationToken ct) => ValidateAsync(filePath, ct);

    public Task<PdfValidationResult> ValidateAsync(string filePath, CancellationToken ct)
    {
        ThrowIfUnavailable();
        // Реализация появится вместе с поставкой qpdf.exe (этап 3):
        // qpdf --check --json — разбор отчёта в PdfValidationResult.
        throw new PdfFeatureUnavailableException("Проверка qpdf", "реализация запланирована на этап 3.");
    }

    public Task LinearizeAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        ThrowIfUnavailable();
        throw new PdfFeatureUnavailableException("Линеаризация", "реализация запланирована на этап 3.");
    }

    public Task EncryptAsync(string sourcePath, string targetPath, string userPassword, string? ownerPassword, CancellationToken ct)
    {
        ThrowIfUnavailable();
        throw new PdfFeatureUnavailableException("Шифрование", "реализация запланирована на этап 6.");
    }

    public Task DecryptAsync(string sourcePath, string targetPath, string password, CancellationToken ct)
    {
        ThrowIfUnavailable();
        throw new PdfFeatureUnavailableException("Снятие защиты", "реализация запланирована на этап 6.");
    }

    private void ThrowIfUnavailable()
    {
        if (!IsAvailable)
            throw new PdfFeatureUnavailableException("qpdf", UnavailableReason!);
    }
}
