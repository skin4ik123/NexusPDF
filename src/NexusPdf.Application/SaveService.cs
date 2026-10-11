using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Signing;

namespace NexusPdf.Application;

/// <summary>Чем закончилось сохранение.</summary>
/// <param name="Reopened">
/// Документ переоткрыт из записанного файла и помечен сохранённым. false —
/// пока шла запись, человек продолжил правку: файл записан, а документ
/// остаётся с его новыми правками и признаком несохранённости.
/// </param>
/// <param name="SignaturesPreserved">Документ подписан, и изменения дописаны так, что подписи целы.</param>
public sealed record SaveResult(bool Reopened, bool SignaturesPreserved);

public sealed class SaveService
{
    private readonly IPdfRenderEngine _engine;

    public SaveService(IPdfRenderEngine engine) => _engine = engine;

    /// <summary>
    /// Документ без структурных правок (один источник, исходный порядок, без
    /// поворотов, удалений аннотаций и нового контента).
    /// </summary>
    public static bool CanSaveDirect(OpenedDocument document) =>
        KeepsPageStructure(document) &&
        document.Session.Model.Pages.All(p => p.OverlayList.Count == 0);

    /// <summary>Страницы на своих местах, но есть добавленные правки.</summary>
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

    /// <summary>
    /// Состав страниц тот же, что у файла-основы: каждая страница на своём
    /// месте, без поворотов и удалений аннотаций. Тогда изменения — только
    /// новые правки и значения полей, и их можно ДОПИСАТЬ к файлу.
    /// </summary>
    private static bool KeepsStructureOf(IReadOnlyList<ComposedPage> composition, IPdfDocumentHandle baseHandle)
    {
        if (composition.Count != baseHandle.Info.PageCount)
            return false;
        for (var i = 0; i < composition.Count; i++)
        {
            var page = composition[i];
            if (!ReferenceEquals(page.Source, baseHandle) || page.SourcePageIndex != i ||
                page.ExtraQuarterTurns % 4 != 0 || page.RemovedAnnotations is { Count: > 0 })
                return false;
        }
        return true;
    }

    /// <summary>
    /// Файл-основа: тот, чьих страниц в документе больше всего (при равенстве —
    /// основной). Именно его структура — закладки, форма, вложения, пароль —
    /// переносится в результат; после оптимизации это обработанный файл.
    /// </summary>
    private static IPdfDocumentHandle ChooseBase(OpenedDocument document, IReadOnlyList<ComposedPage> composition)
    {
        var primary = document.PrimaryHandle;
        var counts = composition
            .GroupBy(p => p.Source, ReferenceEqualityComparer.Instance)
            .Select(g => (Handle: (IPdfDocumentHandle)g.Key!, Count: g.Count()))
            .ToList();
        var best = counts.OrderByDescending(c => c.Count)
            .ThenByDescending(c => ReferenceEquals(c.Handle, primary))
            .FirstOrDefault();
        return best.Handle ?? primary;
    }

