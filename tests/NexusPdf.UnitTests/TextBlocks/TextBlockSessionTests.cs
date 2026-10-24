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

    [Fact]
    public void Deleting_A_Moved_Saved_Block_Keeps_It_Deleted()
    {
        // Сохранённый блок сдвинули (появилась замена), потом нажали Delete:
        // удаление обязано стать заменой «без новой версии», а не снять замену
        // — иначе блок вернулся бы на прежнее место.
        var session = Session();
        var id = Guid.NewGuid();
        TextBlockSessionService.Commit(session, 1, null, id, Block("moved"));

        TextBlockSessionService.Delete(session, 1, 0);

        var replacement = Assert.IsType<TextBlockReplacement>(Assert.Single(Overlays(session)));
        Assert.Equal(id, replacement.BlockId);
        Assert.Null(replacement.NewBlock);
    }

    [Fact]
    public void Deleting_Any_Other_Overlay_Removes_It()
    {
        var session = Session();
        TextBlockSessionService.Commit(session, 1, null, null, Block("draft"));
        TextBlockSessionService.Delete(session, 1, 0);
        Assert.Empty(Overlays(session));
    }

    [Fact]
    public void Commit_After_The_Draft_Vanished_Adds_The_Text_Instead_Of_Failing()
    {
        var session = Session();
        TextBlockSessionService.Commit(session, 1, null, null, Block("first"));
        var draft = (TextBlockOverlay)Overlays(session)[0];
        session.Undo();   // черновик исчез, пока шёл ввод

        TextBlockSessionService.Commit(session, 1, draft, null, Block("typed"));

        Assert.Equal("typed", Assert.IsType<TextBlockOverlay>(Assert.Single(Overlays(session))).Paragraphs[0].Text);
    }

    [Fact]
    public void Commit_On_A_Page_That_Is_Gone_Does_Not_Throw()
    {
        var session = Session();
        TextBlockSessionService.Commit(session, 7, null, null, Block("lost"));
        Assert.False(session.History.CanUndo);
    }

    [Fact]
    public void Session_Announces_A_Change_Before_Making_It()
    {
        var session = Session();
        var seen = new List<int>();
        session.Changing += (_, _) => seen.Add(session.Model.Pages[1].OverlayList.Count);

        session.Apply(new AddOverlayOperation(1, Block("a")));
        session.Undo();
        session.Redo();

        Assert.Equal([0, 1, 0], seen);
    }

    private static DetectedParagraph Paragraph(params (int Path, string Fingerprint)[] objects) =>
        new(objects.Select(o => new PageTextObject([o.Path], "old", new OverlayBox(10, 10, 50, 12), 10, 20, 0, 11, "Arial",
                false, false, 0xFF000000, false, false, false, [5.5, 5.5, 5.5], o.Fingerprint)).ToList(),
            [new PageLineObject([9], new OverlayBox(10, 22, 50, 0.6), 0xFF000000, "line-print")],
            new OverlayBox(10, 10, 50, 12), Block("old"), null, []);

    [Fact]
    public void Editing_A_Paragraph_Adds_A_Replacement_Of_Its_Objects()
    {
        var session = Session();
        TextBlockSessionService.CommitParagraph(session, 1, Paragraph((0, "a"), (3, "b")), Block("new"));

        var replacement = Assert.IsType<TextParagraphReplacement>(Assert.Single(Overlays(session)));
        Assert.Equal([[0], [3], [9]], replacement.Objects.Select(o => o.Path.ToArray()));
        Assert.Equal(["a", "b", "line-print"], replacement.Objects.Select(o => o.Fingerprint));
        Assert.Equal("new", replacement.NewBlock!.Paragraphs[0].Text);
        Assert.NotEqual(Guid.Empty, replacement.NewBlock.BlockId);
        session.Undo();
        Assert.Empty(Overlays(session));
    }

    [Fact]
    public void Second_Edit_Of_A_Paragraph_Replaces_The_Pending_Replacement()
    {
        var session = Session();
        var paragraph = Paragraph((0, "a"));
        TextBlockSessionService.CommitParagraph(session, 1, paragraph, Block("first"));
        var first = Assert.IsType<TextParagraphReplacement>(Assert.Single(Overlays(session)));

        TextBlockSessionService.CommitParagraph(session, 1, first.Objects, Block("second"));

        var second = Assert.IsType<TextParagraphReplacement>(Assert.Single(Overlays(session)));
        Assert.Equal("second", second.NewBlock!.Paragraphs[0].Text);
        Assert.Equal(first.NewBlock!.BlockId, second.NewBlock.BlockId);
    }

    [Fact]
    public void Deleting_A_Paragraph_Writes_A_Replacement_Without_A_Block()
    {
        var session = Session();
        TextBlockSessionService.CommitParagraph(session, 1, Paragraph((0, "a")), Block(""));

        var replacement = Assert.IsType<TextParagraphReplacement>(Assert.Single(Overlays(session)));
        Assert.Null(replacement.NewBlock);
    }

    [Fact]
    public void Deleting_A_Pending_Paragraph_Replacement_Keeps_The_Old_Text_Removed()
    {
        var session = Session();
        TextBlockSessionService.CommitParagraph(session, 1, Paragraph((0, "a")), Block("new"));

        TextBlockSessionService.Delete(session, 1, 0);

        var replacement = Assert.IsType<TextParagraphReplacement>(Assert.Single(Overlays(session)));
        Assert.Null(replacement.NewBlock);
    }
}
