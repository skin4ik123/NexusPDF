using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusPdf.Application;
using NexusPdf.Ocr;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;

namespace NexusPdf.Cli;

/// <summary>
/// Консольные операции NexusPDF — те же движки, что и в приложении.
/// Коды возврата: 0 — успех, 1 — ошибка аргументов, 2 — ошибка операции.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var engine = new PdfiumRenderEngine();
        try
        {
            return await RunAsync(engine, args[0].ToLowerInvariant(), args.Skip(1).ToArray());
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            Console.Error.WriteLine("Запустите NexusPdfCli --help для справки.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Операция не выполнена: " + ex.Message);
            return 2;
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    private static async Task<int> RunAsync(PdfiumRenderEngine engine, string verb, string[] rest)
    {
        var ct = CancellationToken.None;
        var (positional, options) = ParseArgs(rest);

        switch (verb)
        {
            case "export-images":
            {
                if (positional.Count != 2)
                    throw new UsageException("export-images: нужны <вход.pdf> и <папка-результата>.");
                var dpi = ParseDouble(options, "dpi", 150);
                if (dpi is < 24 or > 1200)
                    throw new UsageException("--dpi: допустимы значения 24–1200.");
                var format = Get(options, "format", "png").ToLowerInvariant();
                if (format is not ("png" or "jpeg" or "jpg"))
                    throw new UsageException("--format: поддерживаются png и jpeg.");
                var document = await OpenAsync(engine, positional[0], options, ct);
                await using (document)
                {
                    Directory.CreateDirectory(positional[1]);
                    var baseName = Path.GetFileNameWithoutExtension(positional[0]);
                    var extension = format == "png" ? "png" : "jpg";
                    // Контракт --force распространяется и на файлы страниц.
                    var mask = $"{baseName}-*.{extension}";
                    if (!options.ContainsKey("force") &&
                        Directory.EnumerateFiles(positional[1], mask).Any())
                        throw new UsageException(
                            $"В «{positional[1]}» уже есть файлы {mask}. Добавьте --force для перезаписи.");
                    var dpiWarned = false;
                    var count = await new ConvertService(engine).ExportImagesAsync(
                        document, null, dpi,
                        async (image, pageIndex, effectiveDpi, token) =>
                        {
                            if (!dpiWarned && effectiveDpi < dpi - 0.5)
                            {
                                dpiWarned = true;
                                Console.Error.WriteLine(
                                    $"Предупреждение: гигантские страницы урезаны до {Math.Round(effectiveDpi)} DPI (предел стороны растра).");
                            }
                            var path = Path.Combine(positional[1], $"{baseName}-{pageIndex + 1:000}.{extension}");
                            await File.WriteAllBytesAsync(path, Encode(image, format, effectiveDpi), token);
                            Console.WriteLine(path);
                        },
                        null, ct);
                    // Устаревшие остатки прошлого прогона (страниц стало меньше)
                    // убираются только при --force: без него мы сюда не попадаем
                    // при существующих файлах.
                    foreach (var stale in Directory.EnumerateFiles(positional[1], mask))
                    {
                        var name = Path.GetFileNameWithoutExtension(stale);
                        var suffix = name[(baseName.Length + 1)..];
                        if (int.TryParse(suffix, out var n) && n > count)
                            File.Delete(stale);
                    }
                    Console.WriteLine($"Готово: {count} страниц → {positional[1]}");
                }
                return 0;
            }

            case "extract-text":
            {
                if (positional.Count != 2)
                    throw new UsageException("extract-text: нужны <вход.pdf> и <выход.txt>.");
                RequireNotExists(positional[1], options);
                var document = await OpenAsync(engine, positional[0], options, ct);
                await using (document)
                {
                    var text = await new ConvertService(engine).ExtractTextAsync(document, ct);
                    await File.WriteAllTextAsync(positional[1], text, new UTF8Encoding(true), ct);
                    Console.WriteLine($"Готово: текст сохранён в {positional[1]}");
                }
                return 0;
            }

            case "merge":
            {
                if (positional.Count < 3)
                    throw new UsageException("merge: нужны <выход.pdf> и минимум два входных PDF.");
                RequireNotExists(positional[0], options);
                var pages = await new ConvertService(engine)
                    .MergeAsync(positional.Skip(1).ToList(), positional[0], ct);
                Console.WriteLine($"Готово: {pages} страниц → {positional[0]}");
                return 0;
            }

            case "from-images":
            {
                if (positional.Count < 2)
                    throw new UsageException("from-images: нужны <выход.pdf> и минимум одно изображение.");
                RequireNotExists(positional[0], options);
                var specs = positional.Skip(1).Select(DecodeImage).ToList();
                await new ConvertService(engine).CreateFromImagesAsync(specs, positional[0], ct);
                Console.WriteLine($"Готово: {specs.Count} страниц → {positional[0]}");
                return 0;
            }

            case "optimize":
            {
                if (positional.Count != 2)
                    throw new UsageException("optimize: нужны <вход.pdf> и <выход.pdf>.");
                RequireNotExists(positional[1], options);
                var qpdf = RequireQpdf();
                var before = new FileInfo(positional[0]).Length;
                await RunQpdfSafelyAsync(positional[1],
                    tmp => qpdf.OptimizeAsync(positional[0], tmp, linearize: true, ct));
                var after = new FileInfo(positional[1]).Length;
                Console.WriteLine($"Готово: {before / 1024} КиБ → {after / 1024} КиБ ({positional[1]})");
                return 0;
            }

            case "protect":
            {
                if (positional.Count != 2)
                    throw new UsageException("protect: нужны <вход.pdf> и <выход.pdf>.");
                // Пароль в командной строке виден в истории и списке процессов —
                // NEXUSPDF_PASSWORD надёжнее (см. --help).
                var password = Get(options, "password",
                    Environment.GetEnvironmentVariable("NEXUSPDF_PASSWORD") ?? "");
                if (password.Length == 0)
                    throw new UsageException(
                        "protect: укажите --password <пароль> или переменную окружения NEXUSPDF_PASSWORD.");
                RequireNotExists(positional[1], options);
                var qpdf = RequireQpdf();
                var owner = Get(options, "owner-password", "");
                await RunQpdfSafelyAsync(positional[1],
                    tmp => qpdf.EncryptAsync(positional[0], tmp, password,
                        owner.Length > 0 ? owner : null, ct));
                Console.WriteLine($"Готово: защищённая копия {positional[1]} (AES-256)");
                return 0;
            }

            case "ocr":
            {
                if (positional.Count != 2)
                    throw new UsageException("ocr: нужны <вход.pdf> и <выход.pdf>.");
                RequireNotExists(positional[1], options);
                using var ocrEngine = new TesseractOcrEngine();
                if (!ocrEngine.IsAvailable)
                    throw new InvalidOperationException(ocrEngine.UnavailableReason);
                var document = await OpenAsync(engine, positional[0], options, ct);
                await using (document)
                {
                    if (await document.PrimaryHandle.GetFormTypeAsync(ct) == 1)
                        Console.Error.WriteLine(
                            "Предупреждение: у документа есть формы AcroForm — в копии со слоем OCR они станут статикой.");
                    var service = new OcrService(ocrEngine);
                    var result = await service.RecognizeAsync(document, null,
                        new Progress<OcrProgress>(p =>
                            Console.WriteLine($"Страница {p.PagesDone}/{p.TotalPages}, слов: {p.WordsSoFar}")),
                        ct);
                    if (result.Error != null)
                        throw new InvalidOperationException(result.Error);
                    if (result.PagesRecognized == 0)
                    {
                        // Пересобирать файл без единого нового слоя незачем.
                        Console.WriteLine(
                            $"Распознавать нечего (пропущено с текстом: {result.PagesSkippedWithText}, " +
                            $"без читаемого текста: {result.PagesWithoutWords}) — файл не создан.");
                        return 0;
                    }
                    await new SaveService(engine).SaveCopyAsync(document, positional[1], ct);
                    Console.WriteLine(
                        $"Готово: распознано страниц {result.PagesRecognized}, слов {result.WordCount}, " +
                        $"пропущено с текстом {result.PagesSkippedWithText} → {positional[1]}");
                }
                return 0;
            }

            case "compress":
            {
                if (positional.Count != 2)
                    throw new UsageException("compress: нужны <вход.pdf> и <выход.pdf>.");
                var dpi = ParseDouble(options, "dpi", 150);
                if (dpi is < 24 or > 600)
                    throw new UsageException("--dpi: допустимы значения 24–600.");
                var quality = (int)ParseDouble(options, "quality", 75);
                if (quality is < 10 or > 100)
                    throw new UsageException("--quality: допустимы значения 10–100.");
                RequireNotExists(positional[1], options);
                try
                {
                    await using var probe = await engine.OpenAsync(positional[0], null, ct);
                }
                catch (PdfPasswordRequiredException)
                {
                    // SaveAsCopy молча снял бы шифрование — честный отказ.
                    throw new InvalidOperationException(
                        "Файл защищён паролем: пересжатие сняло бы защиту. Сначала снимите пароль (qpdf --decrypt).");
                }

                var before = new FileInfo(positional[0]).Length;
                var tmp = positional[1] + ".nexustmp";
                NexusPdf.Pdf.Abstractions.ImageRecompressStats stats;
                try
                {
                    stats = await engine.RecompressImagesAsync(positional[0], null, tmp, dpi,
                        (bgra, w, h) => EncodeJpegRaw(bgra, w, h, quality), ct);
                    File.Move(tmp, positional[1], overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* лучшая попытка */ }
                }
                var after = new FileInfo(positional[1]).Length;
                Console.WriteLine(
                    $"Готово: {before / 1024} КиБ → {after / 1024} КиБ; " +
                    $"изображений пересжато {stats.Recompressed}, пропущено {stats.Skipped}");
                if (after >= before)
                    Console.Error.WriteLine("Предупреждение: файл не стал меньше — исходные изображения уже компактны.");
                return 0;
            }

            case "compare":
            {
                if (positional.Count != 2)
                    throw new UsageException("compare: нужны <первый.pdf> и <второй.pdf>.");
                CompareSession session;
                try
                {
                    session = await CompareSession.OpenAsync(engine,
                        positional[0], NullIfEmpty(Get(options, "password-a", "")),
                        positional[1], NullIfEmpty(Get(options, "password-b", "")),
                        ct);
                }
                catch (PdfPasswordRequiredException)
                {
                    throw new InvalidOperationException(
                        "Один из файлов защищён паролем: укажите --password-a / --password-b.");
                }
                await using (session)
                {
                    var showText = options.ContainsKey("text");
                    var summary = await session.AnalyzeAsync(null, ct);
                    foreach (var page in summary.Pages)
                    {
                        var status = page switch
                        {
                            { OnlyInFirst: true } => "только в первом файле",
                            { OnlyInSecond: true } => "только во втором файле",
                            { SizeMismatch: true } => $"размер страницы отличается (визуально {page.DiffPercent:0.##}%)",
                            { IsDifferent: true } => $"отличия {page.DiffPercent:0.##}%",
                            _ => "одинаковые",
                        };
                        Console.WriteLine($"Страница {page.PageIndex + 1}: {status}");
                        if (showText && page.IsDifferent)
                        {
                            var fragments = await session.GetPageTextDiffAsync(page.PageIndex, ct);
                            foreach (var fragment in fragments)
                            {
                                switch (fragment.Kind)
                                {
                                    case TextDiffKind.Removed:
                                        Console.WriteLine($"  - {fragment.Text}");
                                        break;
                                    case TextDiffKind.Added:
                                        Console.WriteLine($"  + {fragment.Text}");
                                        break;
                                    case TextDiffKind.TooLong:
                                        Console.WriteLine("  (страница слишком длинная для пословного сравнения)");
                                        break;
                                }
                            }
                        }
                    }
                    Console.WriteLine(summary.DifferentPages == 0
                        ? "Итог: документы визуально идентичны."
                        : $"Итог: отличаются {summary.DifferentPages} из {summary.Pages.Count} страниц.");
                    return summary.DifferentPages == 0 ? 0 : 3;
                }
            }

            default:
                throw new UsageException($"Неизвестная команда «{verb}».");
        }
    }

    // ----- Вспомогательные -----

    private static async Task<OpenedDocument> OpenAsync(
        PdfiumRenderEngine engine, string path, Dictionary<string, string> options, CancellationToken ct)
    {
        try
        {
            return await OpenedDocument.OpenAsync(engine, path, Get(options, "password", "") is { Length: > 0 } p ? p : null, ct);
        }
        catch (PdfPasswordRequiredException)
        {
            throw new InvalidOperationException(
                $"«{Path.GetFileName(path)}» защищён паролем: укажите --password <пароль>.");
        }
    }

    private static QpdfEngine RequireQpdf()
    {
        var qpdf = new QpdfEngine();
        if (!qpdf.IsAvailable)
            throw new InvalidOperationException(qpdf.UnavailableReason);
        return qpdf;
    }

    private static void RequireNotExists(string path, Dictionary<string, string> options)
    {
        if (File.Exists(path) && !options.ContainsKey("force"))
            throw new UsageException($"«{path}» уже существует. Добавьте --force для перезаписи.");
    }

    /// <summary>
    /// qpdf пишет прямо в целевой путь и при сбое/таймауте оставил бы вместо
    /// прежнего корректного файла обрубок. Запись идёт во временный файл;
    /// цель подменяется только при успехе.
    /// </summary>
    private static async Task RunQpdfSafelyAsync(string targetPath, Func<string, Task> operation)
    {
        var tmp = targetPath + ".nexustmp-qpdf";
        try
        {
            await operation(tmp);
            File.Move(tmp, targetPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* лучшая попытка */ }
        }
    }

    private static (List<string> Positional, Dictionary<string, string> Options) ParseArgs(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i][2..];
                // Флаги без значения; значение — следующий аргумент.
                if (name is "force" or "text")
                    options[name] = "1";
                else if (i + 1 < args.Length)
                    options[name] = args[++i];
                else
                    throw new UsageException($"--{name}: не указано значение.");
            }
            else
            {
                positional.Add(args[i]);
            }
        }
        return (positional, options);
    }

    private static string Get(Dictionary<string, string> options, string name, string fallback) =>
        options.TryGetValue(name, out var value) ? value : fallback;

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    private static double ParseDouble(Dictionary<string, string> options, string name, double fallback)
    {
        if (!options.TryGetValue(name, out var raw))
            return fallback;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new UsageException($"--{name}: «{raw}» не число.");
        return value;
    }

    /// <summary>BGRA-растр → JPEG с заданным качеством (для пересжатия изображений).</summary>
    private static byte[] EncodeJpegRaw(byte[] bgra, int width, int height, int quality)
    {
        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>BGRA-растр страницы → PNG/JPEG через WPF-кодеки.</summary>
    private static byte[] Encode(RenderedPageImage image, string format, double dpi)
    {
        var source = BitmapSource.Create(
            image.PixelWidth, image.PixelHeight, dpi, dpi,
            PixelFormats.Bgra32, null, image.Bgra, image.Stride);
        BitmapEncoder encoder = format == "png"
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Файл изображения → страница PDF (размер по DPI из метаданных, запасной
    /// вариант 96). Фото больше 24 Мп ужимаются — как и в приложении.
    /// </summary>
    private static ImagePageSpec DecodeImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frame = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var dpiX = frame.DpiX > 1 ? frame.DpiX : 96;
        var dpiY = frame.DpiY > 1 ? frame.DpiY : 96;
        var widthPoints = frame.PixelWidth / dpiX * 72.0;
        var heightPoints = frame.PixelHeight / dpiY * 72.0;

        BitmapSource decoded = frame;
        var totalPixels = (double)frame.PixelWidth * frame.PixelHeight;
        const double maxPixels = 24_000_000;
        if (totalPixels > maxPixels)
        {
            var k = Math.Sqrt(maxPixels / totalPixels);
            var scaled = new TransformedBitmap(frame, new ScaleTransform(k, k));
            scaled.Freeze();
            decoded = scaled;
        }

        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * (long)converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new ImagePageSpec(
            pixels, converted.PixelWidth, converted.PixelHeight, widthPoints, heightPoints);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
NexusPdfCli — консольные операции NexusPDF (локально, без интернета).

Команды:
  export-images <вход.pdf> <папка>   [--dpi 150] [--format png|jpeg] [--password X]
      Экспорт каждой страницы в изображение.
  extract-text  <вход.pdf> <выход.txt> [--password X]
      Извлечение всего текста документа.
  merge         <выход.pdf> <а.pdf> <б.pdf> [...]
      Объединение PDF-файлов в порядке перечисления.
  from-images   <выход.pdf> <img1> <img2> [...]
      Сборка PDF из изображений (PNG/JPEG/BMP/TIFF; каждая — страница).
  optimize      <вход.pdf> <выход.pdf>
      Структурная оптимизация без потерь (qpdf) + линеаризация.
  compress      <вход.pdf> <выход.pdf> [--dpi 150] [--quality 75]
      Сжатие С ПОТЕРЯМИ: изображения выше целевого DPI уменьшаются и
      пересжимаются в JPEG. Прозрачные и факсовые (CCITT/JBIG2/JPX)
      пропускаются; защищённые паролем файлы — честный отказ.
  protect       <вход.pdf> <выход.pdf> --password X [--owner-password Y]
      Защищённая копия (AES-256). Пароль в командной строке виден другим
      процессам и попадает в историю — надёжнее задать его переменной
      окружения NEXUSPDF_PASSWORD и не указывать --password.
  ocr           <вход.pdf> <выход.pdf> [--password X]
      Распознавание сканов (rus+eng): невидимый текстовый слой.
      Формы AcroForm в копии становятся статикой (выводится предупреждение).
  compare       <первый.pdf> <второй.pdf> [--text] [--password-a X] [--password-b Y]
      Визуальное постраничное сравнение. Код возврата 0 — идентичны,
      3 — есть отличия (постраничный отчёт в stdout). --text добавляет
      пословные текстовые отличия изменённых страниц (- удалено, + добавлено).

Общее: --force — перезаписать существующие файлы результата (в том числе
картинки страниц у export-images). merge и from-images переносят страницы
и аннотации, но не закладки/формы/вложения исходников.
Коды возврата: 0 — успех, 1 — ошибка аргументов, 2 — ошибка операции,
3 — compare нашёл отличия.
""");
    }
}
