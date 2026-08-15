using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Разметка выделенного текста: маркер, подчёркивание, зачёркивание.
/// Проверяется главное — что это НАСТОЯЩАЯ текстовая аннотация PDF нужного
/// подтипа, что она видна на странице и что текст под ней остаётся текстом.
/// </summary>
public sealed class TextMarkupTests : IAsyncLifetime
{
    private const int SubtypeHighlight = 9;
    private const int SubtypeUnderline = 10;
    private const int SubtypeStrikeOut = 12;

    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<string> ComposeAsync(string dir, PageOverlay overlay, string text = "MARKEDTEXT")
    {
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: text)));
        var target = Path.Combine(dir, "markup.pdf");

        await using var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        await _pdfium.ComposeAsync(
            new[] { new ComposedPage(handle, 0, 0, new[] { overlay }) },
            target, CancellationToken.None);
        return target;
    }

    /// <summary>Доля НЕбелых пикселей в полосе — так видно, легла ли разметка.</summary>
    private static double PaintedFraction(RenderedPageImage image, int rowFrom, int rowTo)
    {
        var painted = 0;
        var total = 0;
        for (var y = Math.Max(0, rowFrom); y < Math.Min(image.PixelHeight, rowTo); y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var o = y * image.Stride + x * 4;
                total++;
                if (image.Bgra[o] < 245 || image.Bgra[o + 1] < 245 || image.Bgra[o + 2] < 245)
                    painted++;
            }
        }
        return total == 0 ? 0 : (double)painted / total;
    }

    [Theory]
    [InlineData(TextMarkupKind.Highlight, SubtypeHighlight)]
    [InlineData(TextMarkupKind.Underline, SubtypeUnderline)]
    [InlineData(TextMarkupKind.StrikeOut, SubtypeStrikeOut)]
    public async Task Markup_Is_Saved_As_A_Real_Text_Annotation(TextMarkupKind kind, int expectedSubtype)
    {
        var dir = NewDir();
        var target = await ComposeAsync(dir, new TextMarkupDraft(
            kind,
            new[] { new TextMarkupRect(100, 300, 220, 14) },
            0x66FDE047, "", "Тест"));

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);

        // Подтип обязан быть именно текстовой разметкой: прямоугольник поверх
        // строки другие программы не покажут в списке комментариев и не дадут
        // снять как разметку.
        var annotations = await result.GetAnnotationsAsync(0, CancellationToken.None);
        Assert.Contains(annotations, a => a.Subtype == expectedSubtype);

        // Текст под разметкой остаётся текстом — она не растеризует страницу.
        Assert.Contains("MARKEDTEXT", await result.GetPageTextAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Highlight_Is_Actually_Visible_On_The_Page()
    {
        var dir = NewDir();
        // Страница без текста: закрашенные пиксели могут появиться только от
        // самой разметки, иначе измерение ничего не доказывает.
        var target = await ComposeAsync(dir, new TextMarkupDraft(
            TextMarkupKind.Highlight,
            new[] { new TextMarkupRect(100, 300, 400, 16) },
            0x66FDE047, "", "Тест"), text: "");

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var image = await result.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);

        var onMarkup = PaintedFraction(image, 302, 314);
        var elsewhere = PaintedFraction(image, 400, 440);
        Assert.True(onMarkup > 0.5, $"маркер должен закрашивать строку, а закрашено {onMarkup:P1}");
        Assert.True(elsewhere < 0.01, $"вне разметки страница должна остаться чистой, закрашено {elsewhere:P1}");
    }

    [Fact]
    public async Task Multi_Line_Selection_Keeps_One_Annotation_With_A_Quad_Per_Line()
    {
        var dir = NewDir();
        var target = await ComposeAsync(dir, new TextMarkupDraft(
            TextMarkupKind.Highlight,
            new[]
            {
                new TextMarkupRect(100, 300, 400, 14),
                new TextMarkupRect(100, 320, 400, 14),
                new TextMarkupRect(100, 340, 180, 14),
            },
            0x66FDE047, "", "Тест"), text: "");

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        var annotations = await result.GetAnnotationsAsync(0, CancellationToken.None);

        // Одно выделение — одна аннотация, а не три отдельных комментария.
        Assert.Single(annotations, a => a.Subtype == SubtypeHighlight);

        var image = await result.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
        // Закрашены все три строки, и промежутки между ними — нет: разметка
        // идёт по строкам, а не одним блоком через весь абзац.
        Assert.True(PaintedFraction(image, 302, 312) > 0.5);
        Assert.True(PaintedFraction(image, 322, 332) > 0.5);
        Assert.True(PaintedFraction(image, 342, 352) > 0.2);
        Assert.True(PaintedFraction(image, 316, 318) < 0.05,
            "промежуток между строками закрашиваться не должен");
    }

    [Fact]
    public async Task Markup_Follows_Page_Rotation_Applied_After_It_Was_Placed()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "src.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, Text: "")));
        var target = Path.Combine(dir, "rotated-markup.pdf");

        await using (var handle = await _pdfium.OpenAsync(source, null, CancellationToken.None))
        {
            await _pdfium.ComposeAsync(
                new[]
                {
                    new ComposedPage(handle, 0, 1, new PageOverlay[]
                    {
                        new TextMarkupDraft(
                            TextMarkupKind.Highlight,
                            new[] { new TextMarkupRect(100, 300, 400, 16) },
                            0x66FDE047, "", "Тест")
                        { PlacedRotation = 0 },
                    }),
                },
                target, CancellationToken.None);
        }

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(792, result.Info.Pages[0].WidthPoints, 1);

        // Горизонтальная полоса обязана стать вертикальной вместе со страницей.
        var image = await result.RenderPageAsync(0, 792, 612, 0, CancellationToken.None);
        var acrossOldPlace = PaintedFraction(image, 302, 314);
        Assert.True(acrossOldPlace < 0.2,
            $"разметка не должна остаться в старой рамке, там закрашено {acrossOldPlace:P1}");

        var painted = 0;
        for (var y = 0; y < image.PixelHeight; y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var o = y * image.Stride + x * 4;
                if (image.Bgra[o] < 245 || image.Bgra[o + 1] < 245 || image.Bgra[o + 2] < 245)
                    painted++;
            }
        }
        Assert.True(painted > 1000, "разметка должна остаться на странице после поворота");
    }

    [Fact]
    public async Task Empty_Rect_List_Adds_Nothing_Instead_Of_Breaking_The_File()
    {
        var dir = NewDir();
        var target = await ComposeAsync(dir, new TextMarkupDraft(
            TextMarkupKind.Underline, Array.Empty<TextMarkupRect>(), 0xFF2563EB, "", ""));

        await using var result = await _pdfium.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(1, result.Info.PageCount);
        Assert.DoesNotContain(
            await result.GetAnnotationsAsync(0, CancellationToken.None),
            a => a.Subtype == SubtypeUnderline);
    }
}
