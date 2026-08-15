using System.Security.Cryptography.X509Certificates;
using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Signing;

namespace NexusPdf.Application;

public sealed record OptimizeResult(long BytesBefore, long BytesAfter);

/// <param name="Recompressed">Сколько изображений пересжато (0 — движок не считает поштучно).</param>
/// <param name="KeptOriginal">Выигрыша не вышло, поэтому копия равна исходнику.</param>
public sealed record CompressImagesResult(
    long BytesBefore, long BytesAfter, int Recompressed, int Skipped, bool KeptOriginal = false);

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
    private readonly IPdfCompressionEngine? _compression;

    public DocumentToolsService(
        IPdfRenderEngine renderEngine, IPdfStructureEngine structure, IPdfSecurityEngine security,
        IPdfCompressionEngine? compression = null)
    {
        _renderEngine = renderEngine;
        _structure = structure;
        _security = security;
        _compression = compression;
    }

    /// <summary>Доступно ли сжатие через отдельный движок (MuPDF).</summary>
    public bool HasCompressionEngine => _compression?.IsAvailable == true;

    public bool IsAvailable => _structure.IsAvailable && _security.IsAvailable;

    /// <summary>Копия текущего состояния документа, зашифрованная AES-256.</summary>
    public async Task ProtectCopyAsync(
        OpenedDocument document, string targetPath, string userPassword, string? ownerPassword, CancellationToken ct)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        await using var baked = await document.BuildCompositionBakedAsync(_renderEngine, ct).ConfigureAwait(false);
        var composition = baked.Composition;
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
    public Task SignCopyAsync(
        OpenedDocument document, string targetPath, X509Certificate2 certificate,
        string reason, string location, CancellationToken ct) =>
        SignCopyAsync(document, targetPath, certificate, reason, location, visibleStamp: false, ct);

    public async Task SignCopyAsync(
        OpenedDocument document, string targetPath, X509Certificate2 certificate,
        string reason, string location, bool visibleStamp, CancellationToken ct)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
        if (document.Password != null)
            throw new PdfEngineException(
                "Подписание защищённых паролем документов пока не поддерживается: сначала сохраните копию без пароля.");

        // Конвейер пересобирает файл и разрушил бы существующие подписи.
        // UI тоже отказывает, но его сведения асинхронные — здесь последний
        // рубеж с проверкой самого исходного файла.
        var existing = await PdfSignatureInspector.InspectAsync(document.PrimaryHandle.FilePath, ct)
            .ConfigureAwait(false);
        if (existing.Count > 0)
            throw new PdfEngineException(
                "Документ уже содержит цифровые подписи. Повторное подписание пересобрало бы файл и разрушило их — " +
                "сохраните изменённую копию под другим именем и подпишите её.");

        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);

        await using var baked = await document.BuildCompositionBakedAsync(_renderEngine, ct).ConfigureAwait(false);
        var composition = baked.Composition;
        if (visibleStamp && composition.Count > 0)
        {
            // Видимая отметка запекается В СТРАНИЦУ до подписания (кириллица
            // через CID-шрифт компоновки) и потому криптографически покрыта
            // подписью. Позиция — левый низ первой страницы.
            var pageSize = document.GetLogicalPageSize(0);
            var signerName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            var stampTitle = new TextOverlay(
                $"Подписано: {signerName}", 20, pageSize.HeightPoints - 44, 11, 0xFF1D4ED8, 0);
            var stampDate = new TextOverlay(
                $"{DateTimeOffset.Now:dd.MM.yyyy HH:mm} · NexusPDF" +
                (reason.Length > 0 ? $" · {reason}" : ""),
                20, pageSize.HeightPoints - 30, 8.5, 0xFF64748B, 0);
            var first = composition[0];
            var overlays = new List<PageOverlay>(first.Overlays ?? Array.Empty<PageOverlay>())
                { stampTitle, stampDate };
            var patched = new List<ComposedPage>(composition);
            patched[0] = first with { Overlays = overlays };
            composition = patched;
        }

        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            async tempPath =>
            {
                var plain = tempPath + ".plain";
                var normalized = tempPath + ".qdf";
                try
                {
                    // С видимой отметкой прямой путь невозможен: оверлеи
                    // запекаются только компоновкой.
                    if (!visibleStamp && SaveService.CanSaveDirect(document))
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
                if (signatures.Count != 1)
                    throw new PdfEngineException(
                        $"Проверка подписанной копии: ожидалась ровно одна подпись, найдено {signatures.Count}.");
                var own = signatures[0];
                if (!own.IsCryptoValid || !own.CoversWholeDocument)
                    throw new PdfEngineException("Созданная подпись не прошла собственную проверку.");
            },
            keepBackup: false,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Копия текущего состояния документа со сжатыми изображениями, урезанными
    /// шрифтами и пересобранной структурой. Основной движок — MuPDF; без него
    /// работает запасной путь на PDFium, где кодек JPEG даёт вызывающий слой.
    /// </summary>
    public async Task<CompressImagesResult> CompressImagesCopyAsync(
        OpenedDocument document, string targetPath, double targetDpi,
        int quality, EncodeImage encode, CancellationToken ct,
        bool structureOnly = false, bool subsetFonts = false)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
        // Не только user-пароль: файл с одним owner-паролем (запрет изменений)
        // открывается без пароля, но пересохранение молча сняло бы защиту.
        var metadata = await document.PrimaryHandle.GetMetadataAsync(ct).ConfigureAwait(false);
        if (document.Password != null || metadata.IsEncrypted)
            throw new PdfEngineException(
                "Пересжатие защищённых документов не поддерживается: сохранение сняло бы шифрование молча. Сначала сохраните копию без защиты.");
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);

        await using var baked = await document.BuildCompositionBakedAsync(_renderEngine, ct).ConfigureAwait(false);
        var composition = baked.Composition;
        long bytesBefore = 0;
        var keptOriginal = false;
        ImageRecompressStats stats = new(0, 0);

        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            async tempPath =>
            {
                var plain = tempPath + ".plain";
                try
                {
                    if (SaveService.CanSaveDirect(document))
                        await document.PrimaryHandle.SaveCurrentAsync(plain, ct).ConfigureAwait(false);
                    else
                        await _renderEngine.ComposeAsync(composition, plain, ct).ConfigureAwait(false);
                    bytesBefore = new FileInfo(plain).Length;

                    if (_compression?.IsAvailable == true)
                    {
                        // Основной путь: MuPDF делает изображения, шрифты и
                        // структуру за один проход и сам не отдаёт результат,
                        // который больше исходника.
                        var result = await _compression.CompressAsync(
                            plain, tempPath,
                            new PdfCompressionRequest(targetDpi, quality, structureOnly, subsetFonts),
                            ct).ConfigureAwait(false);
                        stats = new ImageRecompressStats(result.Recompressed, result.Skipped);
                        keptOriginal = result.KeptOriginal;
                    }
                    else
                    {
                        // Запасной путь на своём движке: работает всегда, но
                        // не умеет 1-битные сканы и шрифты.
                        stats = await _renderEngine.RecompressImagesAsync(
                            plain, null, tempPath, targetDpi, quality, encode, ct).ConfigureAwait(false);

                        // Структурная оптимизация тем же заходом: потоки объектов
                        // и сжатая таблица ссылок дают ещё несколько процентов и
                        // не трогают качество.
                        await TryOptimizeInPlaceAsync(tempPath, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    try { File.Delete(plain); } catch { /* лучшая попытка */ }
                }
            },
            async tempPath =>
            {
                var handle = await _renderEngine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);
                await using (handle.ConfigureAwait(false))
                {
                    if (handle.Info.PageCount != composition.Count)
                        throw new PdfEngineException("Проверка пересжатой копии не пройдена: число страниц не совпало.");
                }
            },
            keepBackup: false,
            ct).ConfigureAwait(false);

        return new CompressImagesResult(
            bytesBefore, new FileInfo(targetPath).Length, stats.Recompressed, stats.Skipped, keptOriginal);
    }

    /// <summary>
    /// Структурная оптимизация уже готового файла на месте. Ошибка здесь не
    /// повод терять пересжатие: без qpdf копия просто останется без этой
    /// последней пары процентов.
    /// </summary>
    private async Task TryOptimizeInPlaceAsync(string path, CancellationToken ct)
    {
        if (!_structure.IsAvailable) return;
        var optimized = path + ".opt";
        try
        {
            await _structure.OptimizeAsync(path, optimized, linearize: true, ct).ConfigureAwait(false);
            if (new FileInfo(optimized).Length < new FileInfo(path).Length)
            {
                File.Copy(optimized, path, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Оптимизация не обязана удаваться на любом файле.
        }
        finally
        {
            try { File.Delete(optimized); } catch { /* лучшая попытка */ }
        }
    }

    /// <summary>Структурная оптимизация без потери качества. Возвращает размеры до/после.</summary>
    public async Task<OptimizeResult> OptimizeCopyAsync(OpenedDocument document, string targetPath, CancellationToken ct)
    {
        SaveService.ThrowIfTargetIsOpenSource(document, targetPath);
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        await using var baked = await document.BuildCompositionBakedAsync(_renderEngine, ct).ConfigureAwait(false);
        var composition = baked.Composition;
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
