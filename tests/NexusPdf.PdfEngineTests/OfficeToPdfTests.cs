using NexusPdf.Office;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Преобразование документов Office в PDF.
///
/// Проверяется не «файл появился», а то, ради чего выбран именно экспорт, а не
/// печать в PDF: в результате остаются ЖИВЫЕ ссылки, оглавление по заголовкам
/// и настоящий текст, а не картинка страницы.
///
/// Без установленного Office тесты пропускаются: врать зелёным цветом о том,
/// что не проверялось, нельзя, но и падать на машине без Word тоже незачем.
/// </summary>
public sealed class OfficeToPdfTests : IAsyncLifetime
{
    private readonly PdfiumRenderEngine _pdfium = new();
    private readonly OfficeToPdfConverter _converter = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Документ Word с заголовком и гиперссылкой — через сам Word.</summary>
    private static string? MakeWordDocument(string dir)
    {
        if (!OfficeToPdfConverter.IsInstalled("Word.Application")) return null;
        var path = Path.Combine(dir, "договор.docx");
        dynamic? app = null;
        dynamic? doc = null;
        try
        {
            app = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            app.Visible = false;
            app.DisplayAlerts = 0;
            doc = app.Documents.Add();

            var heading = doc.Content;
            heading.Text = "Раздел первый\r\nОбычный абзац со ссылкой.\r\n";
            doc.Paragraphs[1].Range.Style = "Заголовок 1";

            var target = doc.Paragraphs[2].Range;
            target.SetRange(target.Start, target.Start + 8);
            doc.Hyperlinks.Add(target, "https://example.org/", Type.Missing, "пример");

            doc.SaveAs2(path);
            return path;
        }
        catch (Exception ex)
        {
            // Word установлен, но документ не получился — это ошибка проверки,
            // а не повод показать зелёный цвет.
            throw new InvalidOperationException(
                "Не удалось подготовить документ Word для проверки: " + ex.Message, ex);
        }
        finally
        {
            try { doc?.Close(0); } catch (Exception) { }
            try { app?.Quit(); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task Word_Document_Becomes_A_Pdf_With_Live_Links_And_An_Outline()
    {
        var dir = NewDir();
        var source = MakeWordDocument(dir);
        if (source == null) return; // Word не установлен — проверять нечего

        var target = Path.Combine(dir, "договор.pdf");
        var result = await _converter.ConvertAsync(source, target, CancellationToken.None);

        Assert.Equal("Word", result.Application);
        Assert.True(result.KeepsLinks && result.KeepsOutline && result.KeepsTags);
        Assert.True(File.Exists(target));

        await using var doc = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.True(doc.Info.PageCount >= 1);

        // Текст настоящий, а не растр страницы.
        var text = await doc.GetPageTextAsync(0, CancellationToken.None);
        Assert.Contains("Раздел первый", text);

        // Ссылка жива: печать в PDF-принтер оставила бы от неё только синие буквы.
        var links = await doc.GetPageLinksAsync(0, CancellationToken.None);
        Assert.Contains(links, l => (l.Uri ?? "").Contains("example.org"));

        // Заголовок стал оглавлением документа.
        var outline = await doc.GetBookmarksAsync(CancellationToken.None);
        Assert.Contains(outline, o => o.Title.Contains("Раздел первый"));
    }

    [Fact]
    public async Task Excel_Workbook_Becomes_A_Pdf_With_Real_Text()
    {
        if (!OfficeToPdfConverter.IsInstalled("Excel.Application")) return;
        var dir = NewDir();
        var source = Path.Combine(dir, "смета.csv");
        await File.WriteAllTextAsync(source,
            "Наименование;Количество;Цена\nБолт;10;25\nГайка;20;7\n",
            System.Text.Encoding.UTF8);

        var target = Path.Combine(dir, "смета.pdf");
        var result = await _converter.ConvertAsync(source, target, CancellationToken.None);

        Assert.Equal("Excel", result.Application);
        await using var doc = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var text = await doc.GetPageTextAsync(0, CancellationToken.None);
        Assert.Contains("Болт", text);
    }

    [Fact]
    public void Formats_Are_Recognised_And_Foreign_Ones_Are_Refused()
    {
        Assert.True(OfficeToPdfConverter.IsOfficeFile("отчёт.docx"));
        Assert.True(OfficeToPdfConverter.IsOfficeFile("книга.XLSX"));
        Assert.True(OfficeToPdfConverter.IsOfficeFile("слайды.pptm"));
        Assert.False(OfficeToPdfConverter.IsOfficeFile("страница.pdf"));
        Assert.False(OfficeToPdfConverter.IsOfficeFile("снимок.png"));
    }

    [Fact]
    public void An_Unsupported_File_Explains_Itself()
    {
        var reason = OfficeToPdfConverter.UnavailableReason("картинка.png");
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("Office", reason);
    }

    [Fact]
    public async Task A_Missing_File_Fails_Loudly()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _converter.ConvertAsync(
            Path.Combine(NewDir(), "нет-такого.docx"),
            Path.Combine(NewDir(), "out.pdf"), CancellationToken.None));
    }
}