    /// <summary>
    /// Текущее состояние документа — в файл <paramref name="targetPath"/>
    /// (временный: подмену делает вызывающий). Правки ложатся на КОПИЮ
    /// файла-основы, поэтому сохраняется всё, что живёт вне страниц: пароль,
    /// закладки, форма, вложения, свойства документа. Открытый документ при
    /// этом только читается.
    /// </summary>
    /// <param name="composition">Готовый состав страниц (с уже применёнными вымарками).</param>
    public async Task WriteCurrentStateAsync(
        OpenedDocument document, IReadOnlyList<ComposedPage> composition, string targetPath,
        InPlaceSaveOptions options, CancellationToken ct)
    {
        if (composition.Count == 0)
            throw new InvalidOperationException("The document has no pages.");

        var baseHandle = ChooseBase(document, composition);
        var password = ReferenceEquals(baseHandle, document.PrimaryHandle) ? document.Password : null;

        // Значение поля, которое сейчас редактируется, фиксируется до копии.
        await baseHandle.FormKillFocusAsync(ct).ConfigureAwait(false);

        // Копия-основа берётся с диска, если файл документа совпадает с тем,
        // что в памяти; иначе — из памяти (там значения полей, введённые в
        // этой сессии, или файл уже заменён прошлым сохранением).
        string? snapshot = null;
        try
        {
            var workingSource = baseHandle.FilePath;
            if (baseHandle.HasUnsavedFormChanges || !document.IsHandleCurrent(baseHandle) ||
                !File.Exists(workingSource))
            {
                snapshot = Path.Combine(Path.GetTempPath(), "NexusPDF-save-" + Guid.NewGuid().ToString("N") + ".pdf");
                await baseHandle.SaveCurrentAsync(snapshot, options.Incremental, ct).ConfigureAwait(false);
                workingSource = snapshot;
            }

            var working = await _engine.OpenAsync(workingSource, password, ct).ConfigureAwait(false);
            await using (working.ConfigureAwait(false))
            {
                await _engine.SaveCompositionInPlaceAsync(
                    working, baseHandle, composition, targetPath, options, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (snapshot != null)
                SafeFileReplace.TryDelete(snapshot);
        }
    }

    /// <summary>
    /// Сохранение документа в файл (в свой или новый).
    ///
    /// Порядок: правки пишутся в копию файла-основы → результат проверяется
    /// повторным открытием → подменяет цель двумя переименованиями (с откатом).
    /// Открытый документ до самого конца не меняется и свой файл не
    /// отпускает: неудача на любом шаге оставляет вкладку рабочей, все правки,
    /// историю и признак несохранённости — на месте, а файл на диске — целым.
    /// </summary>
    public async Task<SaveResult> SaveAsAsync(
        OpenedDocument document, string targetPath, bool keepBackup, CancellationToken ct)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        SafeFileReplace.ThrowIfReadOnly(fullTarget);

        var stamp = document.Session.ChangeStamp;
        var formStamps = new Dictionary<IPdfDocumentHandle, long>(ReferenceEqualityComparer.Instance);
        foreach (var handle in document.Handles.Values)
            formStamps[handle] = handle.FormChangeStamp;

        await using var baked = await document.BuildCompositionBakedAsync(_engine, ct);
        var composition = baked.Composition;
        if (composition.Count == 0)
            throw new InvalidOperationException("The document has no pages.");

        var baseHandle = ChooseBase(document, composition);
        var password = ReferenceEquals(baseHandle, document.PrimaryHandle) ? document.Password : null;

        // Подписанный документ без изменения состава страниц сохраняется
        // ДОПИСЫВАНИЕМ: подписанные байты остаются как были, и подписи целы.
        // Полная перезапись сделала бы их недействительными без единого слова.
        var incremental = KeepsStructureOf(composition, baseHandle) &&
                          await IsSignedAsync(baseHandle, ct);

        var backupPath = keepBackup && !document.BackupTaken && File.Exists(fullTarget)
            ? fullTarget + ".bak"
            : null;

        var outcome = await SafeFileReplace.WriteAndReplaceRetainingAsync(
            fullTarget,
            tempPath => WriteCurrentStateAsync(document, composition, tempPath,
                new InPlaceSaveOptions(Incremental: incremental), ct),
            tempPath => ValidateAsync(tempPath, composition.Count, password, ct),
            backupPath,
            beforeReplace: null,
            ct);
        if (backupPath != null)
            document.BackupTaken = true;
        document.PreserveTemporaryFile(fullTarget);

        // Пока файл записывался, человек мог продолжить работу. Тогда документ
        // НЕ переоткрывается из записанного файла (это стёрло бы новые правки):
        // он остаётся как есть и несохранённым, а дескрипторы, чей файл заменён,
        // помечаются — их содержимое теперь старше файла на диске.
        var editedDuringSave = document.Session.ChangeStamp != stamp ||
                               document.Handles.Values.Any(h =>
                                   !formStamps.TryGetValue(h, out var before) || before != h.FormChangeStamp);
        if (editedDuringSave)
        {
            // Файл записан под новым именем — дальше вкладка сохраняется туда же.
            document.Session.FilePath = fullTarget;
            document.MarkHandlesStale(document.Handles.Values.Where(h =>
                string.Equals(Path.GetFullPath(h.FilePath), fullTarget, StringComparison.OrdinalIgnoreCase)));
            if (outcome.RetiredPath != null)
                document.AddRetiredFile(outcome.RetiredPath);
            return new SaveResult(Reopened: false, SignaturesPreserved: incremental);
        }

        if (outcome.RetiredPath != null)
            document.AddRetiredFile(outcome.RetiredPath);
        await document.RebaseToSavedFileAsync(_engine, fullTarget, password, ct);
        return new SaveResult(Reopened: true, SignaturesPreserved: incremental);
    }

    /// <summary>
    /// Есть ли в файле цифровые подписи. Ошибка чтения — «нет»: она не должна
    /// мешать сохранению, а полная перезапись — прежнее поведение.
    /// </summary>
    private static async Task<bool> IsSignedAsync(IPdfDocumentHandle handle, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(handle.FilePath)) return false;
            var signatures = await PdfSignatureInspector.InspectAsync(handle.FilePath, ct).ConfigureAwait(false);
            return signatures.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Debug(ex, "Подписи документа не прочитались");
            return false;
        }
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

    /// <summary>
    /// Сохранение копии текущего состояния в файл без переключения документа
    /// на него. Структура документа — пароль, закладки, форма — сохраняется
    /// так же, как при обычном сохранении.
    /// </summary>
    public async Task SaveCopyAsync(OpenedDocument document, string targetPath, CancellationToken ct)
    {
        ThrowIfTargetIsOpenSource(document, targetPath);
        await using var baked = await document.BuildCompositionBakedAsync(_engine, ct).ConfigureAwait(false);
        var composition = baked.Composition;
        if (composition.Count == 0)
            throw new InvalidOperationException("Документ не содержит страниц.");

        var baseHandle = ChooseBase(document, composition);
        var password = ReferenceEquals(baseHandle, document.PrimaryHandle) ? document.Password : null;
        await SafeFileReplace.WriteAndReplaceAsync(
            targetPath,
            tempPath => WriteCurrentStateAsync(document, composition, tempPath, default, ct),
            tempPath => ValidateAsync(tempPath, composition.Count, password, ct),
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
