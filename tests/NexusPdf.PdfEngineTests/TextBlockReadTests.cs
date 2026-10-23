using System.Globalization;
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
/// Сохранённый блок открывается для правки снова — но только целым и только
/// своим: блок, который поменяла другая программа, остаётся обычным текстом,
/// а поддельные метки не роняют чтение.
/// </summary>
public sealed class TextBlockReadTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "tb-read-" + Guid.NewGuid().ToString("N"));
    private string _goneDir = null!;
    private FontCatalog _fonts = null!;
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _goneDir = Path.Combine(_dir, "gone");
        Directory.CreateDirectory(_goneDir);
        File.WriteAllBytes(Path.Combine(_goneDir, "gone.ttf"), TinyFontBuilder.Build(new TinyFontSpec("Gone")));
        _fonts = FontCatalog.Scan([TestFontPaths.Directory, _goneDir]);
        _pdfium = new PdfiumRenderEngine(null, _fonts);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pdfium.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static TextBlockOverlay Block(string family = "Liberation Sans", params string[] paragraphs) =>
        new(40, 50, 220, 10, 0, TextBlockWidthMode.Fixed, true, false,
            paragraphs.Select((p, i) => new TextParagraph(
                [new TextRun(p, new TextStyle(family, 14, Bold: i == 1, ColorArgb: 0xFF1A1AB4))],
                i == 1 ? TextAlign.Center : TextAlign.Left)).ToList(),
            Guid.NewGuid());

    private string Source(string text = "", string? raw = null)
    {
        var path = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(400, 300, Text: text, RawContent: raw)));
        return path;
    }

    private async Task<string> ComposeAsync(string source, params PageOverlay[] overlays)
    {
        var target = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N") + ".pdf");
        await using var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        await _pdfium.ComposeAsync([new ComposedPage(doc, 0, 0, overlays)], target, CancellationToken.None);
        return target;
    }

    private async Task<IReadOnlyList<StoredTextBlock>> BlocksAsync(string path, PdfiumRenderEngine? engine = null)
    {
        await using var doc = await (engine ?? _pdfium).OpenAsync(path, null, CancellationToken.None);
        return await doc.GetTextBlocksAsync(0, 0, CancellationToken.None);
    }

    private async Task<string> PageTextAsync(string path)
    {
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        return await doc.GetPageTextAsync(0, CancellationToken.None);
    }

    private static void AssertSameBlock(TextBlockOverlay expected, TextBlockOverlay? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(TextBlockCodec.EncodeStructure(expected), TextBlockCodec.EncodeStructure(actual!));
        Assert.Equal(expected.Paragraphs.Select(p => p.Text), actual!.Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public async Task Saved_Block_Is_Found_Again()
    {
        var block = Block(paragraphs: ["First line of the block", "Centred bold", "Third"]);
        var saved = await ComposeAsync(Source("Existing"), block);

        var stored = Assert.Single(await BlocksAsync(saved));
        Assert.True(stored.Intact);
        Assert.Equal(block.BlockId, stored.Id);
        AssertSameBlock(block, stored.Block);
        Assert.InRange(stored.BoundsPt.XPt, block.XPt - 1, block.XPt + 6);
        Assert.InRange(stored.BoundsPt.YPt, block.YPt - 1, block.YPt + 8);
    }

    public static TheoryData<string> RoundTrips => new()
    {
        "wrapped", "justify", "letter-spacing", "line-break", "decorations", "super-sub", "rotated-page", "mixed-runs",
    };

    /// <summary>
    /// Главное обещание блока — правка после сохранения. Каждый вид объектов,
    /// которые пишет блок (пробелы в конце строк, одни пробелы при выравнивании
    /// по ширине, буква-объект при интервале, линии, индексы), обязан читаться
    /// обратно целым.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoundTrips))]
    public async Task Saved_Block_Of_Every_Kind_Reopens_Intact(string kind)
    {
        TextStyle Sans(double size = 12) => new("Liberation Sans", size);
        TextParagraph Para(string text, TextStyle? style = null, TextAlign align = TextAlign.Left) =>
            new([new TextRun(text, style ?? Sans())], align);
        TextBlockOverlay Make(double width, double rotation, params TextParagraph[] paragraphs) =>
            new(50, 60, width, 10, rotation, TextBlockWidthMode.Fixed, true, false, paragraphs, Guid.NewGuid());

        var rotate = 0;
        var block = kind switch
        {
            "wrapped" => Make(90, 0, Para("alpha beta gamma delta epsilon zeta eta theta")),
            "justify" => Make(110, 0, Para("one two three four five six seven eight nine ten", align: TextAlign.Justify)),
            "letter-spacing" => Make(200, 0, Para("Spaced out", Sans() with { LetterSpacingPercent = 25 })),
            "line-break" => Make(200, 0, Para("first" + TextBlockText.LineBreak + "second" + TextBlockText.LineBreak + "third")),
            "decorations" => Make(200, 0, Para("under and struck", Sans() with { Underline = true, Strike = true, ColorArgb = 0xFFD3282F })),
            "super-sub" => Make(200, 0, new TextParagraph([
                new TextRun("E=mc", Sans()), new TextRun("2", Sans() with { Baseline = BaselineShift.Superscript }),
                new TextRun(" H", Sans()), new TextRun("2", Sans() with { Baseline = BaselineShift.Subscript }), new TextRun("O", Sans())])),
            "rotated-page" => Make(160, 30, Para("turned twice over")),
            _ => Make(220, 0, new TextParagraph([
                new TextRun("Bold ", Sans() with { Bold = true }), new TextRun("italic ", Sans(14) with { Italic = true }),
                new TextRun("mono", new TextStyle("Liberation Mono", 10))], TextAlign.Center), Para("Последняя строка")),
        };
        if (kind == "rotated-page") rotate = 90;

        var path = Path.Combine(_dir, "kind-" + kind + ".pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(400, 300, rotate, Text: "")));
        var saved = await ComposeAsync(path, block);

        var stored = Assert.Single(await BlocksAsync(saved));
        Assert.True(stored.Intact, kind + ": the block must reopen as a block");
        AssertSameBlock(block, stored.Block);
    }

    [Fact]
    public async Task Marks_Survive_Repeated_Full_And_Incremental_Saves()
    {
        var block = Block(paragraphs: ["Survives saves", "and more"]);
        var first = await ComposeAsync(Source(), block);

        // Полная перезапись через сохранение документа с другой правкой.
        var full = Path.Combine(_dir, "full.pdf");
        await using (var document = await OpenedDocument.OpenAsync(_pdfium, first, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("Stamp", 40, 250, 12, 0xFF000000, 0, "Liberation Sans", false, false)));
            await new SaveService(_pdfium).SaveAsAsync(document, full, keepBackup: false, CancellationToken.None);
        }
        AssertSameBlock(block, Assert.Single(await BlocksAsync(full)).Block);

        // Дописывание: подписанный документ сохраняется только так. Подписать
        // можно лишь «нормализованный» файл — фикстуру, поэтому блок
        // добавляется уже после подписи, и дописываний два подряд.
        var source = Source();
        var signed = Path.Combine(_dir, "signed.pdf");
        using (var certificate = MakeSelfSigned())
            PdfIncrementalSigner.Sign(source, signed, certificate, "", "");
        var once = Path.Combine(_dir, "appended-once.pdf");
        await using (var document = await OpenedDocument.OpenAsync(_pdfium, signed, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, block));
            var result = await new SaveService(_pdfium).SaveAsAsync(document, once, keepBackup: false, CancellationToken.None);
            Assert.True(result.SignaturesPreserved);
        }
        AssertSameBlock(block, Assert.Single(await BlocksAsync(once)).Block);

        var twice = Path.Combine(_dir, "appended-twice.pdf");
        await using (var document = await OpenedDocument.OpenAsync(_pdfium, once, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0,
                new TextOverlay("Second", 40, 270, 12, 0xFF000000, 0, "Liberation Sans", false, false)));
            var result = await new SaveService(_pdfium).SaveAsAsync(document, twice, keepBackup: false, CancellationToken.None);
            Assert.True(result.SignaturesPreserved);
        }
        var stored = Assert.Single(await BlocksAsync(twice));
        Assert.True(stored.Intact);
        AssertSameBlock(block, stored.Block);
    }

    [Fact]
    public async Task Replacement_Removes_Old_Objects_And_Keeps_Id()
    {
        var block = Block(paragraphs: ["Old words here"]);
        var saved = await ComposeAsync(Source("Keep me"), block);
        var changed = Block(paragraphs: ["Brand new text", "Second"]) with { BlockId = Guid.Empty };

        var replaced = await ComposeAsync(saved, new TextBlockReplacement(block.BlockId, changed));

        var stored = Assert.Single(await BlocksAsync(replaced));
        Assert.Equal(block.BlockId, stored.Id);
        Assert.True(stored.Intact);
        Assert.Equal(["Brand new text", "Second"], stored.Block!.Paragraphs.Select(p => p.Text));
        var text = await PageTextAsync(replaced);
        Assert.DoesNotContain("Old words", text);
        Assert.Contains("Keep me", text);
    }

    [Fact]
    public async Task Replacement_With_Null_Deletes_The_Block()
    {
        var block = Block(paragraphs: ["Delete these words"]);
        var saved = await ComposeAsync(Source("Keep me"), block);

        var deleted = await ComposeAsync(saved, new TextBlockReplacement(block.BlockId, null));

        Assert.Empty(await BlocksAsync(deleted));
        var text = await PageTextAsync(deleted);
        Assert.DoesNotContain("Delete", text);
        Assert.Contains("Keep me", text);
    }

    [Fact]
    public async Task Block_Changed_By_Another_Program_Is_Not_Restored()
    {
        var block = Block(paragraphs: ["Original wording"]);
        var saved = await ComposeAsync(Source(), block);

        // Правка строкой меняет текст объекта на месте — как сделала бы чужая программа.
        PdfTextObject target;
        await using (var doc = await _pdfium.OpenAsync(saved, null, CancellationToken.None))
            target = (await doc.GetTextObjectAtAsync(0, 0, block.XPt + 10, block.YPt + 10, CancellationToken.None))!;
        Assert.NotNull(target);
        var foreign = await ComposeAsync(saved, new TextObjectReplacement(target.ObjectPath, "Foreign"));

        var stored = Assert.Single(await BlocksAsync(foreign));
        Assert.False(stored.Intact);
        Assert.Null(stored.Block);
        Assert.Contains("Foreign", await PageTextAsync(foreign));
    }

    [Fact]
    public async Task Region_Erase_Over_A_Block_Leaves_No_Block()
    {
        var block = Block(paragraphs: ["Erase me"]);
        var saved = await ComposeAsync(Source(), block);
        var erased = Path.Combine(_dir, "erased.pdf");

        await using (var document = await OpenedDocument.OpenAsync(_pdfium, saved, null, CancellationToken.None))
        {
            document.Session.Apply(new AddOverlayOperation(0, new RegionEraseDraft(30, 40, 200, 40, 0xFFFFFFFF)));
            await new SaveService(_pdfium).SaveAsAsync(document, erased, keepBackup: false, CancellationToken.None);
        }

        Assert.Empty(await BlocksAsync(erased));
    }

    [Fact]
    public async Task Reopened_Without_The_Font_Uses_Substitute()
    {
        var block = Block("Gone", "ghost font words");
        var saved = await ComposeAsync(Source(), block);

        var withoutGone = FontCatalog.Scan([TestFontPaths.Directory]);
        await using var engine = new PdfiumRenderEngine(null, withoutGone);
        var stored = Assert.Single(await BlocksAsync(saved, engine));

        Assert.True(stored.Intact);
        AssertSameBlock(block, stored.Block);
        var layout = TextBlockLayout.Compute(stored.Block!, withoutGone);
        Assert.All(layout.Lines.SelectMany(l => l.Pieces), p => Assert.True(p.IsFallback));
    }

    public static TheoryData<string> Forgeries => new() { "broken-data", "wild-reference", "no-hash", "fifty-thousand" };

    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task Forged_Marks_Do_Not_Break_The_Reader(string kind)
    {
        var id = Guid.NewGuid().ToString("N");
        var valid = TextBlockCodec.EncodeStructure(new TextBlockOverlay(10, 10, 100, 20, 0, TextBlockWidthMode.AutoWidth, true, false,
            [new TextParagraph([new TextRun("ab", new TextStyle("Liberation Sans", 10))])]));
        string Marked(string param, string text, double y) =>
            $"/NexusTextBlock <</Id ({id})/V (1){param}>> BDC BT /F1 10 Tf 20 {y.ToString(CultureInfo.InvariantCulture)} Td ({text}) Tj ET EMC\n";
        string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        var raw = kind switch
        {
            "broken-data" => Marked("/R (0:0)/Data ({)/Hash (00)", "ab", 200),
            "wild-reference" => Marked($"/R (9999999:-5)/Data ({Escape(valid)})/Hash (00)", "ab", 200),
            "no-hash" => Marked($"/R (0:0)/Data ({Escape(valid)})", "ab", 200),
            _ => string.Concat(Enumerable.Range(0, 50_000).Select(i =>
                Marked($"/R (0:{i})", "a", 10 + i % 250))),
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stored = Assert.Single(await BlocksAsync(Source(raw: raw)));
        Assert.False(stored.Intact);
        Assert.Null(stored.Block);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"чтение заняло {watch.Elapsed}");
    }

    [Fact]
    public async Task Shared_Huge_Description_Is_Read_Once()
    {
        // Один словарь свойств с описанием в 2 МБ, на который ссылаются сто
        // объектов: копировать описание для каждого — сотни мегабайт, а при
        // тысячах объектов и большем описании — зависание и нехватка памяти.
        var id = Guid.NewGuid().ToString("N");
        var properties = $"/Properties << /MC0 << /Id ({id}) /V (1) /R (0:0) /Data ({new string('x', 2_000_000)}) >> >>";
        var raw = string.Concat(Enumerable.Range(0, 100).Select(i =>
            $"/NexusTextBlock /MC0 BDC BT /F1 10 Tf 20 {10 + i * 2} Td (a) Tj ET EMC\n"));
        var path = Path.Combine(_dir, "shared.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(400, 300, RawContent: raw, ExtraResources: properties)));

        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stored = Assert.Single(await doc.GetTextBlocksAsync(0, 0, CancellationToken.None));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.False(stored.Intact);
        Assert.True(allocated < 100_000_000, $"чтение меток выделило {allocated / 1_000_000} МБ");
    }

    [Fact]
    public async Task Page_That_Exhausts_The_Mark_Budget_Fails_Closed()
    {
        // Огромный общий Id у сотни объектов выбирает весь бюджет чтения меток.
        // Дальше нельзя ни открыть блок для правки, ни молча оставить на
        // странице его старые объекты при замене.
        var properties = $"/Properties << /MC0 << /Id ({new string('7', 1_000_000)}) /V (1) /R (0:0) >> >>";
        var raw = string.Concat(Enumerable.Range(0, 100).Select(i =>
            $"/NexusTextBlock /MC0 BDC BT /F1 10 Tf 20 {10 + i * 2} Td (a) Tj ET EMC\n"));
        var hostile = Path.Combine(_dir, "hostile.pdf");
        File.WriteAllBytes(hostile, PdfFixture.Build(new PdfFixture.PageSpec(400, 300, RawContent: raw, ExtraResources: properties)));
        var block = Block(paragraphs: ["Our block"]);
        var saved = await ComposeAsync(hostile, block);

        Assert.DoesNotContain(await BlocksAsync(saved), b => b.Intact);
        await Assert.ThrowsAsync<PdfEngineException>(() =>
            ComposeAsync(saved, new TextBlockReplacement(block.BlockId, Block(paragraphs: ["New"]))));
    }

    [Fact]
    public async Task Document_Font_Names_Are_Listed()
    {
        var mono = new TextBlockOverlay(40, 150, 200, 10, 0, TextBlockWidthMode.Fixed, true, false,
            [new TextParagraph([new TextRun("mono", new TextStyle("Liberation Mono", 12))])], Guid.NewGuid());
        var saved = await ComposeAsync(Source("Hello"), Block(paragraphs: ["sans"]), mono);

        await using var doc = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        var names = await doc.GetDocumentFontNamesAsync(10, CancellationToken.None);

        Assert.Contains(names, n => n.Contains("Helvetica", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("LiberationSans", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("LiberationMono", StringComparison.Ordinal));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static X509Certificate2 MakeSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Block Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }
}
