using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusPdf.Application;
using NexusPdf.Export;
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

        // Без этой строки Serilog внутри общих с приложением библиотек молча
        // писал в пустоту: журнал заводило только оконное приложение. Разбор
        // сбоя пакетной обработки упирался в отсутствие любых следов.
        Serilog.Log.Logger = NexusPdf.Infrastructure.LoggingSetup.Create("nexuspdfcli-");

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
            Console.Error.WriteLine("Операция не выполнена: " + Explain(ex));
            return 2;
        }
        finally
        {
            await engine.DisposeAsync();
            // Серилог пишет с буферизацией: без явного сброса последние записи
            // перед завершением процесса до файла не доходят.
            await Serilog.Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Понятная причина отказа вместо системного текста.
    ///
    /// Самые частые сбои — файла нет и нет доступа — приходят из .NET на
    /// английском, и программа, которая везде говорит по-русски, вдруг выдавала
    /// «Could not find file» или «Access to the path is denied». Здесь эти
    /// случаи названы по-человечески, а всё прочее остаётся как есть: выдумывать
    /// перевод неизвестной ошибки хуже, чем показать её дословно.
    /// </summary>
    private static string Explain(Exception ex) => ex switch
    {
        FileNotFoundException e => $"файл не найден: {e.FileName}",
        DirectoryNotFoundException => "папка не найдена — проверьте путь.",
        UnauthorizedAccessException => "нет доступа к файлу или папке. " +
            "Файл может быть открыт в другой программе или защищён от записи.",
        IOException e when e.Message.Contains("being used by another process",
            StringComparison.OrdinalIgnoreCase) => "файл занят другой программой — закройте его и повторите.",
        PathTooLongException => "слишком длинный путь к файлу.",
        OutOfMemoryException => "не хватило памяти. Попробуйте обработать файл по частям ключом --pages.",
        _ => ex.Message,
    };

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    /// <summary>
    /// Ключи, которые понимает каждая команда.
    ///
    /// Раньше неизвестный ключ просто игнорировался, и программа делала вид,
    /// что поняла: «export-images --pages 1» молча выгружало все страницы, а
    /// опечатка «--forse» съедала следующий аргумент — путь к файлу — и
    /// превращалась в «нужны вход и выход». Отказ с перечнем допустимых
    /// ключей честнее любого из этих двух исходов.
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownOptions = new(StringComparer.Ordinal)
    {
        ["export-images"] = ["dpi", "format", "password", "force"],
        ["extract-text"] = ["password", "force"],
        ["word"] = ["pages", "ocr", "no-guess-tables", "engine", "lang", "password", "force"],
        ["excel"] = ["pages", "ocr", "no-guess-tables", "dot-decimal", "engine", "lang", "password", "force"],
        ["merge"] = ["force"],
        ["from-images"] = ["force"],
        ["optimize"] = ["force"],
        ["compress"] = ["preset", "dpi", "quality", "keep-fonts", "force"],
        ["protect"] = ["password", "owner-password", "force"],
        ["ocr"] = ["editable", "engine", "lang", "password", "force"],
        ["compare"] = ["text", "password-a", "password-b"],
    };

    /// <summary>Ключ, на который похож ошибочный: подсказка вместо голого отказа.</summary>
    private static string? ClosestOption(string wrong, IEnumerable<string> known)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            var distance = Distance(wrong, candidate);
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }
        // Правка на треть длины — уже не опечатка, а другой ключ.
        return bestDistance <= Math.Max(1, wrong.Length / 3) ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static void RejectUnknownOptions(string verb, Dictionary<string, string> options)
    {
        if (!KnownOptions.TryGetValue(verb, out var known))
            return;
        foreach (var name in options.Keys)
        {
            if (known.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            var hint = ClosestOption(name, known);
            throw new UsageException(
                $"{verb}: ключ --{name} не существует." +
                (hint == null ? "" : $" Возможно, имелся в виду --{hint}.") +
                $" Допустимы: {string.Join(", ", known.Select(k => "--" + k))}.");
        }
    }

    private static async Task<int> RunAsync(PdfiumRenderEngine engine, string verb, string[] rest)
    {
        var ct = CancellationToken.None;
        var (positional, options) = ParseArgs(rest);
        RejectUnknownOptions(verb, options);

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

            case "word":
            case "excel":
            {
                var toWord = verb == "word";
                if (positional.Count != 2)
                    throw new UsageException($"{verb}: нужны <вход.pdf> и <выход.{(toWord ? "docx" : "xlsx")}>.");
                RequireNotExists(positional[1], options);

                // Распознавание сканов по требованию: без него страница без
                // текстового слоя выгрузится пустой, и лучше сказать об этом,
                // чем молча отдать пустышку.
                var wantOcr = options.ContainsKey("ocr");
                ITextRecognizer? recognizer = null;
                OcrService? ocr = null;
                if (wantOcr)
                {
                    recognizer = CreateRecognizer(
                        Get(options, "engine", "paddle"), Get(options, "lang", "cyrillic"));
                    if (!recognizer.IsAvailable)
                        throw new InvalidOperationException(recognizer.UnavailableReason);
                    ocr = new OcrService(recognizer);
                }

                try
                {
                    var document = await OpenAsync(engine, positional[0], options, ct);
                    await using (document)
                    {
                        var pages = ParsePages(
                            options.TryGetValue("pages", out var range) ? range : null,
                            document.Session.Model.Pages.Count);
                        var analysis = new PageAnalysisOptions(
                            DetectWhitespaceTables: !options.ContainsKey("no-guess-tables"),
                            RecognizeScans: wantOcr);
                        var convert = new ConvertService(engine, ocr);

                        if (toWord)
                        {
                            var summary = await convert.ExportToWordAsync(
                                document, positional[1], pages,
                                new WordExportOptions(), analysis, null, ct);
                            Console.WriteLine(
                                $"Готово: страниц {summary.Pages}, абзацев {summary.Paragraphs}, " +
                                $"таблиц {summary.Tables}, картинок {summary.Images}, " +
                                $"ссылок {summary.Links} → {positional[1]}");
                            ReportScans(summary.ScannedPages, summary.RecognizedPages);
                        }
                        else
                        {
                            var summary = await convert.ExportToExcelAsync(
                                document, positional[1], pages,
                                new ExcelExportOptions(DecimalIsComma: !options.ContainsKey("dot-decimal")),
                                analysis, null, ct);
                            Console.WriteLine(
                                $"Готово: листов {summary.Sheets}, таблиц {summary.Tables} " +
                                $"(по линиям {summary.RulingTables}, по расположению {summary.GuessedTables}), " +
                                $"чисел {summary.Numbers} → {positional[1]}");
                            ReportScans(summary.ScannedPages, summary.RecognizedPages);
                        }
                    }
                }
                finally
                {
                    recognizer?.Dispose();
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
                using var ocrEngine = CreateRecognizer(
                    Get(options, "engine", "paddle"), Get(options, "lang", "cyrillic"));
                if (!ocrEngine.IsAvailable)
                    throw new InvalidOperationException(ocrEngine.UnavailableReason);
                Console.WriteLine("Движок: " + ocrEngine.DisplayName);
                var document = await OpenAsync(engine, positional[0], options, ct);
                await using (document)
                {
                    if (await document.PrimaryHandle.GetFormTypeAsync(ct) == 1)
                        Console.Error.WriteLine(
                            "Предупреждение: у документа есть формы AcroForm — в копии со слоем OCR они станут статикой.");
                    var service = new OcrService(ocrEngine);
                    // --editable: тот же режим, что и в программе, — распознанное
                    // становится настоящим правимым текстом вместо невидимого слоя.
                    var editable = options.ContainsKey("editable");
                    var result = await service.RecognizeAsync(document, null,
                        new Progress<OcrProgress>(p =>
                            Console.WriteLine($"Страница {p.PagesDone}/{p.TotalPages}, слов: {p.WordsSoFar}")),
                        ct, editable);
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
                var preset = Get(options, "preset", "smart").ToLowerInvariant();
                var presetKind = preset switch
                {
                    "smart" or "умное" => NexusPdf.Ux.CompressionPresetKind.Smart,
                    "quality" or "бережное" => NexusPdf.Ux.CompressionPresetKind.Quality,
                    "balanced" => NexusPdf.Ux.CompressionPresetKind.Balanced,
                    "max" or "aggressive" => NexusPdf.Ux.CompressionPresetKind.Aggressive,
                    "structure" => NexusPdf.Ux.CompressionPresetKind.Structure,
                    "custom" => NexusPdf.Ux.CompressionPresetKind.Custom,
                    _ => throw new UsageException(
                        "--preset: smart | quality | balanced | max | structure | custom."),
                };
                var dpi = ParseDouble(options, "dpi", 150);
                if (dpi is < 24 or > 600)
                    throw new UsageException("--dpi: допустимы значения 24–600.");
                var quality = (int)ParseDouble(options, "quality", 75);
                if (quality is < 10 or > 100)
                    throw new UsageException("--quality: допустимы значения 10–100.");
                // Явные числа означают «делай как сказано», без пресета.
                if (options.ContainsKey("dpi") || options.ContainsKey("quality"))
                    presetKind = NexusPdf.Ux.CompressionPresetKind.Custom;
                var subsetFonts = !options.ContainsKey("keep-fonts");
                RequireNotExists(positional[1], options);

                NexusPdf.Ux.DocumentImageProfile profile;
                try
                {
                    await using var probe = await engine.OpenAsync(positional[0], null, ct);
                    var summary = await probe.GetImageSummaryAsync(
                        NexusPdf.Ux.DocumentImageProfile.SampleLimit, ct);
                    profile = new NexusPdf.Ux.DocumentImageProfile(
                        probe.Info.PageCount, summary.Images, summary.TextLength,
                        summary.SampledPages, summary.AverageImageDpi);
                }
                catch (PdfPasswordRequiredException)
                {
                    // Пересохранение молча сняло бы шифрование — честный отказ.
                    throw new InvalidOperationException(
                        "Файл защищён паролем: пересжатие сняло бы защиту. Сначала снимите пароль (qpdf --decrypt).");
                }

                var settings = NexusPdf.Ux.CompressionPresets.Resolve(presetKind, profile, dpi, quality);
                Console.WriteLine(
                    $"Режим: {preset}; изображения до {settings.Dpi:0} DPI, качество {settings.Quality}" +
                    (settings.StructureOnly ? " (изображения не трогаются)" : "") +
                    $"; документ {(profile.LooksScanned ? "похож на скан" : "похож на вёрстку")}");

                var before = new FileInfo(positional[0]).Length;
                var compression = new NexusPdf.Pdf.MuPdf.MuPdfCompressionEngine();
                if (!compression.IsAvailable)
                    throw new InvalidOperationException(compression.UnavailableReason);
                var result = await compression.CompressAsync(
                    positional[0], positional[1],
                    new NexusPdf.Pdf.Abstractions.PdfCompressionRequest(
                        settings.Dpi, settings.Quality, settings.StructureOnly, subsetFonts),
                    ct);

                var after = result.BytesAfter;
                Console.WriteLine($"Готово: {before / 1024} КиБ → {after / 1024} КиБ " +
                                  $"({(before - after) * 100.0 / Math.Max(1, before):0.#}% меньше)");
                if (result.KeptOriginal)
                    Console.Error.WriteLine(
                        "Предупреждение: файл не стал меньше — оставлен исходный вариант.");
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

    /// <summary>
    /// Движок распознавания по --engine/--lang. По умолчанию PaddleOCR — тот же,
    /// что и в приложении; молчаливой подмены на Tesseract нет: если пакет не
    /// установлен, команда честно падает с причиной, а не читает другим языком.
    /// </summary>
    private static NexusPdf.Ocr.ITextRecognizer CreateRecognizer(string engineId, string languagePack)
    {
        if (string.Equals(engineId, "tesseract", StringComparison.OrdinalIgnoreCase))
            return new TesseractOcrEngine();
        if (!string.Equals(engineId, "paddle", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("ocr: --engine принимает paddle или tesseract.");

        var known = NexusPdf.Ocr.Paddle.PaddleOcrEngine.Catalog;
        if (known.Count > 0 && !known.Any(p => string.Equals(p.Id, languagePack, StringComparison.OrdinalIgnoreCase)))
            throw new UsageException(
                "ocr: неизвестный --lang. Доступны: " + string.Join(", ", known.Select(p => p.Id)));
        return new NexusPdf.Ocr.Paddle.PaddleOcrEngine(AppContext.BaseDirectory, languagePack);
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
                if (name is "force" or "text" or "ocr" or "no-guess-tables" or "dot-decimal" or "editable")
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
    private static byte[] EncodeJpegRaw(
        byte[] bgra, int width, int height, NexusPdf.Pdf.Abstractions.ImageEncodingChoice choice)
    {
        BitmapSource source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        if (choice.IsGray)
        {
            var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            source = gray;
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = choice.Quality };
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

    /// <summary>Диапазон страниц из --pages или null (весь документ).</summary>
    private static IReadOnlyList<int>? ParsePages(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parsed = NexusPdf.Printing.PageRangeParser.Parse(text, pageCount);
        if (parsed.Error != null)
            throw new UsageException("--pages: " + parsed.Error);
        return parsed.Indices;
    }

    /// <summary>
    /// Сказать про страницы без текстового слоя. Пустые листы в результате
    /// выглядят как «в PDF ничего не было» — а это непрочитанный скан.
    /// </summary>
    private static void ReportScans(int scanned, int recognized)
    {
        if (scanned == 0) return;
        if (recognized > 0)
            Console.WriteLine($"Распознано страниц-сканов: {recognized} из {scanned}.");
        else
            Console.Error.WriteLine(
                $"Внимание: страниц без текстового слоя {scanned} — они пустые. " +
                "Добавьте --ocr, чтобы распознать их.");
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
  word          <вход.pdf> <выход.docx> [--pages 1-5] [--ocr] [--no-guess-tables]
      Экспорт в Word: абзацы, таблицы, ссылки, примечания, картинки.
      Разметка ВОССТАНАВЛИВАЕТСЯ по расположению текста — в PDF её нет.
  excel         <вход.pdf> <выход.xlsx> [--pages 1-5] [--ocr] [--dot-decimal]
      Экспорт таблиц в Excel: числа числами, ссылки живыми. Таблицы берутся
      по нарисованным границам, а без них — по просветам между колонками.
      --ocr распознаёт страницы-сканы, иначе они выгружаются пустыми.
  merge         <выход.pdf> <а.pdf> <б.pdf> [...]
      Объединение PDF-файлов в порядке перечисления.
  from-images   <выход.pdf> <img1> <img2> [...]
      Сборка PDF из изображений (PNG/JPEG/BMP/TIFF; каждая — страница).
  optimize      <вход.pdf> <выход.pdf>
      Структурная оптимизация без потерь (qpdf) + линеаризация.
  compress      <вход.pdf> <выход.pdf> [--preset smart] [--dpi 150] [--quality 75] [--keep-fonts]
      Сжатие С ПОТЕРЯМИ: изображения выше целевого DPI уменьшаются и
      пересжимаются в JPEG. Прозрачные и факсовые (CCITT/JBIG2/JPX)
      пропускаются; защищённые паролем файлы — честный отказ.
      --preset: smart | quality | balanced | max | structure | custom.
      Явные --dpi или --quality означают «делай как сказано» и отменяют пресет.
      --keep-fonts оставляет шрифты целиком, без вырезания неиспользуемых глифов.
  protect       <вход.pdf> <выход.pdf> --password X [--owner-password Y]
      Защищённая копия (AES-256). Пароль в командной строке виден другим
      процессам и попадает в историю — надёжнее задать его переменной
      окружения NEXUSPDF_PASSWORD и не указывать --password.
  ocr           <вход.pdf> <выход.pdf> [--editable] [--engine paddle|tesseract] [--lang ID] [--password X]
      Распознавание сканов: невидимый текстовый слой.
      --editable — распознанное становится настоящим правимым текстом:
      гарнитура, насыщенность и цвет подбираются под оригинал, а фон под
      строкой восстанавливается по бумаге вокруг неё.
      По умолчанию PaddleOCR, пакет cyrillic (кириллица+латиница+греческий);
      список пакетов покажет неверный --lang. --engine tesseract — rus+eng.
      Формы AcroForm в копии становятся статикой (выводится предупреждение).
  compare       <первый.pdf> <второй.pdf> [--text] [--password-a X] [--password-b Y]
      Визуальное постраничное сравнение. Код возврата 0 — идентичны,
      3 — есть отличия (постраничный отчёт в stdout). --text добавляет
      пословные текстовые отличия изменённых страниц (- удалено, + добавлено).

Общее: --force — перезаписать существующие файлы результата (в том числе
картинки страниц у export-images). Ключ, которого у команды нет, — ошибка
аргументов, а не молчаливое умолчание. merge и from-images переносят страницы
и аннотации, но не закладки/формы/вложения исходников.
Коды возврата: 0 — успех, 1 — ошибка аргументов, 2 — ошибка операции,
3 — compare нашёл отличия.
""");
    }
}
