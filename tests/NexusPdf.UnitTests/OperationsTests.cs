using NexusPdf.Domain;

namespace NexusPdf.UnitTests;

public sealed class OperationsTests
{
    private static readonly Guid Source = Guid.NewGuid();

    private static DocumentModel MakeModel(int pages) =>
        DocumentModel.ForNewSource(Source, "test.pdf", pages);

    private static int[] Order(DocumentModel model) =>
        model.Pages.Select(p => p.SourcePageIndex).ToArray();

    [Fact]
    public void Rotate_Normalizes_Quarter_Turns()
    {
        var model = MakeModel(3);
        new RotatePagesOperation(new[] { 1 }, 3).Apply(model);
        Assert.Equal(3, model.Pages[1].RotationOffset);

        new RotatePagesOperation(new[] { 1 }, 2).Apply(model);
        Assert.Equal(1, model.Pages[1].RotationOffset);

        new RotatePagesOperation(new[] { 1 }, -1).Apply(model);
        Assert.Equal(0, model.Pages[1].RotationOffset);
    }

    [Fact]
    public void Delete_Removes_Scattered_Pages_And_Revert_Restores_Exact_Order()
    {
        var model = MakeModel(6);
        var op = new DeletePagesOperation(new[] { 4, 0, 2 });
        op.Apply(model);
        Assert.Equal(new[] { 1, 3, 5 }, Order(model));

        op.Revert(model);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Order(model));
    }

    [Fact]
    public void Delete_All_Pages_Is_Rejected()
    {
        var model = MakeModel(2);
        Assert.Throws<InvalidOperationException>(
            () => new DeletePagesOperation(new[] { 0, 1 }).Apply(model));
    }

    [Fact]
    public void Move_Block_Forward_Accounts_For_Extracted_Pages()
    {
        var model = MakeModel(6);
        // Переносим страницы 0 и 1 к позиции 4 (позиция до изъятия).
        new MovePagesOperation(new[] { 0, 1 }, 4).Apply(model);
        Assert.Equal(new[] { 2, 3, 0, 1, 4, 5 }, Order(model));
    }

    [Fact]
    public void Move_Block_Backward_And_Revert()
    {
        var model = MakeModel(6);
        var op = new MovePagesOperation(new[] { 4, 5 }, 1);
        op.Apply(model);
        Assert.Equal(new[] { 0, 4, 5, 1, 2, 3 }, Order(model));

        op.Revert(model);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Order(model));
    }

    [Fact]
    public void Duplicate_Inserts_Copies_After_Originals()
    {
        var model = MakeModel(3);
        new DuplicatePagesOperation(new[] { 0, 2 }).Apply(model);
        Assert.Equal(new[] { 0, 0, 1, 2, 2 }, Order(model));
    }

    [Fact]
    public void Insert_Adds_Foreign_Pages_At_Position()
    {
        var model = MakeModel(2);
        var foreign = Guid.NewGuid();
        new InsertPagesOperation(1, new[] { new PageRef(foreign, 0, 0) }).Apply(model);

        Assert.Equal(3, model.Pages.Count);
        Assert.Equal(foreign, model.Pages[1].SourceId);
    }

    [Fact]
    public void UndoRedo_Roundtrip_Restores_States()
    {
        var model = MakeModel(4);
        var stack = new UndoRedoStack();

        stack.Do(new DeletePagesOperation(new[] { 0 }), model);
        stack.Do(new MovePagesOperation(new[] { 0 }, 3), model);
        Assert.Equal(new[] { 2, 3, 1 }, Order(model));

        stack.Undo(model);
        Assert.Equal(new[] { 1, 2, 3 }, Order(model));
        stack.Undo(model);
        Assert.Equal(new[] { 0, 1, 2, 3 }, Order(model));
        Assert.False(stack.CanUndo);

        stack.Redo(model);
        stack.Redo(model);
        Assert.Equal(new[] { 2, 3, 1 }, Order(model));
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Session_Tracks_Dirty_State_Through_Undo_And_Save()
    {
        var session = new DocumentSession(MakeModel(3), "doc.pdf");
        Assert.False(session.IsDirty);

        session.Apply(new RotatePagesOperation(new[] { 0 }, 1));
        Assert.True(session.IsDirty);

        session.Undo();
        Assert.False(session.IsDirty);

        session.Redo();
        Assert.True(session.IsDirty);

        session.MarkSaved();
        Assert.False(session.IsDirty);

        session.Undo();
        Assert.True(session.IsDirty);
    }
}
