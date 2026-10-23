using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Что попадает в историю правок, когда ввод в блоке закончен: каждое
/// завершение — один шаг отмены, правка сохранённого блока — его замена с тем
/// же Id, повторная правка — та же замена, а не стопка новых.
/// </summary>
public sealed class TextBlockSessionTests
{
    private static DocumentSession Session() =>
        new(DocumentModel.ForNewSource(Guid.NewGuid(), "test.pdf", 2), "test.pdf");

    private static TextBlockOverlay Block(string text, Guid id = default) =>
        new(10, 10, 100, 20, 0, TextBlockWidthMode.AutoWidth, true, false,
            [new TextParagraph([new TextRun(text, new TextStyle("Arial", 11))])], id);

    private static IReadOnlyList<PageOverlay> Overlays(DocumentSession session) => session.Model.Pages[1].OverlayList;

    [Fact]
    public void New_Block_Is_One_Undo_Step()
    {
        var session = Session();
        TextBlockSessionService.Commit(session, 1, null, null, Block("hello"));

        var added = Assert.IsType<TextBlockOverlay>(Assert.Single(Overlays(session)));
        Assert.NotEqual(Guid.Empty, added.BlockId);
        Assert.True(session.History.CanUndo);

        session.Undo();
        Assert.Empty(Overlays(session));
        Assert.False(session.History.CanUndo);
    }

    [Fact]
    public void Empty_New_Block_Leaves_Nothing()
    {
        var session = Session();
        TextBlockSessionService.Commit(session, 1, null, null, Block(""));
        TextBlockSessionService.Commit(session, 1, null, null, null);

        Assert.Empty(Overlays(session));
        Assert.False(session.History.CanUndo);
    }

    [Fact]
    public void Edited_Draft_Is_Replaced_And_Emptied_Draft_Is_Removed()
    {
        var session = Session();
        TextBlockSessionService.Commit(session, 1, null, null, Block("first"));
        var draft = (TextBlockOverlay)Overlays(session)[0];

        TextBlockSessionService.Commit(session, 1, draft, null, Block("second"));
        var edited = Assert.IsType<TextBlockOverlay>(Assert.Single(Overlays(session)));
        Assert.Equal("second", edited.Paragraphs[0].Text);
        Assert.Equal(draft.BlockId, edited.BlockId);

        TextBlockSessionService.Commit(session, 1, edited, null, Block(""));
        Assert.Empty(Overlays(session));
    }

    [Fact]
    public void Editing_A_Stored_Block_Adds_A_Replacement_With_Its_Id()
    {
        var session = Session();
        session.Apply(new RotatePagesOperation([1], 1));   // правят на повёрнутой странице
        var id = Guid.NewGuid();
        TextBlockSessionService.Commit(session, 1, null, id, Block("changed"));

        var replacement = Assert.IsType<TextBlockReplacement>(Assert.Single(Overlays(session)));
        Assert.Equal(id, replacement.BlockId);
        Assert.Equal(id, replacement.NewBlock!.BlockId);
        Assert.Equal(1, replacement.PlacedRotation);
    }

    [Fact]
    public void Deleting_A_Stored_Block_Adds_An_Empty_Replacement()
    {
        var session = Session();
        var id = Guid.NewGuid();
        TextBlockSessionService.Commit(session, 1, null, id, Block(""));

        var replacement = Assert.IsType<TextBlockReplacement>(Assert.Single(Overlays(session)));
        Assert.Equal(id, replacement.BlockId);
        Assert.Null(replacement.NewBlock);
    }

    [Fact]
    public void Second_Edit_Of_A_Stored_Block_Replaces_The_Pending_Replacement()
    {
        var session = Session();
        var id = Guid.NewGuid();
        TextBlockSessionService.Commit(session, 1, null, id, Block("one"));
        TextBlockSessionService.Commit(session, 1, null, id, Block("two"));

        var replacement = Assert.IsType<TextBlockReplacement>(Assert.Single(Overlays(session)));
        Assert.Equal("two", replacement.NewBlock!.Paragraphs[0].Text);

        session.Undo();
        Assert.Equal("one", Assert.IsType<TextBlockReplacement>(Assert.Single(Overlays(session))).NewBlock!.Paragraphs[0].Text);
    }
}
