using System.Diagnostics;
using NexusPdf.Application;
using NexusPdf.Ocr;
using NexusPdf.Ocr.Paddle;
using NexusPdf.Pdf.Pdfium;
using Xunit.Abstractions;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Сравнение движков распознавания на НАСТОЯЩЕМ скане. Новый движок не должен
/// становиться основным «потому что новее» — только по замеру. Тест не
/// утверждает, кто лучше: он печатает цифры и следит, что оба вообще работают.
/// Если скана или моделей нет, тест честно пропускается, а не притворяется.
/// </summary>
public sealed class OcrEngineComparisonTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PdfiumRenderEngine _pdfium = null!;

    public OcrEngineComparisonTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private const string ScanPath = @"C:\Users\yurch\Desktop\For Send\Scan_121139.pdf";
    private const double TargetDpi = 300.0;

    [Fact]
    public async Task Compare_Tesseract_And_Paddle_On_A_Real_Scan()
    {
        if (!File.Exists(ScanPath))
        {
            _output.WriteLine("Скан для сравнения не найден, замер пропущен: " + ScanPath);
            return;
        }

        using var tesseract = new TesseractOcrEngine();
        using var paddle = new PaddleOcrEngine();
        _output.WriteLine($"Tesseract доступен: {tesseract.IsAvailable} {tesseract.UnavailableReason}");
        _output.WriteLine($"Paddle доступен:    {paddle.IsAvailable} {paddle.UnavailableReason}");
        if (!tesseract.IsAvailable || !paddle.IsAvailable)
        {
            _output.WriteLine("Один из движков недоступен — сравнивать нечего.");
            return;
        }

        await using var doc = await _pdfium.OpenAsync(ScanPath, null, CancellationToken.None);
        var size = doc.Info.Pages[0];
        var scale = TargetDpi / 72.0;
        var width = (int)Math.Round(size.WidthPoints * scale);
        var height = (int)Math.Round(size.HeightPoints * scale);
        var image = await doc.RenderPageContentOnlyAsync(0, width, height, 0, CancellationToken.None);
        _output.WriteLine($"Растр: {width}×{height} px при {TargetDpi} DPI");

        var sw = Stopwatch.StartNew();
        var t = await tesseract.RecognizeAsync(image, (int)TargetDpi, CancellationToken.None);
        var tesseractMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var p = await paddle.RecognizeAsync(image, (int)TargetDpi, CancellationToken.None);
        var paddleMs = sw.ElapsedMilliseconds;

        var tesseractText = string.Join(" ", t.Words.Select(w => w.Text));
        var paddleText = string.Join(" ", p.Words.Select(w => w.Text));

        _output.WriteLine("");
        _output.WriteLine($"Tesseract: блоков {t.Words.Count,5}, символов {tesseractText.Length,6}, " +
                          $"уверенность {t.MeanConfidence,5:F1}%, время {tesseractMs,6} мс");
        _output.WriteLine($"Paddle:    блоков {p.Words.Count,5}, символов {paddleText.Length,6}, " +
                          $"уверенность {p.MeanConfidence,5:F1}%, время {paddleMs,6} мс");
        _output.WriteLine("");
        _output.WriteLine("--- первые 400 символов Tesseract ---");
        _output.WriteLine(tesseractText.Substring(0, Math.Min(400, tesseractText.Length)));
        _output.WriteLine("");
        _output.WriteLine("--- первые 400 символов Paddle ---");
        _output.WriteLine(paddleText.Substring(0, Math.Min(400, paddleText.Length)));

        // Оба движка обязаны хоть что-то прочитать на читаемом скане.
        Assert.True(t.Words.Count > 0, "Tesseract не прочитал ничего");
        Assert.True(p.Words.Count > 0, "Paddle не прочитал ничего");
    }
}
