using System.Diagnostics;
using NexusPdf.Ocr.Paddle;
using NexusPdf.Pdf.Pdfium;
using Xunit.Abstractions;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Каждый языковой пакет обязан подниматься и читать. Модель и словарь идут
/// парой: с чужим словарём распознаватель не падает, а выдаёт мусор, поэтому
/// проверяется не «файлы на месте», а фактический прогон.
/// </summary>
public sealed class AllLanguagePacksTests
{
    private readonly ITestOutputHelper _output;
    public AllLanguagePacksTests(ITestOutputHelper output) => _output = output;

    private const string ScanPath = @"C:\Users\yurch\Desktop\For Send\Scan_121139.pdf";

    [Fact]
    public void Catalog_Is_Read_From_Lock_File()
    {
        var catalog = PaddleOcrEngine.Catalog;
        _output.WriteLine($"пакетов в каталоге: {catalog.Count}");
        foreach (var p in catalog)
            _output.WriteLine($"  {p.Id,-20} {p.ModelFile,-38} {p.DictFile}");

        Assert.NotEmpty(catalog);
        Assert.Single(catalog.Where(p => p.IsDefault));
        // Идентификатор пакета НЕ обязан совпадать с именем файла — ровно
        // поэтому имена берутся из каталога, а не угадываются.
        Assert.Contains(catalog, p => p.Id == "japanese" && p.ModelFile.StartsWith("japan_"));
        Assert.Contains(catalog, p => p.Id == "chinese" && p.ModelFile.StartsWith("ch_"));
    }

    /// <summary>
    /// Полный обход всех пакетов занимает минуты (китайская server-модель одна
    /// съедает пять), поэтому в обычном прогоне он выключен и запускается
    /// переменной NEXUSPDF_TEST_ALL_PACKS=1.
    /// </summary>
    [Fact]
    public async Task Every_Installed_Pack_Loads_And_Reads()
    {
        if (Environment.GetEnvironmentVariable("NEXUSPDF_TEST_ALL_PACKS") != "1")
        {
            _output.WriteLine("Полная проверка пакетов выключена. NEXUSPDF_TEST_ALL_PACKS=1 — включить.");
            return;
        }

        var installed = PaddleOcrEngine.InstalledPacks(AppContext.BaseDirectory);
        _output.WriteLine($"установлено пакетов: {installed.Count} из {PaddleOcrEngine.Catalog.Count}");
        if (installed.Count == 0)
        {
            _output.WriteLine("Модели не загружены — запустите tools/fetch-ocrmodels.ps1 -All");
            return;
        }
        if (!File.Exists(ScanPath))
        {
            _output.WriteLine("Скана для прогона нет, проверка пропущена");
            return;
        }

        await using var pdfium = new PdfiumRenderEngine();
        await using var doc = await pdfium.OpenAsync(ScanPath, null, CancellationToken.None);
        var size = doc.Info.Pages[0];
        // Половинный масштаб: цель — проверить работоспособность всех пакетов,
        // а не качество; полный размер на 16 моделях занял бы минуты.
        var image = await doc.RenderPageContentOnlyAsync(0,
            (int)Math.Round(size.WidthPoints * 150.0 / 72.0),
            (int)Math.Round(size.HeightPoints * 150.0 / 72.0), 0, CancellationToken.None);

        var failures = new List<string>();
        foreach (var pack in installed)
        {
            using var engine = new PaddleOcrEngine(AppContext.BaseDirectory, pack.Id);
            try
            {
                var sw = Stopwatch.StartNew();
                var result = await engine.RecognizeAsync(image, 150, CancellationToken.None);
                var chars = result.Words.Sum(w => w.Text.Length);
                _output.WriteLine(
                    $"{pack.Id,-20} строк {result.Words.Count,4}, символов {chars,5}, " +
                    $"уверенность {result.MeanConfidence,5:F1}%, {sw.ElapsedMilliseconds,6} мс");
                if (result.Words.Count == 0)
                    failures.Add($"{pack.Id}: не прочитал ничего");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"{pack.Id,-20} ОШИБКА: {ex.Message}");
                failures.Add($"{pack.Id}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "Пакеты, которые не работают:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }
}
