using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.TestSupport;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Правка строки или абзаца перезаписывает поток содержимого страницы. PDFium
/// умеет записать только цвета RGB и серые: фон в CMYK, ICC (так пишет Office),
/// CalRGB или плашечном цвете после правки становился чёрным. Фон обязан
/// остаться того же цвета; градиент, который перезапись потеряла бы, правкой
/// не трогается вовсе.
/// </summary>
public sealed class ContentRewriteColourTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "rewrite-" + Guid.NewGuid().ToString("N"));
    private FontCatalog _fonts = null!;
    private PdfiumRenderEngine _pdfium = null!;

    private const string Text =
        " BT 0 g /F1 12 Tf 72 680 Td (Text on a background) Tj ET BT 0 g /F1 12 Tf 72 666 Td (second line of it) Tj ET";

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

    public static TheoryData<string, string, string> Backgrounds => new()
    {
        { "cmyk", "0.2 0.1 0 0 k", "" },
        { "calrgb", "/CS1 cs 0.8 0.9 1 scn", "/ColorSpace << /CS1 [/CalRGB << /WhitePoint [0.9505 1 1.089] >>] >>" },
        { "separation", "/CS0 cs 0.4 scn",
          "/ColorSpace << /CS0 [/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0.5 0 0.5 0] /N 1 >>] >>" },
    };

    private string Source(string setup, string resources)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792,
            RawContent: $"q {setup} 50 600 400 120 re f Q 0.5 0 0 RG 2 w 50 590 m 450 590 l S" + Text, ExtraResources: resources)));
        return path;
    }

    private async Task<(byte R, byte G, byte B)> PixelAsync(string path, int x, int y)
    {
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var render = await doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);
        var at = (long)y * render.Stride + x * 4;
        return (render.Bgra[at + 2], render.Bgra[at + 1], render.Bgra[at]);
    }

    private static void AssertNear((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual)
    {
        Assert.InRange(actual.R, Math.Max(0, expected.R - 8), Math.Min(255, expected.R + 8));
        Assert.InRange(actual.G, Math.Max(0, expected.G - 8), Math.Min(255, expected.G + 8));
        Assert.InRange(actual.B, Math.Max(0, expected.B - 8), Math.Min(255, expected.B + 8));
    }

    private async Task<string> ComposeAsync(string source, PageOverlay overlay)
    {
        var target = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        await using var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        await _pdfium.ComposeAsync([new ComposedPage(doc, 0, 0, [overlay])], target, CancellationToken.None);
        return target;
    }

    [Theory]
    [MemberData(nameof(Backgrounds))]
    public async Task Line_Edit_Keeps_The_Background_Colour(string name, string setup, string resources)
    {
        var source = Source(setup, resources);
        var background = await PixelAsync(source, 60, 100);
        var line = await PixelAsync(source, 200, 202);
        await using var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        var layout = await doc.GetPageTextLayoutAsync(0, 0, CancellationToken.None);

        var saved = await ComposeAsync(source, new TextObjectReplacement(layout.Objects[0].Path, "Changed line"));

        AssertNear(background, await PixelAsync(saved, 60, 100));
        AssertNear(line, await PixelAsync(saved, 200, 202));
        Assert.NotNull(name);
    }

    [Theory]
    [MemberData(nameof(Backgrounds))]
    public async Task Paragraph_Edit_Keeps_The_Background_Colour(string name, string setup, string resources)
    {
        var source = Source(setup, resources);
        var background = await PixelAsync(source, 60, 100);
        await using var doc = await _pdfium.OpenAsync(source, null, CancellationToken.None);
        var layout = await doc.GetPageTextLayoutAsync(0, 0, CancellationToken.None);
        var paragraph = ParagraphBuilder.Build(layout, FormTargetIndex.Build(layout, 11), _fonts).Single(p => p.Block != null);
        var refs = paragraph.Objects.Select(o => new ParagraphObjectRef(o.Path, o.Fingerprint)).ToList();

        var saved = await ComposeAsync(source, new TextParagraphReplacement(refs, paragraph.Block));

        AssertNear(background, await PixelAsync(saved, 60, 100));
        Assert.NotNull(name);
    }

    [Fact]
    public async Task Page_With_A_Gradient_Object_Refuses_Paragraph_And_In_Place_Line_Edits()
    {
        var path = Path.Combine(_dir, "gradient.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(612, 792,
            RawContent: "q 50 600 400 120 re W n /Sh0 sh Q" + Text,
            ExtraResources: "/Shading << /Sh0 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [50 600 450 600] " +
                            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.9 0.8] /C1 [0.8 0.9 1] /N 1 >> >> >>")));
        await using var doc = await _pdfium.OpenAsync(path, null, CancellationToken.None);

        var layout = await doc.GetPageTextLayoutAsync(0, 0, CancellationToken.None);
        var paragraph = Assert.Single(ParagraphBuilder.Build(layout, FormTargetIndex.Build(layout, 11), _fonts));
        var line = await doc.GetTextObjectAtAsync(0, 0, 100, 108, CancellationToken.None);

        Assert.True(layout.HasGradients);
        Assert.Equal("TextParagraphGradient", paragraph.RefusedKey);
        Assert.NotNull(line);
        Assert.False(line!.CanEdit);
    }
}
