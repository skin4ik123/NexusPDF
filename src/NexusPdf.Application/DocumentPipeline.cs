using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>Этап обработки — для строки состояния и полосы выполнения.</summary>
public enum ProcessingStage
{
    Preparing,
    Enhancing,
    Compressing,
    Optimizing,
    Applying,
}

/// <param name="Done">Сколько сделано внутри этапа; 0, если этап не считает поштучно.</param>
public readonly record struct ProcessingProgress(ProcessingStage Stage, int Done, int Total);

/// <summary>
/// Что сделать с документом за один заход.
///
/// Порядок шагов задан не полями записи, а конвейером, и он не случаен:
/// сначала чистка растров, потом пересжатие, потом структура. Наоборот
/// нельзя — чистка кладёт растры несжатыми, и пересжатие обязано идти ПОСЛЕ
/// неё, иначе весь выигрыш тут же теряется. Структурная оптимизация идёт
/// последней: она работает с уже готовым файлом.
/// </summary>
/// <param name="Enhance">Чистка сканов; null — не трогать.</param>
/// <param name="Compress">Пересжатие изображений; null — не трогать.</param>
/// <param name="OptimizeStructure">Потоки объектов, сжатая таблица ссылок, линеаризация.</param>
public sealed record ProcessingPlan(
    ScanEnhanceOptions? Enhance = null,
    PdfCompressionRequest? Compress = null,
    bool OptimizeStructure = false)
{
    /// <summary>Делать нечего — окно не должно запускать пустую обработку.</summary>
    public bool IsEmpty => Enhance == null && Compress == null && !OptimizeStructure;
}

/// <param name="BytesBefore">Размер документа в его нынешнем виде, до обработки.</param>
/// <param name="BytesAfter">Размер обработанного файла.</param>
/// <param name="StructureOptimized">Структурная оптимизация действительно отработала.</param>
public sealed record ProcessingResult(
    long BytesBefore, long BytesAfter,
    ScanEnhanceStats Enhance, int Recompressed, int Skipped,
    bool StructureOptimized, int PageCount);

