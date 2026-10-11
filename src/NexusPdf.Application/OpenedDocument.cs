using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Открытый документ: логическая сессия плюс движковые дескрипторы всех
/// физических источников, на которые ссылаются её страницы.
/// </summary>
public sealed class OpenedDocument : IAsyncDisposable
{
    /// <summary>Движок нужен документу самому: он рисует страницы с несохранёнными правками.</summary>
    private readonly IPdfRenderEngine _engine;

    private OpenedDocument(
        IPdfRenderEngine engine, DocumentSession session,
        Guid primarySourceId, IPdfDocumentHandle primaryHandle)
    {
        _engine = engine;
        Session = session;
        PrimarySourceId = primarySourceId;
        Handles = new Dictionary<Guid, IPdfDocumentHandle> { [primarySourceId] = primaryHandle };
    }

    public DocumentSession Session { get; }
    public Guid PrimarySourceId { get; private set; }
    public Dictionary<Guid, IPdfDocumentHandle> Handles { get; }

    /// <summary>Пароль, которым документ был открыт: нужен для валидации и
    /// переоткрытия после ПРЯМОГО сохранения (шифрование сохраняется в копии).</summary>
    public string? Password { get; private set; }

    public IPdfDocumentHandle PrimaryHandle => Handles[PrimarySourceId];

    /// <summary>
    /// Резервная копия исходного файла уже сделана в этой сессии. Копия
    /// делается один раз — до ПЕРВОГО сохранения: раньше каждое сохранение
    /// перезаписывало «.bak», и после второго в нём лежала первая сохранённая
    /// версия, а не исходный файл.
    /// </summary>
    public bool BackupTaken { get; set; }

    // Дескрипторы, чей файл на диске уже заменён сохранением, которое документ
    // не переоткрыло (человек правил, пока шла запись): их содержимое — прежняя
    // версия, и брать «их файл» с диска для следующего сохранения нельзя.
    private readonly HashSet<IPdfDocumentHandle> _staleHandles = new(ReferenceEqualityComparer.Instance);

    // Прежние версии файлов, отодвинутые сохранением: удаляются, когда их
    // перестают читать дескрипторы документа.
    private readonly List<string> _retiredFiles = new();
    private readonly Dictionary<string, Infrastructure.TemporaryFileLease> _temporaryFiles =
        new(StringComparer.OrdinalIgnoreCase);

