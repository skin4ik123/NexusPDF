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
                var format = Get(options, "format", "png").ToLowerInvariant();
                if (format is not ("png" or "jpeg" or "jpg"))
                    throw new UsageException("--format: поддерживаются png и jpeg.");
                var document = await OpenAsync(engine, positional[0], options, ct);
                await using (document)
                {
                    Directory.CreateDirectory(positional[1]);
                    var baseName = Path.GetFileNameWithoutExtension(positional[0]);
                    var extension = format == "png" ? "png" : "jpg";
                    var count = await new ConvertService(engine).ExportImagesAsync(
                        document, null, dpi,
                        async (image, pageIndex, token) =>
                        {
                            var path = Path.Combine(positional[1], $"{baseName}-{pageIndex + 1:000}.{extension}");
                            await File.WriteAllBytesAsync(path, Encode(image, format, dpi), token);
                            Console.WriteLine(path);
                        },
                        null, ct);
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
                await qpdf.OptimizeAsync(positional[0], positional[1], linearize: true, ct);
                var after = new FileInfo(positional[1]).Length;
                Console.WriteLine($"Готово: {before / 1024} КиБ → {after / 1024} КиБ ({positional[1]})");
                return 0;
            }

            case "protect":
            {
                if (positional.Count != 2)
                    throw new UsageException("protect: нужны <вход.pdf> и <выход.pdf>.");
                var password = Get(options, "password", "");
                if (password.Length == 0)
                    throw new UsageException("protect: обязателен --password <пароль>.");
                RequireNotExists(positional[1], options);
                var qpdf = RequireQpdf();
                var owner = Get(options, "owner-password", "");
                await qpdf.EncryptAsync(positional[0], positional[1], password,
                    owner.Length > 0 ? owner : null, ct);
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
                    var service = new OcrService(ocrEngine);
                    var result = await service.RecognizeAsync(document, null,
                        new Progress<OcrProgress>(p =>
                            Console.WriteLine($"Страница {p.PagesDone}/{p.TotalPages}, слов: {p.WordsSoFar}")),
                        ct);
                    if (result.Error != null)
                        throw new InvalidOperationException(result.Error);
                    await new SaveService(engine).SaveCopyAsync(document, positional[1], ct);
                    Console.WriteLine(
                        $"Готово: распознано страниц {result.PagesRecognized}, слов {result.WordCount}, " +
                        $"пропущено с текстом {result.PagesSkippedWithText} → {positional[1]}");
                }
                return 0;
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

    private static (List<string> Positional, Dictionary<string, string> Options) ParseArgs(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i][2..];
                // Флаги без значения (--force); значение — следующий аргумент.
                if (name is "force")
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

    private static double ParseDouble(Dictionary<string, string> options, string name, double fallback)
    {
        if (!options.TryGetValue(name, out var raw))
            return fallback;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new UsageException($"--{name}: «{raw}» не число.");
        return value;
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

    /// <summary>Файл изображения → страница PDF (размер по DPI из метаданных, запасной вариант 96).</summary>
    private static ImagePageSpec DecodeImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 24_000_000)
            throw new InvalidOperationException($"«{Path.GetFileName(path)}» больше 24 мегапикселей.");
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var dpiX = frame.DpiX > 1 ? frame.DpiX : 96;
        var dpiY = frame.DpiY > 1 ? frame.DpiY : 96;
        return new ImagePageSpec(
            pixels, frame.PixelWidth, frame.PixelHeight,
            frame.PixelWidth / dpiX * 72.0, frame.PixelHeight / dpiY * 72.0);
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
  protect       <вход.pdf> <выход.pdf> --password X [--owner-password Y]
      Защищённая копия (AES-256).
  ocr           <вход.pdf> <выход.pdf> [--password X]
      Распознавание сканов (rus+eng): невидимый текстовый слой.

Общее: --force — перезаписать существующий файл результата.
Коды возврата: 0 — успех, 1 — ошибка аргументов, 2 — ошибка операции.
""");
    }
}
