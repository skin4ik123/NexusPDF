namespace NexusPdf.Domain;

/// <summary>Изменяемое логическое состояние документа: порядок страниц и известные источники.</summary>
public sealed class DocumentModel
{
    public List<PageRef> Pages { get; } = new();

    /// <summary>Источники страниц: идентификатор → путь к физическому файлу.</summary>
    public Dictionary<Guid, string> Sources { get; } = new();

    public static DocumentModel ForNewSource(Guid sourceId, string filePath, int pageCount)
    {
        var model = new DocumentModel();
        model.Sources[sourceId] = filePath;
        for (var i = 0; i < pageCount; i++)
            model.Pages.Add(new PageRef(sourceId, i, 0));
        return model;
    }
}
