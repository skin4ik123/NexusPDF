namespace NexusPdf.Domain;

public sealed class UndoRedoStack
{
    private readonly List<IDocumentOperation> _undo = new();
    private readonly List<IDocumentOperation> _redo = new();
    private readonly int _capacity;

    public UndoRedoStack(int capacity = 200) => _capacity = capacity;

    public event EventHandler? StateChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoName => CanUndo ? _undo[^1].Name : null;
    public string? NextRedoName => CanRedo ? _redo[^1].Name : null;

    public void Do(IDocumentOperation operation, DocumentModel model)
    {
        operation.Apply(model);
        _undo.Add(operation);
        _redo.Clear();
        if (_undo.Count > _capacity)
            _undo.RemoveAt(0);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo(DocumentModel model)
    {
        if (!CanUndo) return;
        var op = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        op.Revert(model);
        _redo.Add(op);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo(DocumentModel model)
    {
        if (!CanRedo) return;
        var op = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        op.Apply(model);
        _undo.Add(op);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
