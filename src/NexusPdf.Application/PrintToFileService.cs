using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Printing;

namespace NexusPdf.Application;

/// <summary>Итог вывода печатной раскладки в файл.</summary>
public sealed record PrintToFileResult(int SheetsWritten, double EffectiveDpi, string Path);

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
    private readonly IPdfRenderEngine _engine;

    public PrintToFileService(IPdfRenderEngine engine) => _engine = engine;

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
            throw new InvalidOperationException("В плане печати нет ни одного листа.");

        var renderer = new PrintPlanRenderer(document);
        var specs = new List<ImagePageSpec>(plan.Sheets.Count);
        var effectiveDpi = dpi;

        for (var i = 0; i < plan.Sheets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var composed = SheetComposer.Compose(plan.Sheets[i], dpi);
            effectiveDpi = Math.Min(effectiveDpi, composed.Dpi);

            var image = await renderer.RenderSheetAsync(composed, ct).ConfigureAwait(false);
            specs.Add(new ImagePageSpec(
                image.Bgra, image.PixelWidth, image.PixelHeight,
                // Размер страницы будущего файла — физический размер листа, а не
                // размер растра: иначе A4 при 600 dpi стал бы страницей в метры.
                composed.Sheet.PaperSizePt.WidthPt,
                composed.Sheet.PaperSizePt.HeightPt));

            progress?.Report((i + 1, plan.Sheets.Count));
        }

        // Запись через временный файл с проверкой: обрубок вместо результата
        // недопустим ровно так же, как при обычном сохранении документа.
        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            tempPath => _engine.CreateImageDocumentAsync(specs, tempPath, ct),
            async tempPath =>
            {
                await using var check = await _engine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);
                if (check.Info.PageCount != specs.Count)
                    throw new PdfEngineException(
                        $"В файле оказалось листов {check.Info.PageCount} вместо {specs.Count}.");
            },
            keepBackup: false,
            ct).ConfigureAwait(false);

        return new PrintToFileResult(specs.Count, effectiveDpi, targetPath);
    }
}