    public void OwnTemporaryFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!_temporaryFiles.ContainsKey(path))
            _temporaryFiles[path] = Infrastructure.TemporaryFileLease.Own(path);
    }

    private void RetainTemporaryFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!_temporaryFiles.ContainsKey(path) && Infrastructure.TemporaryFileLease.TryRetain(path) is { } lease)
            _temporaryFiles[path] = lease;
    }

    public void PreserveTemporaryFile(string path)
    {
        if (_temporaryFiles.TryGetValue(Path.GetFullPath(path), out var lease)) lease.Preserve();
    }

    /// <summary>Файл дескриптора на диске совпадает с тем, что дескриптор читает.</summary>
    public bool IsHandleCurrent(IPdfDocumentHandle handle) => !_staleHandles.Contains(handle);

    /// <summary>Сохранение заменило файлы этих дескрипторов, а документ остался на прежних данных.</summary>
    public void MarkHandlesStale(IEnumerable<IPdfDocumentHandle> handles)
    {
        foreach (var handle in handles)
            _staleHandles.Add(handle);
    }

    /// <summary>Прежняя версия файла: удалить, как только её отпустят дескрипторы.</summary>
    public void AddRetiredFile(string path) => _retiredFiles.Add(path);

    private void DeleteRetiredFiles()
    {
        _retiredFiles.RemoveAll(Infrastructure.SafeFileReplace.TryDelete);
    }

    public string DisplayName =>
        Session.FilePath is { } p ? Path.GetFileName(p) : "Без имени";

    public static async Task<OpenedDocument> OpenAsync(
        IPdfRenderEngine engine, string filePath, string? password, CancellationToken ct)
    {
        var handle = await engine.OpenAsync(filePath, password, ct).ConfigureAwait(false);
        try
        {
            var sourceId = Guid.NewGuid();
            var model = DocumentModel.ForNewSource(sourceId, filePath, handle.Info.PageCount);
            var session = new DocumentSession(model, filePath);
            var document = new OpenedDocument(engine, session, sourceId, handle) { Password = password };
            document.RetainTemporaryFile(filePath);
            return document;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Размер логической страницы в пунктах с учётом добавочного поворота.</summary>
    public PdfPageDescriptor GetLogicalPageSize(int logicalIndex)
    {
        var page = Session.Model.Pages[logicalIndex];
        var descriptor = Handles[page.SourceId].Info.Pages[page.SourcePageIndex];
        return page.RotationOffset % 2 == 0
            ? descriptor
            : new PdfPageDescriptor(descriptor.HeightPoints, descriptor.WidthPoints);
    }

    public async Task<RenderedPageImage> RenderLogicalPageAsync(int logicalIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        var handle = Handles[page.SourceId];

        if (page.RemovedAnnotationList.Count > 0)
        {
            var (edited, index, rotation) = await ResolveExportPageAsync(logicalIndex, ct).ConfigureAwait(false);
            return await edited.RenderPageAsync(index, pixelWidth, pixelHeight, rotation, ct).ConfigureAwait(false);
        }

        if (page.OverlayList.Count == 0)
        {
            return await handle.RenderPageAsync(
                page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct).ConfigureAwait(false);
        }

        // Пометки поверх страницы (заметки, фигуры, рисунки, измерения,
        // надписи, вымарки) рисуются отдельным прозрачным слоем над обычным
        // растром — тем же кодом запекания, что и при сохранении, поэтому
        // экран совпадает с будущим файлом. Содержимое страницы при этом не
        // копируется в промежуточный документ: на тяжёлом чертеже именно это
        // занимало секунды после каждого щелчка. И поля формы остаются на
        // месте: растр берётся из живого документа, где они нарисованы.
        if (OverlayLayers.CanDrawAsLayer(page.OverlayList))
        {
            var content = await handle.RenderPageAsync(
                page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct).ConfigureAwait(false);
            var layer = await RenderOverlayLayerAsync(logicalIndex, pixelWidth, pixelHeight, ct).ConfigureAwait(false);
            return OverlayCompositor.BlendOver(content, layer);
        }

        // Правка самого содержимого (замена текста или картинки на странице)
        // видна только в перерисованной странице — она и рисуется целиком.
        return await _engine.RenderPageWithOverlaysAsync(
            handle, page.SourcePageIndex, page.RotationOffset,
            page.OverlayList, pixelWidth, pixelHeight, ct).ConfigureAwait(false);
    }

    /// <summary>Все правки страницы можно показать слоем поверх её обычного растра.</summary>
    public bool CanDrawOverlaysAsLayer(int logicalIndex) =>
        Session.Model.Pages[logicalIndex].RemovedAnnotationList.Count == 0 &&
        OverlayLayers.CanDrawAsLayer(Session.Model.Pages[logicalIndex].OverlayList);

    /// <summary>Растр страницы без несохранённых правок — основа для слоя правок.</summary>
    public Task<RenderedPageImage> RenderLogicalPageContentAsync(
        int logicalIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        return Handles[page.SourceId].RenderPageAsync(
            page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct);
    }

    /// <summary>Только правки страницы, на прозрачном фоне, в рамке её обычного растра.</summary>
    public Task<RenderedPageImage> RenderOverlayLayerAsync(
        int logicalIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        return _engine.RenderOverlayLayerAsync(
            Handles[page.SourceId], page.SourcePageIndex, page.RotationOffset,
            page.OverlayList, pixelWidth, pixelHeight, ct);
    }

    // Испечённые страницы: по одной на логическую страницу с правками.
    // Держим их, чтобы текст, поиск и выделение работали по тому, что видит
    // пользователь, а не по исходному файлу без его правок.
    private sealed record BakedPage(PageRef Reference, long FormStamp, IPdfDocumentHandle Handle, string? TemporaryPath, int ExtraRotation);
    private readonly Dictionary<(int Page, bool Export), BakedPage> _baked = new();
    private readonly SemaphoreSlim _bakeGate = new(1, 1);

    /// <summary>
    /// Дескриптор и номер страницы, ПО КОТОРЫМ нужно спрашивать текст, ссылки
    /// и координаты символов. Для страницы с несохранёнными правками это
    /// испечённая копия: иначе поиск и выделение не видели бы ни распознанный
    /// текст, ни добавленные надписи до сохранения файла.
    /// </summary>
    public async Task<(IPdfDocumentHandle Handle, int PageIndex)> ResolveTextPageAsync(
        int logicalIndex, CancellationToken ct)
    {
        var (handle, index, _) = await ResolveEditedPageAsync(logicalIndex, false, ct).ConfigureAwait(false);
        return (handle, index);
    }

    /// <summary>Exports include images, comments, removals and applied redactions.</summary>
    public Task<(IPdfDocumentHandle Handle, int PageIndex, int ExtraRotation)> ResolveExportPageAsync(
        int logicalIndex, CancellationToken ct) => ResolveEditedPageAsync(logicalIndex, true, ct);

    private async Task<(IPdfDocumentHandle Handle, int PageIndex, int ExtraRotation)> ResolveEditedPageAsync(
        int logicalIndex, bool forExport, CancellationToken ct)
    {
        await _bakeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        var page = Session.Model.Pages[logicalIndex];
        // Испечённая копия нужна только правкам, которые меняют ТЕКСТ страницы.
        // Заметка, фигура или измерение текста не добавляют — и раньше ради них
        // каждый раз копировалась вся страница, на чертеже это секунды.
        var changed = forExport
            ? page.OverlayList.Count > 0 || page.RemovedAnnotationList.Count > 0
            : page.OverlayList.Any(OverlayLayers.ChangesText);
        if (!changed)
            return (Handles[page.SourceId], page.SourcePageIndex, page.RotationOffset);

        var key = (logicalIndex, forExport);
        var source = Handles[page.SourceId];
        var stamp = source.FormChangeStamp;
        if (_baked.TryGetValue(key, out var cached))
        {
            if (cached.Reference == page && cached.FormStamp == stamp)
                return (cached.Handle, 0, cached.ExtraRotation);
            _baked.Remove(key);
            await cached.Handle.DisposeAsync().ConfigureAwait(false);
            if (cached.TemporaryPath != null) Infrastructure.SafeFileReplace.TryDelete(cached.TemporaryPath);
        }

        IPdfDocumentHandle handle;
        string? temporary = null;
        var remainingRotation = page.RotationOffset;
        var composed = new ComposedPage(source, page.SourcePageIndex, page.RotationOffset,
            page.OverlayList, page.RemovedAnnotationList);
        if (forExport && (page.RemovedAnnotationList.Count > 0 || RedactionBaker.HasRedactions(new[] { composed })))
        {
            temporary = Path.Combine(Path.GetTempPath(), "nexus-export-" + Guid.NewGuid().ToString("N") + ".pdf");
            remainingRotation = 0; // Compose writes the extra rotation into the page.
            try
            {
                await using var baked = await RedactionBaker.BakeAsync(_engine, this, new[] { composed }, ct)
                    .ConfigureAwait(false);
                await _engine.ComposeAsync(baked.Composition, temporary, ct).ConfigureAwait(false);
                handle = await _engine.OpenAsync(temporary, null, ct).ConfigureAwait(false);
            }
            catch
            {
                Infrastructure.SafeFileReplace.TryDelete(temporary);
                throw;
            }
        }
        else
            handle = await _engine.CreateBakedPageAsync(
                source, page.SourcePageIndex, page.RotationOffset, page.OverlayList, ct).ConfigureAwait(false);
        _baked[key] = new BakedPage(page, stamp, handle, temporary, remainingRotation);
        return (handle, 0, remainingRotation);
        }
        finally { _bakeGate.Release(); }
    }

    /// <summary>Освобождает испечённые страницы (после сохранения и при закрытии).</summary>
    public async Task DropBakedPagesAsync()
    {
        await _bakeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var pages = _baked.Values.ToList();
            _baked.Clear();
            foreach (var page in pages)
            {
                await page.Handle.DisposeAsync().ConfigureAwait(false);
                if (page.TemporaryPath != null) Infrastructure.SafeFileReplace.TryDelete(page.TemporaryPath);
            }
        }
        finally { _bakeGate.Release(); }
    }

    /// <summary>
    /// Отпечаток правок страницы для ключа кэша растров: без него после
    /// добавления правки вернулась бы старая картинка из кэша.
    /// </summary>
    public int GetOverlaySignature(int logicalIndex)
    {
        var page = Session.Model.Pages[logicalIndex];
        var overlays = page.OverlayList;
        if (overlays.Count == 0 && page.RemovedAnnotationList.Count == 0)
            return 0;
        var hash = new HashCode();
        foreach (var overlay in overlays)
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(overlay));
        foreach (var removed in page.RemovedAnnotationList) hash.Add(removed);
        var signature = hash.ToHashCode();
        return signature == 0 ? 1 : signature;
    }

    /// <summary>Растр только содержимого страницы (без аннотаций/полей форм) — для OCR.</summary>
    public Task<RenderedPageImage> RenderLogicalPageContentOnlyAsync(int logicalIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        return Handles[page.SourceId].RenderPageContentOnlyAsync(page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct);
    }

    /// <summary>Растр страницы для печати с заданным составом содержимого.</summary>
    public Task<RenderedPageImage> RenderLogicalPageForPrintAsync(
        int logicalIndex, int pixelWidth, int pixelHeight, PrintContentOptions options, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        return Handles[page.SourceId].RenderPageForPrintAsync(
            page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, options, ct);
    }

    /// <summary>
    /// Композиция с ПРИМЕНЁННЫМИ вымарками: страницы с RedactionDraft заменены
    /// растровыми. Возвращённый объект держит временный источник — освобождать
    /// (await using) ПОСЛЕ компоновки результата.
    /// </summary>
    public async Task<RedactionBaker.BakeResult> BuildCompositionBakedAsync(
        IPdfRenderEngine engine, CancellationToken ct)
    {
        var composition = BuildComposition();
        return await RedactionBaker.BakeAsync(engine, this, composition, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<ComposedPage> BuildComposition() =>
        Session.Model.Pages
            .Select(p => new ComposedPage(
                Handles[p.SourceId], p.SourcePageIndex, p.RotationOffset,
                p.OverlayList, p.RemovedAnnotationList))
            .ToList();

    /// <summary>Что получилось при вставке файлов: сколько добавлено и что пропущено.</summary>
    /// <param name="PagesAdded">Сколько страниц добавлено.</param>
    /// <param name="FilesUsed">Сколько файлов пригодилось.</param>
    /// <param name="Skipped">Файлы, которые взять не удалось, с причиной.</param>
    public sealed record InsertFilesResult(
        int PagesAdded, int FilesUsed, IReadOnlyList<(string File, string Reason)> Skipped);

    /// <summary>Расширения, которые вставляются как страница-картинка.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
    };

    /// <summary>Файл, который вставляется страницей-картинкой.</summary>
    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsPdfFile(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Вставка ФАЙЛОВ (перетащенных из Проводника) на заданную позицию.
    ///
    /// PDF отдаёт свои страницы целиком; картинки собираются в один временный
    /// PDF и становятся страницами. Временный файл живёт до закрытия документа:
    /// страницы ссылаются на него ровно так же, как на любой другой источник,
    /// и при сохранении запекаются в итоговый файл.
    ///
    /// Всё добавляется ОДНОЙ операцией: одно нажатие «Отменить» возвращает
    /// документ к тому, что было до перетаскивания, сколько бы файлов ни
    /// бросили.
    ///
    /// Непригодное не роняет всю вставку: защищённый паролем или битый файл
    /// пропускается с причиной, остальные вставляются.
    /// </summary>
    public async Task<InsertFilesResult> InsertFilesAsync(
        IPdfRenderEngine engine, IReadOnlyList<string> paths, int insertIndex,
        Func<string, ImagePageSpec> decodeImage, string tempFolder,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct,
        Func<string, string, CancellationToken, Task<string>>? convertOffice = null)
    {
        var skipped = new List<(string File, string Reason)>();
        // Порядок сохраняется ТОТ, в котором файлы дали: картинки собираются в
        // общий временный PDF, но их места в списке заняты заранее — иначе
        // картинка, брошенная первой, оказалась бы последней.
        var collected = new List<PageRef?>();
        var imageSlots = new List<int>();
        var opened = new List<IPdfDocumentHandle>();
        var images = new List<ImagePageSpec>();
        var generated = new List<string>();
        var filesUsed = 0;
        var done = 0;

        try
        {
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((done, paths.Count));
                done++;

                if (!File.Exists(path))
                {
                    skipped.Add((path, "файл не найден"));
                    continue;
                }

                var extension = Path.GetExtension(path);
                if (ImageExtensions.Contains(extension))
                {
                    try
                    {
                        images.Add(decodeImage(path));
                        imageSlots.Add(collected.Count);
                        collected.Add(null); // место под будущую страницу-картинку
                        filesUsed++;
                    }
                    catch (Exception ex)
                    {
                        skipped.Add((path, ex.Message));
                    }
                    continue;
                }

                var pdfPath = path;
                if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // Документ Office превращается в PDF тем же способом, что и
                    // по команде «Создать PDF из Office»: экспортом, а не
                    // печатью, — со ссылками и оглавлением.
                    if (convertOffice == null)
                    {
                        skipped.Add((path, "не PDF и не изображение"));
                        continue;
                    }
                    try
                    {
                        pdfPath = await convertOffice(path, tempFolder, ct).ConfigureAwait(false);
                        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(pdfPath), StringComparison.OrdinalIgnoreCase) &&
                            Path.GetFullPath(pdfPath).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempFolder)) +
                                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            generated.Add(pdfPath);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add((path, ex.Message));
                        continue;
                    }
                }

                try
                {
                    var (sourceId, handle) = await EnsureSourceAsync(engine, pdfPath, null, ct)
                        .ConfigureAwait(false);
                    if (handle != null) opened.Add(handle);
                    for (var i = 0; i < Handles[sourceId].Info.PageCount; i++)
                        collected.Add(new PageRef(sourceId, i, 0));
                    filesUsed++;
                }
                catch (PdfPasswordRequiredException)
                {
                    // Пароль здесь не спросить — перетаскивание не место для
                    // диалогов; файл честно пропускается с причиной.
                    skipped.Add((path, "защищён паролем"));
                }
                catch (Exception ex)
                {
                    skipped.Add((path, ex.Message));
                }
            }

            if (images.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(tempFolder);
                var temporary = Path.Combine(tempFolder, $"images-{Guid.NewGuid():N}.pdf");
                generated.Add(temporary);
                await engine.CreateImageDocumentAsync(images, temporary, ct).ConfigureAwait(false);
                var (sourceId, handle) = await EnsureSourceAsync(engine, temporary, null, ct).ConfigureAwait(false);
                if (handle != null) opened.Add(handle);
                for (var i = 0; i < imageSlots.Count && i < Handles[sourceId].Info.PageCount; i++)
                    collected[imageSlots[i]] = new PageRef(sourceId, i, 0);
            }

            var pages = collected.Where(p => p != null).Select(p => p!).ToList();
            if (pages.Count > 0)
            {
                var index = Math.Clamp(insertIndex, 0, Session.Model.Pages.Count);
                foreach (var temporary in generated) OwnTemporaryFile(temporary);
                Session.Apply(new InsertPagesOperation(index, pages));
            }
            progress?.Report((paths.Count, paths.Count));
            return new InsertFilesResult(pages.Count, filesUsed, skipped);
        }
        catch
        {
            foreach (var handle in opened)
            {
                var id = Handles.FirstOrDefault(pair => ReferenceEquals(pair.Value, handle)).Key;
                if (id != Guid.Empty)
                {
                    Handles.Remove(id);
                    Session.Model.Sources.Remove(id);
                }
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            foreach (var temporary in generated)
                if (!_temporaryFiles.ContainsKey(Path.GetFullPath(temporary)))
                    Infrastructure.SafeFileReplace.TryDelete(temporary);
        }
    }

    /// <summary>
    /// Источник по пути файла: уже известный переиспользуется, новый
    /// открывается. Второй элемент — дескриптор, ЕСЛИ он открыт сейчас (его
    /// придётся закрыть при откате).
    /// </summary>
    private async Task<(Guid SourceId, IPdfDocumentHandle? Opened)> EnsureSourceAsync(
        IPdfRenderEngine engine, string path, string? password, CancellationToken ct)
    {
        var known = Session.Model.Sources
            .FirstOrDefault(p => string.Equals(p.Value, path, StringComparison.OrdinalIgnoreCase));
        if (known.Value != null)
            return (known.Key, null);

        var handle = await engine.OpenAsync(path, password, ct).ConfigureAwait(false);
        var id = Guid.NewGuid();
        RetainTemporaryFile(path);
        Handles[id] = handle;
        Session.Model.Sources[id] = path;
        return (id, handle);
    }

    /// <summary>
    /// Вставка страниц ДРУГОГО открытого документа.
    ///
    /// Файл-источник открывается этим документом СВОИМ дескриптором: делить
    /// дескриптор между документами нельзя — закрытие одной вкладки утащило бы
    /// страницы из другой. Если тот же файл уже числится источником, он и
    /// используется, второй раз файл не открывается.
    ///
    /// Ссылки переносятся целиком, вместе с поворотом и несохранёнными
    /// правками страницы: перенос не должен молча терять работу.
    /// </summary>
    /// <returns>Сколько страниц вставлено.</returns>
    public async Task<int> InsertPagesFromAsync(
        IPdfRenderEngine engine, OpenedDocument source, IReadOnlyList<int> logicalIndices,
        int insertIndex, CancellationToken ct)
    {
        if (ReferenceEquals(source, this))
            throw new InvalidOperationException("Страницы переносятся между РАЗНЫМИ документами.");

        var wanted = logicalIndices
            .Where(i => i >= 0 && i < source.Session.Model.Pages.Count)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
        if (wanted.Count == 0) return 0;

        var remap = new Dictionary<Guid, Guid>();
        var opened = new List<IPdfDocumentHandle>();
        try
        {
            foreach (var sourceId in wanted.Select(i => source.Session.Model.Pages[i].SourceId).Distinct())
            {
                if (!source.Session.Model.Sources.TryGetValue(sourceId, out var path))
                    throw new PdfEngineException("У переносимой страницы неизвестен файл-источник.");

                var (newId, handle) = await EnsureSourceAsync(engine, path, source.Password, ct)
                    .ConfigureAwait(false);
                if (handle != null) opened.Add(handle);
                remap[sourceId] = newId;
            }

            var pages = wanted
                .Select(i => source.Session.Model.Pages[i])
                .Select(p => p with { SourceId = remap[p.SourceId] })
                .ToList();

            var index = Math.Clamp(insertIndex, 0, Session.Model.Pages.Count);
            Session.Apply(new InsertPagesOperation(index, pages));
            return pages.Count;
        }
        catch
        {
            // Открытые ради переноса дескрипторы не должны пережить неудачу.
            foreach (var handle in opened)
            {
                var id = Handles.FirstOrDefault(p => ReferenceEquals(p.Value, handle)).Key;
                if (id != Guid.Empty)
                {
                    Handles.Remove(id);
                    Session.Model.Sources.Remove(id);
                }
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Переключить документ на ОБРАБОТАННЫЙ файл, никуда его не сохраняя.
    ///
    /// Так работают чистка сканов, пересжатие и оптимизация: результат ложится
    /// во временный файл, тот становится источником вкладки, документ помечается
    /// изменённым — и куда его положить, пользователь решает потом обычным
    /// сохранением. Промежуточное «сохранить как» из середины работы исчезает.
    ///
    /// Прежние дескрипторы НЕ закрываются: на них держится «Отменить». Временный
    /// файл живёт до закрытия документа — ровно как вставленные из Проводника.
    /// </summary>
    /// <returns>Сколько страниц стало в документе.</returns>
    public async Task<int> SwitchToProcessedFileAsync(
        IPdfRenderEngine engine, string processedPath, CancellationToken ct)
    {
        var (sourceId, opened) = await EnsureSourceAsync(engine, processedPath, null, ct)
            .ConfigureAwait(false);
        try
        {
            var pageCount = Handles[sourceId].Info.PageCount;
            Session.Apply(new ReplacePagesWithSourceOperation(sourceId, pageCount));
            // Испечённые страницы относились к прежним ссылкам: оставить их —
            // значит показывать поиском текст документа, которого уже нет.
            await DropBakedPagesAsync().ConfigureAwait(false);
            return pageCount;
        }
        catch
        {
            if (opened != null)
            {
                Handles.Remove(sourceId);
                Session.Model.Sources.Remove(sourceId);
                await opened.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// После сохранения в новый файл (или на место старого) документ переоткрывается,
    /// чтобы ссылки страниц указывали на актуальный источник. История очищается.
    /// </summary>
    public async Task RebaseToSavedFileAsync(IPdfRenderEngine engine, string savedPath, string? password, CancellationToken ct)
    {
        // Без ConfigureAwait(false): модель документа меняется ниже, и делать
        // это надо в потоке вызывающего (интерфейса), а не в фоновом — иначе
        // перестройка списка страниц шла бы параллельно с его чтением.
        var newHandle = await engine.OpenAsync(savedPath, password, ct);
        Password = password;
        var oldHandles = Handles.Values.ToList();

        try
        {
            var newSourceId = Guid.NewGuid();
            Handles.Clear();
            Handles[newSourceId] = newHandle;
            PrimarySourceId = newSourceId;

            Session.Model.Sources.Clear();
            Session.Model.Sources[newSourceId] = savedPath;
            Session.Model.Pages.Clear();
            for (var i = 0; i < newHandle.Info.PageCount; i++)
                Session.Model.Pages.Add(new PageRef(newSourceId, i, 0));

            Session.FilePath = savedPath;
            Session.History.Clear();
            Session.MarkSaved();
        }
        finally
        {
            // Старые дескрипторы (и их memory-mapped файлы) освобождаются даже если
            // обработчик события Changed выбросил исключение — иначе файл-источник
            // остался бы заблокированным до конца работы приложения.
            foreach (var handle in oldHandles)
                await handle.DisposeAsync().ConfigureAwait(false);
            _staleHandles.Clear();
            DeleteRetiredFiles();
            var live = Session.Model.Sources.Values.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var temporary in _temporaryFiles.Keys.Where(p => !live.Contains(p)).ToArray())
            {
                _temporaryFiles[temporary].Dispose();
                _temporaryFiles.Remove(temporary);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropBakedPagesAsync().ConfigureAwait(false);
        foreach (var handle in Handles.Values)
            await handle.DisposeAsync().ConfigureAwait(false);
        Handles.Clear();
        _staleHandles.Clear();
        DeleteRetiredFiles();
        foreach (var lease in _temporaryFiles.Values) lease.Dispose();
        _temporaryFiles.Clear();
    }
}
