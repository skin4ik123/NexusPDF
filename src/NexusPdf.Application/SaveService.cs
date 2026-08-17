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
    /// При сохранении «в себя» дескрипторы, удерживающие целевой файл (memory-mapped),
    /// освобождаются после проверки и перед подменой: Windows не позволяет заменить
    /// файл с активным отображением в память.
    /// </summary>
    /// <summary>
    /// Документ без структурных правок (один источник, исходный порядок, без
    /// поворотов и нового контента) сохраняется НАПРЯМУЮ из открытого документа:
    /// это сохраняет закладки, формы (включая заполненные значения), вложения и
    /// всю прочую структуру, которую перекомпоновка через ImportPages не переносит.
    /// </summary>
    public static bool CanSaveDirect(OpenedDocument document) =>
        KeepsPageStructure(document) &&
        document.Session.Model.Pages.All(p => p.OverlayList.Count == 0);

    /// <summary>
    /// Страницы стоят на своих местах: один источник, исходный порядок, без
    /// доворотов и без удалённых аннотаций. Добавленные правки при этом
    /// допускаются — их можно наложить прямо на страницы открытого документа.
    ///
    /// Это и есть условие, при котором перекомпоновка не нужна: она собирает
    /// новый файл из импортов страниц и теряет форму AcroForm, оглавление и
    /// вложения. Ради одной надписи на бланке платить формой не за что.
    /// </summary>
    public static bool CanSaveDirectWithOverlays(OpenedDocument document) =>
        KeepsPageStructure(document) &&
        document.Session.Model.Pages.Any(p => p.OverlayList.Count > 0);

    private static bool KeepsPageStructure(OpenedDocument document)
    {
        var model = document.Session.Model;
        if (model.Sources.Count != 1 || document.Handles.Count != 1)
            return false;
        var handle = document.PrimaryHandle;
        if (model.Pages.Count != handle.Info.PageCount)
            return false;
        for (var i = 0; i < model.Pages.Count; i++)
        {
            var page = model.Pages[i];
            if (page.SourceId != document.PrimarySourceId ||
                page.SourcePageIndex != i ||
                page.RotationOffset != 0 ||
                page.RemovedAnnotationList.Count > 0)
                return false;
        }
        return true;
    }

    public async Task SaveAsAsync(OpenedDocument document, string targetPath, bool keepBackup, CancellationToken ct)
    {
        await using var baked = await document.BuildCompositionBakedAsync(_engine, ct).ConfigureAwait(false);
        var composition = baked.Composition;
        var expectedCount = composition.Count;
        if (expectedCount == 0)
            throw new InvalidOperationException("Документ не содержит страниц.");

        var saveDirect = CanSaveDirect(document);
        // Правки можно наложить прямо на страницы открытого документа — тогда
        // форма, оглавление и вложения остаются целыми. Растеризованные
        // вымарывания к этому моменту уже превращены в правки композиции,
        // поэтому берём именно её списки, а не сырые из сессии.
        Dictionary<int, IReadOnlyList<PageOverlay>>? overlaysDirect = null;
        if (!saveDirect && CanSaveDirectWithOverlays(document))
        {
            overlaysDirect = new Dictionary<int, IReadOnlyList<PageOverlay>>();
            for (var i = 0; i < composition.Count; i++)
            {
                if (composition[i].Overlays is { Count: > 0 } overlays)
                    overlaysDirect[i] = overlays;
            }
        }
        // Значение редактируемого поля формы фиксируется только при потере
        // фокуса — иначе компоновка сохранит пустое поле (прямые пути делают
        // это сами внутри сохранения).
        if (!saveDirect && overlaysDirect == null)
            await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        // Прямые пути сохраняют и шифрование исходника.
        var resultPassword = saveDirect || overlaysDirect != null ? document.Password : null;
        var fullTarget = Path.GetFullPath(targetPath);
        var blockingSources = document.Handles
            .Where(kv => string.Equals(Path.GetFullPath(kv.Value.FilePath), fullTarget, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        // Наложение правок на месте МЕНЯЕТ документ в памяти. Если запись не
        // дойдёт до конца, его нужно вернуть к тому, что лежит в файле, иначе
        // повторное сохранение наложит те же правки второй раз.
        var sourcePath = document.PrimaryHandle.FilePath;
        var overlaysApplied = false;
        try
        {
            await SafeFileReplace.WriteAndReplaceAsync(
                targetPath,
                tempPath =>
                {
                    if (saveDirect)
                        return document.PrimaryHandle.SaveCurrentAsync(tempPath, ct);
                    if (overlaysDirect != null)
                    {
                        overlaysApplied = true;
                        return document.PrimaryHandle.SaveWithOverlaysAsync(overlaysDirect, tempPath, ct);
                    }
                    return _engine.ComposeAsync(composition, tempPath, ct);
                },
                tempPath => ValidateAsync(tempPath, expectedCount, resultPassword, ct),
                beforeReplace: async () =>
                {
                    // Компоновка и проверка завершены — эти источники больше не нужны;
                    // RebaseToSavedFileAsync ниже переоткроет документ заново.
                    foreach (var sourceId in blockingSources)
                    {
                        if (document.Handles.Remove(sourceId, out var handle))
                            await handle.DisposeAsync().ConfigureAwait(false);
                    }
                },
                keepBackup,
                ct).ConfigureAwait(false);
        }
        catch when (overlaysApplied)
        {
            // Документ в памяти уже с правками — переоткрываем исходный файл,
            // чтобы состояние в программе снова совпадало с диском.
            await document.RebaseToSavedFileAsync(_engine, sourcePath, document.Password, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        await document.RebaseToSavedFileAsync(_engine, targetPath, resultPassword, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Копию нельзя записывать поверх файла, открытого этим документом: файл
    /// отображён в память и Windows не даст его заменить, а «сохранение в себя»
    /// делается командой «Сохранить». Бросает понятную ошибку заранее.
    /// </summary>
    public static void ThrowIfTargetIsOpenSource(OpenedDocument document, string targetPath)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        if (document.Handles.Values.Any(h =>
                string.Equals(Path.GetFullPath(h.FilePath), fullTarget, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PdfEngineException(
                "Этот файл сейчас открыт в NexusPDF. Выберите другое имя, либо используйте «Сохранить», чтобы записать документ в его собственный файл.");
        }
    }

    /// <summary>Сохранение копии текущего состояния в файл без переключения документа на него.</summary>
    public async Task SaveCopyAsync(OpenedDocument document, string targetPath, CancellationToken ct)
    {
        ThrowIfTargetIsOpenSource(document, targetPath);
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        await using var baked = await document.BuildCompositionBakedAsync(_engine, ct).ConfigureAwait(false);
        var composition = baked.Composition;
        if (composition.Count == 0)
            throw new InvalidOperationException("Документ не содержит страниц.");

        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            tempPath => _engine.ComposeAsync(composition, tempPath, ct),
            tempPath => ValidateAsync(tempPath, composition.Count, ct),
            keepBackup: false,
            ct).ConfigureAwait(false);
    }

    /// <summary>Извлечение выбранных логических страниц в отдельный файл (исходный документ не меняется).</summary>
    public async Task ExtractAsync(OpenedDocument document, IReadOnlyList<int> logicalIndices, string targetPath, CancellationToken ct)
    {
        ThrowIfTargetIsOpenSource(document, targetPath);
        await document.PrimaryHandle.FormKillFocusAsync(ct).ConfigureAwait(false);
        await using var baked = await document.BuildCompositionBakedAsync(_engine, ct).ConfigureAwait(false);
        var all = baked.Composition;
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

    private async Task ValidateAsync(string path, int expectedPageCount, CancellationToken ct) =>
        await ValidateAsync(path, expectedPageCount, null, ct).ConfigureAwait(false);

    private async Task ValidateAsync(string path, int expectedPageCount, string? password, CancellationToken ct)
    {
        var handle = await _engine.OpenAsync(path, password, ct).ConfigureAwait(false);
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
