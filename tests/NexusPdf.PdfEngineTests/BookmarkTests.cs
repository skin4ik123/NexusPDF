using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Чтение оглавления: дерево, вложенность и оба способа задать цель.</summary>
public sealed class BookmarkTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Fact]
    public async Task Outline_Tree_Is_Read_With_Nesting_And_Targets()
    {
        var path = PdfFixture.WriteOutlineToTemp("outline.pdf");
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);

        var tree = await doc.GetBookmarksAsync(CancellationToken.None);

        Assert.Equal(2, tree.Count);
        Assert.Equal("Chapter One", tree[0].Title);
        Assert.Equal(0, tree[0].TargetPageIndex);       // прямой /Dest
        Assert.Equal("Chapter Two", tree[1].Title);
        Assert.Equal(2, tree[1].TargetPageIndex);       // действие /GoTo
        Assert.Empty(tree[1].Children);

        var nested = Assert.Single(tree[0].Children);
        Assert.Equal("Section 1.1", nested.Title);
        Assert.Equal(1, nested.TargetPageIndex);
        Assert.Empty(nested.Children);
    }

    [Fact]
    public async Task Document_Without_Outline_Returns_Empty_List()
    {
        var path = PdfFixture.WriteToTemp("plain.pdf", new PdfFixture.PageSpec(612, 792));
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);

        Assert.Empty(await doc.GetBookmarksAsync(CancellationToken.None));
    }
}
