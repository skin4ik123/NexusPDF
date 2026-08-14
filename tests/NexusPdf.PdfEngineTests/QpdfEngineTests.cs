using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Интеграционные тесты qpdf. Если qpdf.exe не установлен (tools/qpdf),
/// тесты тихо пропускают проверку — движок опционален by design.
/// </summary>
public sealed class QpdfEngineTests : IAsyncLifetime
{
    private readonly QpdfEngine _qpdf = new();
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Fact]
    public async Task Check_Passes_On_Valid_File()
    {
        if (!_qpdf.IsAvailable) return;
        var path = PdfFixture.WriteToTemp("valid.pdf", new PdfFixture.PageSpec(612, 792));

        var result = await _qpdf.ValidateAsync(path, null, CancellationToken.None);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
    }

    [Fact]
    public async Task Check_Fails_On_Garbage()
    {
        if (!_qpdf.IsAvailable) return;
        var dir = Path.GetDirectoryName(PdfFixture.WriteToTemp("x.pdf", new PdfFixture.PageSpec(10, 10)))!;
        var path = Path.Combine(dir, "garbage.pdf");
        await File.WriteAllTextAsync(path, "не PDF ни разу");

        var result = await _qpdf.ValidateAsync(path, null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task Encrypt_Requires_Password_In_Pdfium_And_Opens_With_It()
    {
        if (!_qpdf.IsAvailable) return;
        var source = PdfFixture.WriteToTemp("secret.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "top secret"));
        var target = Path.Combine(Path.GetDirectoryName(source)!, "secret-enc.pdf");

        await _qpdf.EncryptAsync(source, target, "hunter2", null, CancellationToken.None);

        await Assert.ThrowsAsync<PdfPasswordRequiredException>(
            () => _pdfium.OpenAsync(target, null, CancellationToken.None));

        await using var doc = await _pdfium.OpenAsync(target, "hunter2", CancellationToken.None);
        Assert.Equal(1, doc.Info.PageCount);
        Assert.Contains("top secret", await doc.GetPageTextAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Decrypt_Removes_Protection()
    {
        if (!_qpdf.IsAvailable) return;
        var source = PdfFixture.WriteToTemp("locked.pdf", new PdfFixture.PageSpec(612, 792));
        var encrypted = Path.Combine(Path.GetDirectoryName(source)!, "locked-enc.pdf");
        var decrypted = Path.Combine(Path.GetDirectoryName(source)!, "locked-dec.pdf");

        await _qpdf.EncryptAsync(source, encrypted, "pw", null, CancellationToken.None);
        await _qpdf.DecryptAsync(encrypted, decrypted, "pw", CancellationToken.None);

        await using var doc = await _pdfium.OpenAsync(decrypted, null, CancellationToken.None);
        Assert.Equal(1, doc.Info.PageCount);
    }

    [Fact]
    public async Task Optimize_Preserves_Content()
    {
        if (!_qpdf.IsAvailable) return;
        var source = PdfFixture.WriteToTemp("fat.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Page one"),
            new PdfFixture.PageSpec(612, 792, Text: "Page two"));
        var target = Path.Combine(Path.GetDirectoryName(source)!, "slim.pdf");

        await _qpdf.OptimizeAsync(source, target, linearize: true, CancellationToken.None);

        await using var doc = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(2, doc.Info.PageCount);
        Assert.Contains("Page two", await doc.GetPageTextAsync(1, CancellationToken.None));
        var check = await _qpdf.ValidateAsync(target, null, CancellationToken.None);
        Assert.True(check.IsValid, string.Join("; ", check.Problems));
    }
}
