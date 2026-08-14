using NexusPdf.Application;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Визуальное сравнение документов, метаданные и детект заявленного PDF/A.</summary>
public sealed class CompareTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Identical_Files_Compare_Equal()
    {
        var dir = TempDir();
        var bytes = PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "Same content"),
            new PdfFixture.PageSpec(612, 792, Text: "Second page"));
        var a = Path.Combine(dir, "a.pdf");
        var b = Path.Combine(dir, "b.pdf");
        File.WriteAllBytes(a, bytes);
        File.WriteAllBytes(b, bytes);

        await using var session = await CompareSession.OpenAsync(_pdfium, a, null, b, null, CancellationToken.None);
        var summary = await session.AnalyzeAsync(null, CancellationToken.None);
        Assert.Equal(0, summary.DifferentPages);
        Assert.All(summary.Pages, p => Assert.False(p.IsDifferent));
    }

    [Fact]
    public async Task Changed_Text_Extra_Page_And_Size_Are_Reported()
    {
        var dir = TempDir();
        var a = Path.Combine(dir, "a.pdf");
        var b = Path.Combine(dir, "b.pdf");
        File.WriteAllBytes(a, PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "Original text"),
            new PdfFixture.PageSpec(612, 792, Text: "Same page")));
        File.WriteAllBytes(b, PdfFixture.Build(
            new PdfFixture.PageSpec(612, 792, Text: "CHANGED text!!"),
            new PdfFixture.PageSpec(612, 792, Text: "Same page"),
            new PdfFixture.PageSpec(500, 500, Text: "Extra page")));

        await using var session = await CompareSession.OpenAsync(_pdfium, a, null, b, null, CancellationToken.None);
        var summary = await session.AnalyzeAsync(null, CancellationToken.None);

        Assert.Equal(3, summary.Pages.Count);
        Assert.True(summary.Pages[0].IsDifferent, "Изменённый текст должен дать отличие");
        Assert.True(summary.Pages[0].DiffPercent > 0.01);
        Assert.False(summary.Pages[1].IsDifferent, "Одинаковые страницы не должны отличаться");
        Assert.True(summary.Pages[2].OnlyInSecond);
        Assert.Equal(2, summary.DifferentPages);

        // Растры пары и маска отличий по требованию.
        var images = await session.GetPageImagesAsync(0, CancellationToken.None);
        Assert.NotNull(images.First);
        Assert.NotNull(images.Second);
        Assert.NotNull(images.DiffMask);
        Assert.Contains(images.DiffMask!, b1 => b1 == 1);
    }

    [Fact]
    public async Task Metadata_Is_Read_From_Document()
    {
        var path = PdfFixture.WriteToTemp("meta.pdf", new PdfFixture.PageSpec(612, 792));
        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        var meta = await handle.GetMetadataAsync(CancellationToken.None);
        Assert.Equal("1.4", meta.PdfVersion); // фикстура пишет %PDF-1.4
    }

    [Fact]
    public void PdfA_Claim_Is_Detected_And_Absent_Honestly()
    {
        var dir = TempDir();
        var plain = Path.Combine(dir, "plain.pdf");
        File.WriteAllBytes(plain, PdfFixture.Build(new PdfFixture.PageSpec(612, 792)));
        Assert.Null(PdfAClaimDetector.DetectClaim(plain));

        // Файл с XMP-заявлением PDF/A-2B (обе формы записи).
        var claimed = Path.Combine(dir, "claimed.pdf");
        var bytes = PdfFixture.Build(new PdfFixture.PageSpec(612, 792))
            .Concat(System.Text.Encoding.ASCII.GetBytes(
                "\n%<x:xmpmeta><rdf:Description pdfaid:part=\"2\" pdfaid:conformance=\"b\"/></x:xmpmeta>\n"))
            .ToArray();
        File.WriteAllBytes(claimed, bytes);
        Assert.Equal("PDF/A-2B", PdfAClaimDetector.DetectClaim(claimed));

        var elementForm = Path.Combine(dir, "element.pdf");
        var bytes2 = PdfFixture.Build(new PdfFixture.PageSpec(612, 792))
            .Concat(System.Text.Encoding.ASCII.GetBytes(
                "\n%<pdfaid:part>3</pdfaid:part><pdfaid:conformance>A</pdfaid:conformance>\n"))
            .ToArray();
        File.WriteAllBytes(elementForm, bytes2);
        Assert.Equal("PDF/A-3A", PdfAClaimDetector.DetectClaim(elementForm));
    }
}
