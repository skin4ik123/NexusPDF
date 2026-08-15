using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Детект активного содержимого: JavaScript, вложения, Launch-действия.
/// Программа их НЕ выполняет, но обязана честно показать пользователю.
/// </summary>
public sealed class ActiveContentTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string WriteRaw(string name, string body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(body));
        return path;
    }

    [Fact]
    public async Task JavaScript_And_Attachment_Are_Detected()
    {
        var js = "app.alert('hi');";
        var payload = "attached payload";
        var raw = "%PDF-1.7\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names << " +
                  "/JavaScript << /Names [(AutoRun) 4 0 R] >> " +
                  "/EmbeddedFiles << /Names [(secret.txt) 6 0 R] >> >> >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
                  $"4 0 obj\n<< /S /JavaScript /JS 5 0 R >>\nendobj\n" +
                  $"5 0 obj\n<< /Length {js.Length} >>\nstream\n{js}\nendstream\nendobj\n" +
                  "6 0 obj\n<< /Type /Filespec /F (secret.txt) /UF (secret.txt) /EF << /F 7 0 R >> >>\nendobj\n" +
                  $"7 0 obj\n<< /Type /EmbeddedFile /Length {payload.Length} >>\nstream\n{payload}\nendstream\nendobj\n" +
                  "trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var path = WriteRaw("active.pdf", raw);

        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var active = await handle.GetActiveContentAsync(CancellationToken.None);

        Assert.True(active.HasAny);
        Assert.Equal(1, active.JavaScriptCount);
        Assert.Contains("AutoRun", active.JavaScriptNames);
        Assert.Equal(1, active.AttachmentCount);
        Assert.Contains("secret.txt", active.AttachmentNames);

        // Имя скрипта показываем, ТЕЛО скрипта наружу не отдаём.
        Assert.DoesNotContain(active.JavaScriptNames, n => n.Contains("app.alert"));
    }

    [Fact]
    public async Task Launch_Action_Is_Counted()
    {
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>\nendobj\n" +
                  "4 0 obj\n<< /Type /Annot /Subtype /Link /Rect [100 600 300 640] /A 5 0 R >>\nendobj\n" +
                  "5 0 obj\n<< /Type /Action /S /Launch /F (calc.exe) >>\nendobj\n" +
                  "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var path = WriteRaw("launch.pdf", raw);

        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var active = await handle.GetActiveContentAsync(CancellationToken.None);
        Assert.True(active.LaunchActionCount >= 1, "Launch-действие должно быть замечено");
        Assert.True(active.HasAny);

        // Главное: такая ссылка НЕ активируется — движок не отдаёт действие,
        // а программа принципиально не запускает внешние программы.
        var link = await handle.GetLinkAtAsync(0, 0, 200, 172, CancellationToken.None);
        Assert.Null(link);
    }

    [Fact]
    public async Task Ordinary_Document_Has_No_Active_Content()
    {
        var path = PdfFixture.WriteToTemp("plain.pdf", new PdfFixture.PageSpec(612, 792));
        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var active = await handle.GetActiveContentAsync(CancellationToken.None);
        Assert.False(active.HasAny);
        Assert.Equal(0, active.JavaScriptCount);
        Assert.Equal(0, active.AttachmentCount);
        Assert.Equal(0, active.LaunchActionCount);
    }
}
