using System.Security.Cryptography.X509Certificates;
using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Signing;

namespace NexusPdf.Application;

public sealed record OptimizeResult(long BytesBefore, long BytesAfter);

/// <summary>
/// Операции над документом, требующие структурного движка (qpdf):
/// защита паролем и оптимизация без потерь. Всегда создают новую копию,
/// исходный файл и текущая сессия не изменяются.
/// </summary>
public sealed class DocumentToolsService
{
    private readonly IPdfRenderEngine _renderEngine;
    private readonly IPdfStructureEngine _structure;
    private readonly IPdfSecurityEngine _security;

    public DocumentToolsService(IPdfRenderEngine renderEngine, IPdfStructureEngine structure, IPdfSecurityEngine security)
    {
        _renderEngine = renderEngine;
        _structure = structure;
        _security = security;
    }

    public bool IsAvailable => _structure.IsAvailable && _security.IsAvailable;

    /// <summary>Копия текущего состояния документа, зашифрованная AES-256.</summary>
    public async Task ProtectCopyAsync(
        OpenedDocument document, string targetPath, string userPassword, string? ownerPassword, CancellationToken ct)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        var composition = document.BuildComposition();
        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            async tempPath =>
            {
                var plainPath = tempPath + ".plain";
                try
                {
                    await _renderEngine.ComposeAsync(composition, plainPath, ct).ConfigureAwait(false);
                    await _security.EncryptAsync(plainPath, tempPath, userPassword, ownerPassword, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Незашифрованный промежуточный файл не должен переживать операцию.
                    try { File.Delete(plainPath); } catch { /* лучшая попытка */ }
                }
            },
            async tempPath =>
            {
                var handle = await _renderEngine.OpenAsync(tempPath, userPassword, ct).ConfigureAwait(false);
                await using (handle.ConfigureAwait(false))
                {
                    if (handle.Info.PageCount != composition.Count)
                        throw new PdfEngineException("Проверка защищённой копии не пройдена: число страниц не совпало.");
                }
            },
            keepBackup: false,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Криптографически подписанная копия текущего состояния документа
    /// (невидимая подпись adbe.pkcs7.detached, SHA-256). Конвейер:
    /// компоновка → нормализация qpdf (QDF) → инкрементальная подпись →
    /// проверка собственным инспектором и повторным открытием.
    /// </summary>
    public async Task SignCopyAsync(
        OpenedDocument document, string targetPath, X509Certificate2 certificate,
        string reason, string location, CancellationToken ct)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
        if (document.Password != null)
            throw new PdfEngineException(
                "Подписание защищённых паролем документов пока не поддерживается: сначала сохраните копию без пароля.");
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);

        var composition = document.BuildComposition();
        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            async tempPath =>
            {
                var plain = tempPath + ".plain";
                var normalized = tempPath + ".qdf";
                try
                {
                    if (SaveService.CanSaveDirect(document))
                        await document.PrimaryHandle.SaveCurrentAsync(plain, ct).ConfigureAwait(false);
                    else
                        await _renderEngine.ComposeAsync(composition, plain, ct).ConfigureAwait(false);
                    await _structure.NormalizeAsync(plain, normalized, ct).ConfigureAwait(false);
                    PdfIncrementalSigner.Sign(normalized, tempPath, certificate, reason, location);
                }
                finally
                {
                    try { File.Delete(plain); } catch { /* лучшая попытка */ }
                    try { File.Delete(normalized); } catch { /* лучшая попытка */ }
                }
            },
            async tempPath =>
            {
                var handle = await _renderEngine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);
                await using (handle.ConfigureAwait(false))
                {
                    if (handle.Info.PageCount != composition.Count)
                        throw new PdfEngineException("Проверка подписанной копии: число страниц не совпало.");
                }
                var signatures = await PdfSignatureInspector.InspectAsync(tempPath, ct).ConfigureAwait(false);
                var own = signatures.LastOrDefault();
                if (own == null || !own.IsCryptoValid || !own.CoversWholeDocument)
                    throw new PdfEngineException("Созданная подпись не прошла собственную проверку.");
            },
            keepBackup: false,
            ct).ConfigureAwait(false);
    }

    /// <summary>Структурная оптимизация без потери качества. Возвращает размеры до/после.</summary>
    public async Task<OptimizeResult> OptimizeCopyAsync(OpenedDocument document, string targetPath, CancellationToken ct)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        var composition = document.BuildComposition();
        long bytesBefore = 0;

        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            async tempPath =>
            {
                var plainPath = tempPath + ".plain";
                try
                {
                    await _renderEngine.ComposeAsync(composition, plainPath, ct).ConfigureAwait(false);
                    bytesBefore = new FileInfo(plainPath).Length;
                    await _structure.OptimizeAsync(plainPath, tempPath, linearize: true, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(plainPath); } catch { /* лучшая попытка */ }
                }
            },
            async tempPath =>
            {
                var handle = await _renderEngine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);
                await using (handle.ConfigureAwait(false))
                {
                    if (handle.Info.PageCount != composition.Count)
                        throw new PdfEngineException("Проверка оптимизированной копии не пройдена: число страниц не совпало.");
                }
                var check = await _structure.CheckAsync(tempPath, null, ct).ConfigureAwait(false);
                if (!check.IsValid)
                    throw new PdfEngineException("qpdf сообщил о проблемах в оптимизированной копии: " +
                                                 string.Join("; ", check.Problems.Take(3)));
            },
            keepBackup: false,
            ct).ConfigureAwait(false);

        return new OptimizeResult(bytesBefore, new FileInfo(targetPath).Length);
    }
}
