using NexusPdf.Pdf.Pdfium;
using RapidOcrNet;
using Xunit.Abstractions;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Разбор, почему связка моделей не читает. Проверяются по очереди:
/// комплектные модели пакета, наш детектор v6 и наш распознаватель, — чтобы
/// увидеть, какое именно звено ломается, а не гадать.
/// </summary>
public sealed class PaddleDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    public PaddleDiagnosticTests(ITestOutputHelper output) => _output = output;

    private const string ScanPath = @"C:\Users\yurch\Desktop\For Send\Scan_121139.pdf";

    private static string? ModelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = 0; dir != null && d < 8; d++, dir = dir.Parent)
        {
            var c = Path.Combine(dir.FullName, "tools", "ocrmodels");
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    private static SkiaSharp.SKBitmap Render(NexusPdf.Pdf.Abstractions.RenderedPageImage image)
    {
        var info = new SkiaSharp.SKImageInfo(image.PixelWidth, image.PixelHeight,
            SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        var bmp = new SkiaSharp.SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Bgra, 0, bmp.GetPixels(), image.Bgra.Length);
        return bmp;
    }

    [Fact]
    public async Task Which_Model_Combination_Actually_Reads()
    {
        if (!File.Exists(ScanPath)) { _output.WriteLine("скана нет"); return; }
        var models = ModelsDir();
        _output.WriteLine("каталог моделей: " + (models ?? "НЕ НАЙДЕН"));
        if (models != null)
            foreach (var f in Directory.EnumerateFiles(models))
                _output.WriteLine("  " + Path.GetFileName(f));

        // Комплектные модели пакет кладёт рядом со СВОЕЙ сборкой, а не с той,
        // что запущена: ищем и там, и там.
        var bundled = Path.Combine(AppContext.BaseDirectory, "models");
        if (!Directory.Exists(bundled))
        {
            var paddleDir = Path.GetDirectoryName(
                typeof(NexusPdf.Ocr.Paddle.PaddleOcrEngine).Assembly.Location);
            if (paddleDir != null && Directory.Exists(Path.Combine(paddleDir, "models")))
                bundled = Path.Combine(paddleDir, "models");
            else
            {
                var repo = new DirectoryInfo(AppContext.BaseDirectory);
                for (var d = 0; repo != null && d < 8; d++, repo = repo.Parent)
                {
                    var c = Path.Combine(repo.FullName, "src", "NexusPdf.Ocr.Paddle",
                        "bin", "Release", "net10.0", "models");
                    if (Directory.Exists(c)) { bundled = c; break; }
                }
            }
        }
        _output.WriteLine("комплектные модели: " + bundled + " есть=" + Directory.Exists(bundled));
        if (Directory.Exists(bundled))
            foreach (var f in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
                _output.WriteLine("  " + f.Substring(bundled.Length + 1));

        await using var pdfium = new PdfiumRenderEngine();
        await using var doc = await pdfium.OpenAsync(ScanPath, null, CancellationToken.None);
        var size = doc.Info.Pages[0];
        var image = await doc.RenderPageContentOnlyAsync(0,
            (int)Math.Round(size.WidthPoints * 300.0 / 72.0),
            (int)Math.Round(size.HeightPoints * 300.0 / 72.0), 0, CancellationToken.None);
        using var bmp = Render(image);

        void Try(string label, Action<RapidOcr> init)
        {
            try
            {
                using var ocr = new RapidOcr();
                init(ocr);
                var r = ocr.Detect(bmp, RapidOcrOptions.Default);
                var text = string.Join(" ", r.TextBlocks.Select(b => b.Text));
                _output.WriteLine($"{label}: блоков {r.TextBlocks.Length}, символов {text.Length}");
                if (text.Length > 0)
                    _output.WriteLine("   " + text.Substring(0, Math.Min(160, text.Length)));
            }
            catch (Exception ex)
            {
                _output.WriteLine($"{label}: ИСКЛЮЧЕНИЕ {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 1. Полностью комплектная связка пакета (латиница v5).
        Try("комплектные v5 latin", o => o.InitModels());

        if (models == null) return;
        var det6 = Path.Combine(models, "PP-OCRv6_det_medium.onnx");
        var rec5 = Path.Combine(models, "cyrillic_PP-OCRv5_rec_mobile.onnx");
        var dict5 = Path.Combine(models, "ppocrv5_cyrillic_dict.txt");
        var cls = Directory.EnumerateFiles(bundled, "*cls*.onnx", SearchOption.AllDirectories).FirstOrDefault();
        var det5 = Directory.EnumerateFiles(bundled, "*det*.onnx", SearchOption.AllDirectories).FirstOrDefault();
        _output.WriteLine($"cls={cls}  det5={det5}");

        // 2. Наш детектор v6 + наш распознаватель.
        if (cls != null)
            Try("det v6 + rec кириллица", o => o.InitModels(det6, cls, rec5, dict5));

        // 3. Комплектный детектор v5 + наш распознаватель — проверка гипотезы,
        //    что несовместим именно детектор v6.
        if (cls != null && det5 != null)
            Try("det v5 + rec кириллица", o => o.InitModels(det5, cls, rec5, dict5));
    }
}
