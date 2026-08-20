using NexusPdf.Application;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;
using NexusPdf.Printing;
using Xunit.Abstractions;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Разрешения PDF на печать. До этой работы они не читались вовсе, и документ
/// с запретом печати печатался как обычный. Проверяется на НАСТОЯЩЕМ
/// зашифрованном файле, а не на подставленных флагах.
/// </summary>
public sealed class PrintPermissionTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PdfiumRenderEngine _pdfium = null!;
    private string _dir = "";

    public PrintPermissionTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Theory]
    [InlineData(0xFFFFFFFF, true, true)]   // не защищён — можно всё
    [InlineData(0xFFFFF7FFu, true, false)] // снят бит 12: только низкое качество
    [InlineData(0xFFFFFFFBu, false, false)] // снят бит 3: печать запрещена
    public void Flags_Are_Decoded_By_The_Spec(uint flags, bool print, bool highQuality)
    {
        var permissions = PrintPermissions.FromFlags(flags);
        Assert.Equal(print, permissions.AllowPrint);
        Assert.Equal(highQuality, permissions.AllowHighQuality);
    }

    [Fact]
    public void High_Quality_Bit_Alone_Does_Not_Allow_Printing()
    {
        // Бит 12 без бита 3 бессмыслен: печать запрещена целиком.
        var permissions = PrintPermissions.FromFlags(0b1000_0000_0000);
        Assert.False(permissions.AllowPrint);
        Assert.False(permissions.AllowHighQuality);
    }

    [Fact]
    public void Low_Quality_Permission_Caps_The_Resolution()
    {
        var limited = new PrintPermissions(AllowPrint: true, AllowHighQuality: false);
        Assert.Equal(150, limited.LimitDpi(600));
        Assert.Equal(72, limited.LimitDpi(72)); // ниже предела не поднимаем

        Assert.Equal(600, PrintPermissions.Unrestricted.LimitDpi(600));
    }

    [Fact]
    public async Task Unprotected_Document_Reports_No_Restrictions()
    {
        var path = Path.Combine(_dir, "plain.pdf");
        File.WriteAllBytes(path, PdfFixture.Build(new PdfFixture.PageSpec(595, 842, Text: "PLAIN")));

        await using var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        var flags = await document.PrimaryHandle.GetPermissionsAsync(CancellationToken.None);
        _output.WriteLine($"флаги обычного документа: 0x{flags:X8}");

        Assert.True(PrintPermissions.FromFlags(flags).AllowPrint);
    }

    [Fact]
    public async Task Document_That_Forbids_Printing_Blocks_The_Job()
    {
        var qpdf = new QpdfEngine();
        if (!qpdf.IsAvailable)
        {
            _output.WriteLine("qpdf недоступен — зашифровать файл нечем, проверка пропущена: " +
                              qpdf.UnavailableReason);
            return;
        }

        var source = Path.Combine(_dir, "source.pdf");
        File.WriteAllBytes(source, PdfFixture.Build(new PdfFixture.PageSpec(595, 842, Text: "SECRET")));

        // Настоящий зашифрованный файл с запретом печати, а не выдуманные флаги.
        var protectedPath = Path.Combine(_dir, "noprint.pdf");
        await qpdf.EncryptAsync(source, protectedPath, "open", "owner",
            CancellationToken.None, allowPrint: false);

        await using var document = await OpenedDocument.OpenAsync(
            _pdfium, protectedPath, "open", CancellationToken.None);

        var flags = await document.PrimaryHandle.GetPermissionsAsync(CancellationToken.None);
        var permissions = PrintPermissions.FromFlags(flags);
        _output.WriteLine($"флаги защищённого документа: 0x{flags:X8}, печать разрешена: {permissions.AllowPrint}");

        Assert.False(permissions.AllowPrint);

        // Предварительная проверка обязана заблокировать задание целиком.
        var plan = MakePlan();
        var issues = Preflight.Analyze(plan, permissions);
        var blocking = Assert.Single(issues, i => i.Level == PreflightLevel.Critical);
        Assert.Equal(Preflight.CodePrintForbidden, blocking.Code);

        // И экспорт раскладки в файл тоже: это тот же вывод содержимого.
        var target = Path.Combine(_dir, "layout.pdf");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PrintToFileService(_pdfium).SaveAsync(
                document, plan with { Issues = issues }, target, 300, null, CancellationToken.None));
        _output.WriteLine("экспорт отклонён: " + error.Message);
        Assert.False(File.Exists(target), "файл раскладки не должен создаваться");
    }

    private static PrintJobPlan MakePlan()
    {
        var a4 = new SizePt(595.28, 841.89);
        var caps = new PrinterCapabilities
        {
            PrinterName = "Тест",
            PaperSizes = new[] { new PaperSizeOption("A4", a4) },
        };
        return new PrintJobPlan
        {
            JobName = "test",
            PrinterName = "Тест",
            Capabilities = caps,
            Sheets = new[]
            {
                new SheetPlan
                {
                    SheetIndex = 0,
                    PaperSizePt = a4,
                    PrintableAreaPt = RectPt.FromSize(a4),
                    HardMarginsPt = MarginsPt.Zero,
                    Pages = new[]
                    {
                        new PlacedPage
                        {
                            DocumentId = "doc",
                            SourcePageIndex = 0,
                            Box = PageBoxKind.CropBox,
                            SourceRectPt = RectPt.FromSize(a4),
                            TargetRectPt = RectPt.FromSize(a4),
                            ClipRectPt = RectPt.FromSize(a4),
                            Scale = 1.0,
                            RotationDegrees = 0,
                        },
                    },
                },
            },
        };
    }
}
