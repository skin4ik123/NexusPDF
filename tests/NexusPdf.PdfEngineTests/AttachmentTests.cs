using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Вложенные файлы: список и извлечение содержимого.</summary>
public sealed class AttachmentTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Fact]
    public async Task Attachment_Is_Listed_And_Extracted_Byte_For_Byte()
    {
        const string content = "NEXUSPDF ATTACHMENT PAYLOAD 12345";
        var path = PdfFixture.WriteAttachmentToTemp("with-attachment.pdf", "notes.txt", content);
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);

        var attachments = await doc.GetAttachmentsAsync(CancellationToken.None);

        var one = Assert.Single(attachments);
        Assert.Equal("notes.txt", one.Name);
        Assert.Equal(content.Length, one.SizeBytes);

        var bytes = await doc.ReadAttachmentAsync(one.Index, CancellationToken.None);
        Assert.Equal(content, System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public async Task Document_Without_Attachments_Reports_None()
    {
        var path = PdfFixture.WriteToTemp("plain.pdf", new PdfFixture.PageSpec(300, 300));
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);

        Assert.Empty(await doc.GetAttachmentsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PdfEngineException>(
            () => doc.ReadAttachmentAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Active_Content_Warning_Sees_The_Same_Attachment()
    {
        var path = PdfFixture.WriteAttachmentToTemp("a.pdf", "payload.bin", "data");
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);

        var active = await doc.GetActiveContentAsync(CancellationToken.None);
        Assert.True(active.HasAny);
        Assert.Equal(1, active.AttachmentCount);
        Assert.Contains("payload.bin", active.AttachmentNames);
    }
}
