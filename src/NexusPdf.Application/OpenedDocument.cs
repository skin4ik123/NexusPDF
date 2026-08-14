using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Открытый документ: логическая сессия плюс движковые дескрипторы всех
/// физических источников, на которые ссылаются её страницы.
/// </summary>
public sealed class OpenedDocument : IAsyncDisposable
{
    private OpenedDocument(DocumentSession session, Guid primarySourceId, IPdfDocumentHandle primaryHandle)
    {
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
            return new OpenedDocument(session, sourceId, handle) { Password = password };
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
        return Handles[page.SourceId].RenderPageAsync(page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct);
    }

    /// <summary>Растр только содержимого страницы (без аннотаций/полей форм) — для OCR.</summary>
    public Task<RenderedPageImage> RenderLogicalPageContentOnlyAsync(int logicalIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var page = Session.Model.Pages[logicalIndex];
        return Handles[page.SourceId].RenderPageContentOnlyAsync(page.SourcePageIndex, pixelWidth, pixelHeight, page.RotationOffset, ct);
    }

    public IReadOnlyList<ComposedPage> BuildComposition() =>
        Session.Model.Pages
            .Select(p => new ComposedPage(
                Handles[p.SourceId], p.SourcePageIndex, p.RotationOffset,
                p.OverlayList, p.RemovedAnnotationList))
            .ToList();

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
