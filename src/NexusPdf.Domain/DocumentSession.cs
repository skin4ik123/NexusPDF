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

    /// <summary>
    /// Растёт при КАЖДОЙ правке, отмене и повторе (в отличие от версии,
    /// которую «Отменить» уменьшает). По нему сохранение узнаёт, менялся ли
    /// документ, пока файл записывался: тогда документ не переоткрывается из
    /// записанного файла, и поздние правки не пропадают.
    /// </summary>
    public long ChangeStamp { get; private set; }

    public event EventHandler? Changed;

    /// <summary>
    /// Перед любой правкой, отменой и повтором — пока модель ещё прежняя. Нужен
    /// тем, кто держит незавершённую правку (набор в текстовом блоке): её
    /// надо зафиксировать до того, как документ изменится под ней.
    /// </summary>
    public event EventHandler? Changing;

    public void Apply(IDocumentOperation operation)
    {
        Changing?.Invoke(this, EventArgs.Empty);
        History.Do(operation, Model);
        _version++;
        ChangeStamp++;
        // После расхождения ветки Redo вернуться в сохранённое состояние по счётчику нельзя.
        if (_savedVersion > _version - 1)
            _savedVersion = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!History.CanUndo) return;
        Changing?.Invoke(this, EventArgs.Empty);
        if (!History.CanUndo) return;
        History.Undo(Model);
        _version--;
        ChangeStamp++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!History.CanRedo) return;
        Changing?.Invoke(this, EventArgs.Empty);
        if (!History.CanRedo) return;
        History.Redo(Model);
        _version++;
        ChangeStamp++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSaved()
    {
        _savedVersion = _version;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
