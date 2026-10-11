using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Printing;

namespace NexusPdf.Application;

/// <summary>Итог вывода печатной раскладки в файл.</summary>
public sealed record PrintToFileResult(int SheetsWritten, double EffectiveDpi, string Path);

/// <summary>Почему раскладку нельзя сохранить: код для перевода в интерфейсе.</summary>
public sealed class PrintToFileException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// «Сохранить печатную раскладку в PDF». Это не запасной вариант, а полноценный
/// выход задания: тот же PrintJobPlan, тот же рендер листов, только вместо
/// очереди принтера — файл. Благодаря этому раскладку можно проверить глазами
/// и без принтера, а результат сравнить с предпросмотром.
///
/// Исходный документ не меняется: создаётся отдельный новый файл.
/// </summary>
public sealed class PrintToFileService
{
    /// <summary>
    /// Сколько сырых растров листов держать в памяти до записи на диск. Раньше
    /// в памяти копились ВСЕ листы задания (около 35 МБ на лист A4 при 300 dpi)
    /// и ещё раз копировались в новый документ — длинный документ кончался
    /// нехваткой памяти. Теперь листы пишутся пачками во временные части, а
    /// итоговый файл собирается из уже сжатых частей.
    /// </summary>
    public const long DefaultBatchBudgetBytes = 384L * 1024 * 1024;

    private readonly IPdfRenderEngine _engine;
    private readonly long _batchBudgetBytes;

    public PrintToFileService(IPdfRenderEngine engine, long batchBudgetBytes = DefaultBatchBudgetBytes)
    {
        _engine = engine;
        _batchBudgetBytes = Math.Max(1, batchBudgetBytes);
    }

    /// <param name="dpi">Разрешение растра листов. 150 — экран, 300 — печать, 600 — качество.</param>
    public async Task<PrintToFileResult> SaveAsync(
        OpenedDocument document,
        PrintJobPlan plan,
        string targetPath,
        double dpi,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        if (plan.Sheets.Count == 0)
            throw new PrintToFileException("PrintNoSheets");

        // Запрет печати нельзя обойти экспортом раскладки: это тот же вывод
        // содержимого, просто в файл, и разрешения документа тут те же.
        var permissions = PrintPermissions.FromFlags(
            await document.PrimaryHandle.GetPermissionsAsync(ct).ConfigureAwait(false));
        if (!permissions.AllowPrint)
            throw new PrintToFileException("PrintToFileForbidden");
        dpi = permissions.LimitDpi(dpi);

        var renderer = new PrintPlanRenderer(document);
        var effectiveDpi = dpi;
        var workDir = Path.Combine(Path.GetTempPath(), "NexusPDF-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var parts = new List<string>();
        var handles = new List<IPdfDocumentHandle>();
        try
        {
            var batch = new List<ImagePageSpec>();
            long batchBytes = 0;

            async Task FlushAsync()
            {
                if (batch.Count == 0) return;
                var partPath = Path.Combine(workDir, $"part-{parts.Count:D4}.pdf");
                await _engine.CreateImageDocumentAsync(batch, partPath, ct).ConfigureAwait(false);
                parts.Add(partPath);
                // Новый список, а не Clear: ссылки на растры должны уйти сразу.
                batch = new List<ImagePageSpec>();
                batchBytes = 0;
            }

            for (var i = 0; i < plan.Sheets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var composed = SheetComposer.Compose(plan.Sheets[i], dpi);
                effectiveDpi = Math.Min(effectiveDpi, composed.Dpi);

                var image = await renderer.RenderSheetAsync(composed, ct).ConfigureAwait(false);
                batch.Add(new ImagePageSpec(
                    image.Bgra, image.PixelWidth, image.PixelHeight,
                    // Размер страницы будущего файла — физический размер листа, а не
                    // размер растра: иначе A4 при 600 dpi стал бы страницей в метры.
                    composed.Sheet.PaperSizePt.WidthPt,
                    composed.Sheet.PaperSizePt.HeightPt));
                batchBytes += image.Bgra.LongLength;
                if (batchBytes >= _batchBudgetBytes)
                    await FlushAsync().ConfigureAwait(false);

                progress?.Report((i + 1, plan.Sheets.Count));
            }
            await FlushAsync().ConfigureAwait(false);

            foreach (var part in parts)
                handles.Add(await _engine.OpenAsync(part, null, ct).ConfigureAwait(false));
            var composition = handles
                .SelectMany(h => Enumerable.Range(0, h.Info.PageCount).Select(p => new ComposedPage(h, p, 0)))
                .ToList();

            // Запись через временный файл с проверкой: обрубок вместо результата
            // недопустим ровно так же, как при обычном сохранении документа.
            await SafeFileReplace.WriteAndReplaceAsync(
                targetPath,
                tempPath => _engine.ComposeAsync(composition, tempPath, ct),
                async tempPath =>
                {
                    await using var check = await _engine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);
                    if (check.Info.PageCount != plan.Sheets.Count)
                        throw new PdfEngineException(
                            $"Layout file has {check.Info.PageCount} sheets instead of {plan.Sheets.Count}.");
                },
                keepBackup: false,
                ct).ConfigureAwait(false);

            return new PrintToFileResult(plan.Sheets.Count, effectiveDpi, targetPath);
        }
        finally
        {
            foreach (var handle in handles)
                await handle.DisposeAsync().ConfigureAwait(false);
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Временные части раскладки не удалились: {Dir}", workDir);
            }
        }
    }
}
