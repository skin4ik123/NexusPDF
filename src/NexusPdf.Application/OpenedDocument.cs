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
            return new OpenedDocument(engine, session, sourceId, handle) { Password = password };
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

    public Task<RenderedPageImage> RenderLogicalPageAsync(int logicalIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        var handle = Handles[page.SourceId];

        // Страница с несохранёнными правками рисуется ВМЕСТЕ с ними — тем же
        // кодом запекания, что и при сохранении. Иначе экран показывает не то,
        // что окажется в файле (а часть правок не показывает вообще).
        if (page.OverlayList.Count > 0)
        {
            return _engine.RenderPageWithOverlaysAsync(
                handle, page.SourcePageIndex, page.RotationOffset,
                page.OverlayList, pixelWidth, pixelHeight, ct);
        }

        return handle.RenderPageAsync(page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct);
    }

    // Испечённые страницы: по одной на логическую страницу с правками.
    // Держим их, чтобы текст, поиск и выделение работали по тому, что видит
    // пользователь, а не по исходному файлу без его правок.
    private readonly Dictionary<int, (int Signature, IPdfDocumentHandle Handle)> _baked = new();

    /// <summary>
    /// Дескриптор и номер страницы, ПО КОТОРЫМ нужно спрашивать текст, ссылки
    /// и координаты символов. Для страницы с несохранёнными правками это
    /// испечённая копия: иначе поиск и выделение не видели бы ни распознанный
    /// текст, ни добавленные надписи до сохранения файла.
    /// </summary>
    public async Task<(IPdfDocumentHandle Handle, int PageIndex)> ResolveTextPageAsync(
        int logicalIndex, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        var signature = GetOverlaySignature(logicalIndex);
        if (signature == 0)
            return (Handles[page.SourceId], page.SourcePageIndex);

        if (_baked.TryGetValue(logicalIndex, out var cached))
        {
            if (cached.Signature == signature)
                return (cached.Handle, 0);
            _baked.Remove(logicalIndex);
            await cached.Handle.DisposeAsync().ConfigureAwait(false);
        }

        var handle = await _engine.CreateBakedPageAsync(
            Handles[page.SourceId], page.SourcePageIndex, page.RotationOffset,
            page.OverlayList, ct).ConfigureAwait(false);
        _baked[logicalIndex] = (signature, handle);
        return (handle, 0);
    }

    /// <summary>Освобождает испечённые страницы (после сохранения и при закрытии).</summary>
    public async Task DropBakedPagesAsync()
    {
        var handles = _baked.Values.Select(v => v.Handle).ToList();
        _baked.Clear();
        foreach (var handle in handles)
            await handle.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Отпечаток правок страницы для ключа кэша растров: без него после
    /// добавления правки вернулась бы старая картинка из кэша.
    /// </summary>
    public int GetOverlaySignature(int logicalIndex)
    {
        var overlays = Session.Model.Pages[logicalIndex].OverlayList;
        if (overlays.Count == 0)
            return 0;
        var hash = new HashCode();
        foreach (var overlay in overlays)
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(overlay));
        return hash.ToHashCode();
    }

    /// <summary>Растр только содержимого страницы (без аннотаций/полей форм) — для OCR.</summary>
    public Task<RenderedPageImage> RenderLogicalPageContentOnlyAsync(int logicalIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        return Handles[page.SourceId].RenderPageContentOnlyAsync(page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct);
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
    /// После сохранения в новый файл (или на место старого) документ переоткрывается,
    /// чтобы ссылки страниц указывали на актуальный источник. История очищается.
    /// </summary>
    public async Task RebaseToSavedFileAsync(IPdfRenderEngine engine, string savedPath, string? password, CancellationToken ct)
    {
        var newHandle = await engine.OpenAsync(savedPath, password, ct).ConfigureAwait(false);
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
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var handle in Handles.Values)
            await handle.DisposeAsync().ConfigureAwait(false);
        Handles.Clear();
    }
}
