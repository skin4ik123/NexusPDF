using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Signing;
using NexusPdf.TestSupport;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Блок пишется в файл обычным текстом страницы — по той же раскладке, что
/// видна в редакторе: каждый кусок строки на своём месте, с запасным шрифтом
/// для недостающих букв, с подчёркиванием и одним встроенным шрифтом на
/// начертание.
/// </summary>
public sealed class TextBlockWriteTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "tb-write-" + Guid.NewGuid().ToString("N"));
    private FontCatalog _fonts = null!;
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var fontDir = Path.Combine(_dir, "fonts");
        Directory.CreateDirectory(fontDir);
        File.WriteAllBytes(Path.Combine(fontDir, "latin.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Latin500")));
        _fonts = FontCatalog.Scan([TestFontPaths.Directory, fontDir]);
        _pdfium = new PdfiumRenderEngine(null, _fonts);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pdfium.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static TextStyle Sans(double size = 20) => new("Liberation Sans", size);

    private static TextBlockOverlay Block(double x, double y, double rotation, params string[] paragraphs) =>
        Block(x, y, rotation, Sans(), paragraphs);

    private static TextBlockOverlay Block(double x, double y, double rotation, TextStyle style, params string[] paragraphs) =>
        new(x, y, 200, 10, rotation, TextBlockWidthMode.Fixed, true, false,
            paragraphs.Select(p => new TextParagraph([new TextRun(p, style)])).ToList(), Guid.NewGuid());

    private string Source(int rotate = 0, string name = "source.pdf", int pages = 1)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, PdfFixture.Build(Enumerable.Range(0, pages)
            .Select(_ => new PdfFixture.PageSpec(400, 300, rotate, Text: "")).ToArray()));
        return path;
    }

    private async Task<string> ComposeAsync(string source, params PageOverlay[][] overlaysByPage)
    {
        var target = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N") + ".pdf");
        await using var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        await _pdfium.ComposeAsync(
            overlaysByPage.Select((overlays, i) => new ComposedPage(doc, i, 0, overlays)).ToList(),
            target, CancellationToken.None);
        return target;
    }

    /// <summary>Точка блока (пункты от левого верхнего угла рамки) в отображаемых координатах страницы.</summary>
    private static (double X, double Y) ToDisplayed(TextBlockOverlay block, LaidOutBlock layout, double lx, double ly)
    {
        var (cx, cy) = (block.XPt + layout.WidthPt / 2, block.YPt + layout.HeightPt / 2);
        var angle = block.RotationDegrees * Math.PI / 180;
        var (dx, dy) = (lx - layout.WidthPt / 2, ly - layout.HeightPt / 2);
        // Поворот по часовой стрелке на экране (ось y вниз), как RotateTransform в WPF.
        return (cx + dx * Math.Cos(angle) - dy * Math.Sin(angle), cy + dx * Math.Sin(angle) + dy * Math.Cos(angle));
    }

    private static (double X, double Y) InkCentre(LaidOutPiece piece) =>
        (piece.XPt + piece.WidthPt / 2, piece.BaselinePt - 0.36 * piece.FontSizePt);

    [Fact]
    public async Task Block_Text_Lands_In_The_File()
    {
        var path = Source();
        var saved = Path.Combine(_dir, "saved.pdf");
        await using (var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, Block(40, 40, 0, "First paragraph", "Second one")));
            await new SaveService(_pdfium).SaveAsAsync(document, saved, keepBackup: false, CancellationToken.None);
        }

        await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        var text = await reopened.GetPageTextAsync(0, CancellationToken.None);
        Assert.Contains("First paragraph", text);
        Assert.Contains("Second one", text);
    }

    [Fact]
    public async Task File_Positions_Match_The_Layout()
    {
        var block = Block(40, 60, 0, "Alpha beta", "Gamma");
        var layout = TextBlockLayout.Compute(block, _fonts);
        var saved = await ComposeAsync(Source(), [block]);

        await using var doc = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        foreach (var piece in layout.Lines.SelectMany(l => l.Pieces).Where(p => p.Text.Trim().Length > 0))
        {
            var (x, y) = InkCentre(piece);
            var found = await doc.GetTextObjectAtAsync(0, 0, block.XPt + x, block.YPt + y, CancellationToken.None);
            Assert.NotNull(found);
            Assert.StartsWith(piece.Text.Trim(), found!.Text.Trim());
            Assert.InRange(found.XPt, block.XPt + piece.XPt - 0.5, block.XPt + piece.XPt + 2);
            Assert.Equal(piece.FontSizePt, found.FontSizePt, 3);
            Assert.True(found.IsEmbeddedFont);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 0)]
    [InlineData(180, 0)]
    [InlineData(270, 0)]
    [InlineData(0, 30)]
    [InlineData(90, 30)]
    [InlineData(180, 30)]
    [InlineData(270, 30)]
    public async Task Rotated_Page_And_Rotated_Block(int pageRotate, double blockRotation)
    {
        // Слово прижато к левому краю широкой рамки: при повороте в другую
        // сторону его центр уехал бы далеко от ожидаемого места.
        var block = Block(60, 50, blockRotation, "Hello");
        var layout = TextBlockLayout.Compute(block, _fonts);
        var saved = await ComposeAsync(Source(pageRotate), [block]);
        var piece = Assert.Single(layout.Lines[0].Pieces);
        var (lx, ly) = InkCentre(piece);
        var (x, y) = ToDisplayed(block, layout, lx, ly);

        await using var doc = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        var found = await doc.GetTextObjectAtAsync(0, 0, x, y, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("Hello", found!.Text);
        Assert.InRange(found.XPt + found.WidthPt / 2, x - 2, x + 2);
        Assert.InRange(found.YPt + found.HeightPt / 2, y - 2, y + 2);
    }

    [Fact]
    public async Task Missing_Glyph_Is_Written_With_Fallback_Font()
    {
        var block = Block(40, 40, 0, new TextStyle("Latin500", 20), "aЖb");
        var layout = TextBlockLayout.Compute(block, _fonts);
        var saved = await ComposeAsync(Source(), [block]);

        await using var doc = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        Assert.Contains("aЖb", (await doc.GetPageTextAsync(0, CancellationToken.None)).Replace(" ", ""));

        var pieces = layout.Lines[0].Pieces;
        var fontsByText = new Dictionary<string, string>();
        foreach (var piece in pieces)
        {
            var (x, y) = InkCentre(piece);
            var found = await doc.GetTextObjectAtAsync(0, 0, block.XPt + x, block.YPt + y, CancellationToken.None);
            Assert.NotNull(found);
            fontsByText[found!.Text] = found.FontName;
        }
        Assert.NotEqual(fontsByText["a"], fontsByText["Ж"]);
    }

    [Fact]
    public async Task Underline_And_Strike_Are_Drawn()
    {
        var style = Sans(40) with { Underline = true, Strike = true };
        var block = Block(20, 40, 0, style, "a      b");
        var layout = TextBlockLayout.Compute(block, _fonts);
        var saved = await ComposeAsync(Source(), [block]);

        var piece = layout.Lines[0].Pieces[0];
        var gapX = block.XPt + piece.XPt + piece.AdvancesPt[0] + piece.AdvancesPt[1] * 3;   // посреди пробелов
        var under = layout.Decorations.MaxBy(d => d.YPt)!;
        var strike = layout.Decorations.MinBy(d => d.YPt)!;

        await using var doc = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        var render = await doc.RenderPageAsync(0, 400, 300, 0, CancellationToken.None);
        byte Pixel(double x, double y) => render.Bgra[(long)(int)y * render.Stride + (int)x * 4];

        Assert.True(Pixel(gapX, block.YPt + under.YPt + under.HeightPt / 2) < 128, "подчёркивания нет под пробелами");
        Assert.True(Pixel(gapX, block.YPt + strike.YPt + strike.HeightPt / 2) < 128, "зачёркивания нет");
        Assert.True(Pixel(gapX, block.YPt + layout.Lines[0].BaselinePt - 2) > 200, "между линиями должно быть пусто");
    }

    [Fact]
    public async Task Font_Is_Embedded_Once_Per_Face()
    {
        PageOverlay[] Blocks(int page) => Enumerable.Range(0, 5)
            .Select(i => (PageOverlay)Block(20, 20 + i * 40, 0, $"Block {page}.{i}")).ToArray();
        var saved = await ComposeAsync(Source(pages: 2), Blocks(0), Blocks(1));

        var raw = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(saved));
        Assert.Equal(1, CountOf(raw, "/FontFile2"));
    }

    [Fact]
    public async Task Signed_Document_Keeps_Signature_With_A_Block()
    {
        // Файл фикстуры уже «нормализован» (классическая xref, словари
        // текстом), поэтому подписывается напрямую, без qpdf.
        var path = PdfFixture.WriteToTemp("tb-sign.pdf", new PdfFixture.PageSpec(612, 792, Text: "Agreement"));
        var signed = Path.Combine(_dir, "signed.pdf");
        using (var certificate = MakeSelfSigned())
            PdfIncrementalSigner.Sign(path, signed, certificate, "", "");
        Assert.True(Assert.Single(await PdfSignatureInspector.InspectAsync(signed, CancellationToken.None)).IsCryptoValid);

        var edited = Path.Combine(_dir, "signed-edited.pdf");
        await using (var document = await OpenedDocument.OpenAsync(_pdfium, signed, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, Block(60, 300, 0, "Added after signing")));
            var result = await new SaveService(_pdfium).SaveAsAsync(document, edited, keepBackup: false, CancellationToken.None);
            Assert.True(result.SignaturesPreserved);
        }

        // Дописывание PDFium повторяет словарь подписи в своём разделе, и
        // проверка видит его дважды (так и с обычной надписью) — важно, что
        // подписанные байты целы и каждая находка проверяется.
        var signatures = await PdfSignatureInspector.InspectAsync(edited, CancellationToken.None);
        Assert.NotEmpty(signatures);
        Assert.All(signatures, s => Assert.True(s.IsCryptoValid, s.Error));
        await using var reopened = await _pdfium.OpenAsync(edited, null, CancellationToken.None);
        Assert.Contains("Added after signing", await reopened.GetPageTextAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Block_Counts_As_Page_Text_And_Replacement_Needs_A_Full_Redraw()
    {
        var block = Block(40, 40, 0, "Text");
        var replacement = new TextBlockReplacement(block.BlockId, null);
        Assert.True(OverlayLayers.ChangesText(block));
        Assert.True(OverlayLayers.ChangesText(replacement));
        Assert.True(OverlayLayers.IsDrawnOnTop(block));
        Assert.False(OverlayLayers.IsDrawnOnTop(replacement));   // удаляет объекты страницы

        // Страница с блоком уже с текстом: OCR не должен класть под него невидимый дубль.
        await using var document = await OpenedDocument.OpenAsync(_pdfium, Source(), null, CancellationToken.None);
        document.Session.Apply(new AddOverlayOperation(0, block));
        Assert.True(await new OcrService(null!).PageAlreadyHasTextAsync(document, 0, CancellationToken.None));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static X509Certificate2 MakeSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Block Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }
}
