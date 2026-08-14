using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;

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

    /// <summary>Структурная оптимизация без потери качества. Возвращает размеры до/после.</summary>
    public async Task<OptimizeResult> OptimizeCopyAsync(OpenedDocument document, string targetPath, CancellationToken ct)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
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
