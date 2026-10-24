using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Signing;
using NexusPdf.TestSupport;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Абзац чужого текста после правки: его объекты (и линии подчёркивания)
/// снимаются со страницы, на их место ложится наш блок — после сохранения он
/// правится как свой. Снимаются ровно те объекты, что видел пользователь:
/// если на их месте уже другое, запись отказывает, а не портит страницу.
/// </summary>
public sealed class ParagraphReplacementTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "paragraph-" + Guid.NewGuid().ToString("N"));
    private FontCatalog _fonts = null!;
    private PdfiumRenderEngine _pdfium = null!;

    private const string ThreeParagraphs =
        "BT /F1 12 Tf 72 700 Td (First paragraph line one) Tj ET " +
        "BT /F1 12 Tf 72 686 Td (and line two of it) Tj ET " +
        "BT /F1 12 Tf 72 640 Td (Middle paragraph text here) Tj ET " +
        "BT /F1 12 Tf 72 626 Td (with its second line) Tj ET " +
        "72 623 100 0.6 re f " +
        "BT /F1 12 Tf 72 580 Td (Last paragraph stays) Tj ET";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _fonts = FontCatalog.Scan([TestFontPaths.Directory]);
        _pdfium = new PdfiumRenderEngine(null, _fonts);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pdfium.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Source(string content, int rotate = 0)
    {
        var path = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792, rotate, RawContent: content)));
        return path;
    }

    private async Task<IReadOnlyList<DetectedParagraph>> ParagraphsAsync(string path)
    {
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var layout = await doc.GetPageTextLayoutAsync(0, 0, CancellationToken.None);
        return ParagraphBuilder.Build(layout, FormTargetIndex.Build(layout, 11), _fonts);
    }

    private async Task<DetectedParagraph> MiddleAsync(string path) =>
        (await ParagraphsAsync(path)).Single(p => p.Objects.Any(o => o.Text.StartsWith("Middle", StringComparison.Ordinal)));

    private TextParagraphReplacement Replace(DetectedParagraph paragraph, string? text) =>
        new(Refs(paragraph), text == null ? null : Edited(paragraph.Block!, text));

    /// <summary>
    /// Новый текст — через модель редактора, как в программе: она пересчитывает
    /// рамку под новый текст, удерживая на месте видимый левый верхний угол.
    /// </summary>
    private TextBlockOverlay Edited(TextBlockOverlay block, string text) =>
        new TextBlockEditorModel(WithText(block, text), _fonts, block.Paragraphs[0].Runs[0].Style).Block;

    /// <summary>Текст страницы без различия пробелов и переводов строк (замена переносит по-своему).</summary>
    private static string Flat(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private static IReadOnlyList<ParagraphObjectRef> Refs(DetectedParagraph paragraph) =>
        paragraph.Objects.Select(o => new ParagraphObjectRef(o.Path, o.Fingerprint))
            .Concat(paragraph.Decorations.Select(d => new ParagraphObjectRef(d.Path, d.Fingerprint))).ToList();

    private static TextBlockOverlay WithText(TextBlockOverlay block, string text) =>
        block with { Paragraphs = [block.Paragraphs[0] with { Runs = [block.Paragraphs[0].Runs[0] with { Text = text }] }] };

    private async Task<string> ComposeAsync(string source, params PageOverlay[] overlays)
    {
        var target = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N") + ".pdf");
        await using var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        await _pdfium.ComposeAsync([new ComposedPage(doc, 0, 0, overlays)], target, CancellationToken.None);
        return target;
    }

    private async Task<string> PageTextAsync(string path)
    {
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        return await doc.GetPageTextAsync(0, CancellationToken.None);
    }

    private async Task<PageTextLayout> LayoutAsync(string path)
    {
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        return await doc.GetPageTextLayoutAsync(0, 0, CancellationToken.None);
    }

    [Fact]
    public async Task Middle_Paragraph_Is_Replaced_And_The_Others_Stay()
    {
        var source = Source(ThreeParagraphs);
        var paragraphs = await ParagraphsAsync(source);
        Assert.Equal(3, paragraphs.Count);
        var middle = await MiddleAsync(source);
        Assert.Single(middle.Decorations);

        var saved = await ComposeAsync(source, Replace(middle, "Replaced middle text"));

        var text = Flat(await PageTextAsync(saved));
        Assert.Contains("First paragraph line one", text);
        Assert.Contains("and line two of it", text);
        Assert.Contains("Last paragraph stays", text);
        Assert.Contains("Replaced middle text", text);
        Assert.DoesNotContain("Middle paragraph", text);
        Assert.DoesNotContain("second line", text);
        await using var doc = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        var stored = Assert.Single(await doc.GetTextBlocksAsync(0, 0, CancellationToken.None));
        Assert.True(stored.Intact);
        Assert.Equal("Replaced middle text", stored.Block!.Paragraphs[0].Text);
        // Подчёркивание снято вместе с абзацем: тонких линий на странице не осталось.
        Assert.Empty((await LayoutAsync(saved)).Lines);
    }

    [Fact]
    public async Task Deleted_Paragraph_Leaves_Nothing()
    {
        var source = Source(ThreeParagraphs);
        var saved = await ComposeAsync(source, Replace(await MiddleAsync(source), null));

        var text = await PageTextAsync(saved);
        Assert.DoesNotContain("Middle paragraph", text);
        Assert.Contains("Last paragraph stays", text);
    }

    [Fact]
    public async Task Paragraph_On_A_Turned_Page_Lands_In_Place()
    {
        // Страница с /Rotate 90: на экране текст идёт сверху вниз.
        var source = Source("BT /F1 12 Tf 72 700 Td (Turned text that wraps) Tj ET BT /F1 12 Tf 72 686 Td (on two lines) Tj ET", 90);
        var paragraph = Assert.Single(await ParagraphsAsync(source));
        Assert.Equal(90, paragraph.Block!.RotationDegrees, 1);
        var before = (await LayoutAsync(source)).Objects[0];

        var saved = await ComposeAsync(source, Replace(paragraph, "Turned text that wraps"));

        var after = (await LayoutAsync(saved)).Objects.OrderBy(o => o.OriginXPt).Last();
        Assert.Equal(90, after.AngleDeg, 1);
        Assert.InRange(after.OriginXPt, before.OriginXPt - 1.5, before.OriginXPt + 1.5);
        Assert.InRange(after.OriginYPt, before.OriginYPt - 1.5, before.OriginYPt + 1.5);
    }

    [Fact]
    public async Task Signed_Document_Is_Appended_And_Keeps_Its_Signature()
    {
        var source = Source(ThreeParagraphs);
        var signed = Path.Combine(_dir, "signed.pdf");
        using (var certificate = MakeSelfSigned())
            PdfIncrementalSigner.Sign(source, signed, certificate, "", "");
        var middle = await MiddleAsync(signed);
        var result = Path.Combine(_dir, "signed-out.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, signed, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, Replace(middle, "Appended text")));
            var save = await new SaveService(_pdfium).SaveAsAsync(document, result, keepBackup: false, CancellationToken.None);
            Assert.True(save.SignaturesPreserved);
        }

        var text = Flat(await PageTextAsync(result));
        Assert.Contains("Appended text", text);
        Assert.DoesNotContain("Middle paragraph", text);
    }

    [Fact]
    public async Task Erase_Elsewhere_Drops_The_Old_Paragraph_From_The_Raster()
    {
        var source = Source(ThreeParagraphs);
        var middle = await MiddleAsync(source);
        var result = Path.Combine(_dir, "erased.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, Replace(middle, null)));
            document.Session.Apply(new AddOverlayOperation(0, new RegionEraseDraft(400, 700, 100, 40, 0xFFFFFFFF)));
            await new SaveService(_pdfium).SaveAsAsync(document, result, keepBackup: false, CancellationToken.None);
        }

        // Там, где был абзац, — бумага (y 152…172 сверху — строки 640 и 626 от низа).
        await using var doc = await _pdfium.OpenAsync(result, null, CancellationToken.None);
        var render = await doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
        var inked = 0;
        for (var y = 145; y < 172; y++)
        for (var x = 72; x < 250; x++)
        {
            var at = (long)y * render.Stride + x * 4;
            if (Math.Min(render.Bgra[at], Math.Min(render.Bgra[at + 1], render.Bgra[at + 2])) < 200) inked++;
        }
        Assert.Equal(0, inked);
    }

    [Fact]
    public async Task Changed_Object_Is_Refused_With_A_Clear_Error()
    {
        var middle = await MiddleAsync(Source(ThreeParagraphs));
        var changed = Source(ThreeParagraphs.Replace("Middle paragraph text here", "Middle paragraph TEXT here"));

        var error = await Assert.ThrowsAsync<PdfEngineException>(() => ComposeAsync(changed, Replace(middle, "New")));

        Assert.Contains("изменил", error.Message);
    }

    [Fact]
    public async Task Edited_Paragraph_Under_A_Later_Redaction_Is_Burned_Into_The_Raster()
    {
        // Сначала абзац переписан, потом поверх него — вымарка: новый текст
        // обязан уйти под чёрное вместе со страницей, а не лечь поверх растра.
        var source = Source(ThreeParagraphs);
        var middle = await MiddleAsync(source);
        var result = Path.Combine(_dir, "redacted.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, Replace(middle, "Hidden secret words")));
            document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(60, 140, 300, 40)));
            await new SaveService(_pdfium).SaveAsAsync(document, result, keepBackup: false, CancellationToken.None);
        }

        Assert.DoesNotContain("Hidden", Flat(await PageTextAsync(result)));
    }

    [Fact]
    public async Task Block_Under_A_Later_Redaction_Is_Burned_But_A_Block_Added_After_It_Stays_Text()
    {
        var source = Source("BT /F1 12 Tf 72 700 Td (Page text) Tj ET");
        var style = new TextStyle("Liberation Sans", 12);
        TextBlockOverlay Block(string text, double y) => new TextBlockEditorModel(
            new TextBlockOverlay(100, y, 10, 10, 0, TextBlockWidthMode.AutoWidth, true, false,
                [new TextParagraph([new TextRun(text, style)])], Guid.NewGuid()), _fonts, style).Block;
        var result = Path.Combine(_dir, "redacted-block.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, Block("Underneath secret", 300)));
            document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(80, 290, 300, 40)));
            document.Session.Apply(new AddOverlayOperation(0, Block("Written on top", 300)));
            await new SaveService(_pdfium).SaveAsAsync(document, result, keepBackup: false, CancellationToken.None);
        }

        var text = Flat(await PageTextAsync(result));
        Assert.DoesNotContain("Underneath", text);
        Assert.Contains("Written on top", text);
    }

    [Fact]
    public async Task Any_Edit_Added_Before_A_Redaction_Is_Burned_Even_Far_From_It()
    {
        // Вымарка не полагается на оценку рамок: всё добавленное до неё уходит в растр.
        var source = Source("BT /F1 12 Tf 72 700 Td (Page text) Tj ET");
        var result = Path.Combine(_dir, "redacted-far.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("Far away words", 400, 700, 12, 0xFF000000, 0, "Liberation Sans", false, false)));
            document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(60, 60, 100, 40)));
            await new SaveService(_pdfium).SaveAsAsync(document, result, keepBackup: false, CancellationToken.None);
        }

        Assert.DoesNotContain("Far away", Flat(await PageTextAsync(result)));
    }

    [Fact]
    public async Task Line_Edit_Below_A_Replaced_Paragraph_Still_Hits_Its_Own_Line()
    {
        // Снятие абзаца не должно сдвигать номера, по которым правятся строки.
        var source = Source(ThreeParagraphs);
        var middle = await MiddleAsync(source);
        var last = (await LayoutAsync(source)).Objects.Single(o => o.Text.StartsWith("Last", StringComparison.Ordinal));

        var saved = await ComposeAsync(source, Replace(middle, "Replaced middle text"),
            new TextObjectReplacement(last.Path, "Last paragraph CHANGED"));

        var text = Flat(await PageTextAsync(saved));
        Assert.Contains("Last paragraph CHANGED", text);
        Assert.Contains("First paragraph line one", text);
        Assert.Contains("and line two of it", text);
        Assert.Contains("Replaced middle text", text);
    }

    [Fact]
    public async Task Line_Edit_Of_A_Line_Being_Replaced_As_A_Paragraph_Is_Refused()
    {
        var source = Source(ThreeParagraphs);
        var middle = await MiddleAsync(source);

        var error = await Assert.ThrowsAsync<PdfEngineException>(() => ComposeAsync(source,
            Replace(middle, "Replaced middle text"), new TextObjectReplacement(middle.Objects[0].Path, "Other")));

        Assert.Contains("абзац", error.Message);
    }

    [Fact]
    public async Task Paragraph_Edited_After_A_Redaction_Over_It_Is_Burned_Too()
    {
        // Вымарка раньше правки, но новая версия абзаца лежит под ней: её текст
        // (с вымаранными словами) не должен остаться в файле.
        var source = Source(ThreeParagraphs);
        var middle = await MiddleAsync(source);
        var result = Path.Combine(_dir, "redacted-then-edited.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, source, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, new RedactionDraft(60, 140, 300, 40)));
            document.Session.Apply(new AddOverlayOperation(0, Replace(middle, "Middle paragraph with secret")));
            await new SaveService(_pdfium).SaveAsAsync(document, result, keepBackup: false, CancellationToken.None);
        }

        Assert.DoesNotContain("secret", Flat(await PageTextAsync(result)));
    }

    private static X509Certificate2 MakeSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Paragraph Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }
}
