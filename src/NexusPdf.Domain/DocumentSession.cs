namespace NexusPdf.Domain;

/// <summary>
/// Сессия редактирования одного документа: логическая структура, история
/// операций и признак несохранённых изменений. Файл на диске остаётся
/// неизменным до явного сохранения.
/// </summary>
public sealed class DocumentSession
{
    private int _version;
    private int _savedVersion;

    public DocumentSession(DocumentModel model, string? filePath)
    {
        Model = model;
        FilePath = filePath;
        History = new UndoRedoStack();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DocumentModel Model { get; }
    public string? FilePath { get; set; }
    public UndoRedoStack History { get; }

    public bool IsDirty => _version != _savedVersion;

    public event EventHandler? Changed;

    public void Apply(IDocumentOperation operation)
    {
        History.Do(operation, Model);
        _version++;
        // После расхождения ветки Redo вернуться в сохранённое состояние по счётчику нельзя.
        if (_savedVersion > _version - 1)
            _savedVersion = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!History.CanUndo) return;
        History.Undo(Model);
        _version--;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!History.CanRedo) return;
        History.Redo(Model);
        _version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSaved()
    {
        _savedVersion = _version;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