public sealed partial class DocumentToolsService
{
    /// <summary>
    /// Обработка ОТКРЫТОГО документа: чистка, пересжатие и оптимизация одним
    /// конвейером, результат которого становится содержимым вкладки.
    ///
    /// Файл на диске не трогается вообще. Обработанный документ живёт во
    /// временном файле и подставляется источником страниц, документ помечается
    /// изменённым — сохранить его пользователь может куда угодно и когда угодно,
    /// а «Отменить» возвращает документ к тому, что было.
    /// </summary>
    /// <param name="tempFolder">Куда класть промежуточные и итоговый файлы.</param>
    /// <param name="encode">Кодек JPEG для запасного пути пересжатия.</param>
    public async Task<ProcessingResult> ProcessInPlaceAsync(
        OpenedDocument document, ProcessingPlan plan, string tempFolder,
        EncodeImage encode, IProgress<ProcessingProgress>? progress, CancellationToken ct)
    {
        if (plan.IsEmpty)
            throw new InvalidOperationException("В плане обработки не выбрано ни одного действия.");

        // Один общий отказ на все три операции: раньше сжатие и чистка
        // защищённые документы отклоняли, а оптимизация молча снимала с них
        // шифрование.
        var metadata = await document.PrimaryHandle.GetMetadataAsync(ct).ConfigureAwait(false);
        if (document.Password != null || metadata.IsEncrypted)
            throw new PdfEngineException(
                "Обработка защищённых документов не поддерживается: сохранение сняло бы шифрование молча. " +
                "Сначала сохраните копию без защиты.");

        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(tempFolder);

        var stem = Path.Combine(tempFolder, $"processed-{Guid.NewGuid():N}");
        var intermediates = new List<string>();
        var enhanceStats = new ScanEnhanceStats();
        var recompressed = 0;
        var skipped = 0;
        var structureDone = false;

        try
        {
            // ---- Исходное состояние документа со всеми несохранёнными правками ----
            progress?.Report(new ProcessingProgress(ProcessingStage.Preparing, 0, 0));
            await using var baked = await document.BuildCompositionBakedAsync(_renderEngine, ct).ConfigureAwait(false);
            var composition = baked.Composition;

            var current = stem + "-0.pdf";
            intermediates.Add(current);
            if (SaveService.CanSaveDirect(document))
                await document.PrimaryHandle.SaveCurrentAsync(current, ct).ConfigureAwait(false);
            else
                await _renderEngine.ComposeAsync(composition, current, ct).ConfigureAwait(false);
            var bytesBefore = new FileInfo(current).Length;

            // ---- 1. Чистка сканов ----
            if (plan.Enhance is { } enhance)
            {
                ct.ThrowIfCancellationRequested();
                var next = stem + "-1.pdf";
                intermediates.Add(next);
                var pages = new Progress<int>(done =>
                    progress?.Report(new ProcessingProgress(ProcessingStage.Enhancing, done, composition.Count)));
                enhanceStats = await _renderEngine
                    .EnhanceScansAsync(current, null, next, enhance, pages, ct).ConfigureAwait(false);
                current = next;
            }

            // ---- 2. Пересжатие изображений ----
            // Строго после чистки: вычищенные растры кладутся в PDF без сжатия,
            // и без этого шага «улучшенный» документ оказывается в разы тяжелее
            // исходного.
            if (plan.Compress is { } compress)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProcessingProgress(ProcessingStage.Compressing, 0, composition.Count));
                var next = stem + "-2.pdf";
                intermediates.Add(next);

                if (_compression?.IsAvailable == true)
                {
                    var result = await _compression.CompressAsync(current, next, compress, ct).ConfigureAwait(false);
                    recompressed = result.Recompressed;
                    skipped = result.Skipped;
                    // Движок отдал исходник: сжимать было нечего или стало хуже.
                    if (result.KeptOriginal) File.Copy(current, next, overwrite: true);
                }
                else
                {
                    var stats = await _renderEngine.RecompressImagesAsync(
                        current, null, next, compress.TargetDpi, compress.Quality, encode, ct).ConfigureAwait(false);
                    recompressed = stats.Recompressed;
                    skipped = stats.Skipped;
                }
                current = next;
            }

            // ---- 3. Структура ----
            if (plan.OptimizeStructure && _structure.IsAvailable)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProcessingProgress(ProcessingStage.Optimizing, 0, 0));
                var next = stem + "-3.pdf";
                try
                {
                    await _structure.OptimizeAsync(current, next, linearize: true, ct).ConfigureAwait(false);
                    // Оптимизация, которая сделала файл больше, не оптимизация.
                    if (new FileInfo(next).Length <= new FileInfo(current).Length)
                    {
                        intermediates.Add(current);
                        current = next;
                        structureDone = true;
                    }
                    else
                    {
                        intermediates.Add(next);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // qpdf не обязан справляться с любым файлом; терять из-за
                    // этого чистку и пересжатие нельзя.
                    intermediates.Add(next);
                }
            }

            // ---- Проверка и подстановка ----
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProcessingProgress(ProcessingStage.Applying, 0, 0));
            var check = await _renderEngine.OpenAsync(current, null, ct).ConfigureAwait(false);
            await using (check.ConfigureAwait(false))
            {
                if (check.Info.PageCount != composition.Count)
                    throw new PdfEngineException(
                        $"Проверка обработанного документа не пройдена: было страниц {composition.Count}, стало {check.Info.PageCount}.");
            }

            var bytesAfter = new FileInfo(current).Length;
            var pageCount = await document.SwitchToProcessedFileAsync(_renderEngine, current, ct).ConfigureAwait(false);
            // Итоговый файл теперь источник страниц вкладки — удалять его нельзя.
            intermediates.Remove(current);

            return new ProcessingResult(
                bytesBefore, bytesAfter, enhanceStats, recompressed, skipped, structureDone, pageCount);
        }
        finally
        {
            foreach (var path in intermediates)
            {
                try { File.Delete(path); } catch { /* лучшая попытка */ }
            }
        }
    }
}
