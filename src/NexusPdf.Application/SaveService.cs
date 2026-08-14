using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

public sealed class SaveService
{
    private readonly IPdfRenderEngine _engine;

    public SaveService(IPdfRenderEngine engine) => _engine = engine;

    /// <summary>
    /// Сохранение по алгоритму «записать во временный файл → проверить → атомарно
    /// подменить цель». Оригинал не изменяется до успешной проверки. После записи
    /// документ переоткрывается из сохранённого файла.
    /// </summary>
    public async Task SaveAsAsync(OpenedDocument document, string targetPath, bool keepBackup, CancellationToken ct)
    {
        var composition = document.BuildComposition();
        var expectedCount = composition.Count;
        if (expectedCount == 0)
            throw new InvalidOperationException("Документ не содержит страниц.");

        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            tempPath => _engine.ComposeAsync(composition, tempPath, ct),
            tempPath => ValidateAsync(tempPath, expectedCount, ct),
            keepBackup,
            ct).ConfigureAwait(false);

        await document.RebaseToSavedFileAsync(_engine, targetPath, ct).ConfigureAwait(false);
    }

    /// <summary>Извлечение выбранных логических страниц в отдельный файл (исходный документ не меняется).</summary>
    public async Task ExtractAsync(OpenedDocument document, IReadOnlyList<int> logicalIndices, string targetPath, CancellationToken ct)
    {
        var all = document.BuildComposition();
        var subset = logicalIndices.Select(i => all[i]).ToList();
        if (subset.Count == 0)
            throw new InvalidOperationException("Не выбрано ни одной страницы.");

        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            tempPath => _engine.ComposeAsync(subset, tempPath, ct),
            tempPath => ValidateAsync(tempPath, subset.Count, ct),
            keepBackup: false,
            ct).ConfigureAwait(false);
    }

    private async Task ValidateAsync(string path, int expectedPageCount, CancellationToken ct)
    {
        var handle = await _engine.OpenAsync(path, null, ct).ConfigureAwait(false);
        await using (handle.ConfigureAwait(false))
        {
            if (handle.Info.PageCount != expectedPageCount)
                throw new PdfEngineException(
                    $"Проверка результата не пройдена: ожидалось {expectedPageCount} страниц, получено {handle.Info.PageCount}.");
            foreach (var page in handle.Info.Pages)
            {
                if (page.WidthPoints < 1 || page.HeightPoints < 1)
                    throw new PdfEngineException("Проверка результата не пройдена: пустая или повреждённая страница.");
            }
        }
    }
}
