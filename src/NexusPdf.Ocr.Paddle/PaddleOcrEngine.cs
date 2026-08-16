using NexusPdf.Ocr;
using NexusPdf.Pdf.Abstractions;
using RapidOcrNet;

namespace NexusPdf.Ocr.Paddle;

/// <summary>
/// Распознавание текста PaddleOCR через RapidOcrNet (ONNX Runtime, офлайн).
/// Детектор строк общий для всех языков, распознаватель выбирается языковым
/// пакетом: набор символов задаётся ИМЕННО словарём пакета, поэтому модель и
/// словарь всегда берутся парой.
/// Экземпляр RapidOcr не потокобезопасен — вызовы сериализуются общим замком.
/// </summary>
public sealed class PaddleOcrEngine : ITextRecognizer
{
    public string Id => "paddle";

    /// <summary>
    /// Название движка. БЕЗ языкового пакета: его заголовки живут в
    /// ocrmodels.lock.json и существуют только по-русски, поэтому в английском
    /// интерфейсе строка получалась смешанной. Какой пакет выбран, видно в
    /// окне распознавания — там ему и место.
    /// </summary>
    public string DisplayName => "PaddleOCR PP-OCRv6";

    /// <summary>
    /// То же с языковым пакетом — для ЖУРНАЛА: там нужно видеть, чем именно
    /// читали, и язык записи значения не имеет.
    /// </summary>
    public string LogName =>
        DisplayName + " — " +
        (Catalog.FirstOrDefault(p => string.Equals(p.Id, _packId, StringComparison.OrdinalIgnoreCase))?.Title
         ?? _packId);

    private readonly string? _modelsDir;
    private readonly string _packId;
    private readonly object _gate = new();
    private RapidOcr? _ocr;
    private bool _initFailed;
    private string? _initError;
    private bool _disposed;

    public PaddleOcrEngine(string packId = "cyrillic") : this(AppContext.BaseDirectory, packId) { }

    public PaddleOcrEngine(string baseDirectory, string packId)
    {
        _packId = packId;
        _modelsDir = ResolveModelsDir(baseDirectory);
    }

    public bool IsAvailable => _modelsDir != null && !_initFailed && FindRecognizer() != null;

    public string? UnavailableReason => IsAvailable
        ? null
        : _modelsDir == null
            ? "Модели OCR не найдены. Запустите tools/fetch-ocrmodels.ps1 или переустановите приложение."
            : _initError ?? "Языковой пакет распознавания не установлен.";

    /// <summary>Поиск tools\ocrmodels рядом с приложением и до шести уровней вверх — как у qpdf.</summary>
    private static string? ResolveModelsDir(string baseDirectory)
    {
        var dir = new DirectoryInfo(baseDirectory);
        for (var depth = 0; dir != null && depth < 7; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ocrmodels");
            if (File.Exists(Path.Combine(candidate, "PP-OCRv6_det_medium.onnx")))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Файлы распознавателя выбранного пакета берутся ИЗ КАТАЛОГА, а не
    /// угадываются по имени: у половины пакетов идентификатор не совпадает с
    /// именем файла (chinese против ch_, japanese против japan_, kannada
    /// против ka_), и угадывание молча находило бы не ту модель или ничего.
    /// </summary>
    private (string Model, string Dict)? FindRecognizer()
    {
        if (_modelsDir == null) return null;
        var pack = Catalog.FirstOrDefault(p =>
            string.Equals(p.Id, _packId, StringComparison.OrdinalIgnoreCase));
        if (pack == null) return null;

        var model = Path.Combine(_modelsDir, pack.ModelFile);
        var dict = Path.Combine(_modelsDir, pack.DictFile);
        return File.Exists(model) && File.Exists(dict) ? (model, dict) : null;
    }

    /// <summary>Языковой пакет распознавания: идентификатор, название и его файлы.</summary>
    public sealed record LanguagePack(
        string Id, string Title, string Languages, bool IsDefault, string ModelFile, string DictFile)
    {
        // Без этого экранный диктор читал бы весь дамп записи вместо названия:
        // DisplayMemberPath меняет только картинку, но не имя для UI Automation.
        public override string ToString() => Title;
    }

    private static IReadOnlyList<LanguagePack>? _catalog;

    /// <summary>Каталог языковых пакетов из tools/ocrmodels.lock.json рядом с моделями.</summary>
    public static IReadOnlyList<LanguagePack> Catalog => _catalog ??= LoadCatalog();

    private static IReadOnlyList<LanguagePack> LoadCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "ocrmodels.lock.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var packs = new List<LanguagePack>();
                foreach (var p in json.RootElement.GetProperty("packs").EnumerateArray())
                {
                    packs.Add(new LanguagePack(
                        p.GetProperty("id").GetString() ?? "",
                        p.GetProperty("title").GetString() ?? "",
                        p.GetProperty("languages").GetString() ?? "",
                        p.GetProperty("isDefault").GetBoolean(),
                        p.GetProperty("model").GetProperty("name").GetString() ?? "",
                        p.GetProperty("dict").GetProperty("name").GetString() ?? ""));
                }
                return packs;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Не удалось прочитать каталог языковых пакетов OCR");
                return Array.Empty<LanguagePack>();
            }
        }
        return Array.Empty<LanguagePack>();
    }

    /// <summary>Пакеты, файлы которых реально загружены и готовы к работе.</summary>
    public static IReadOnlyList<LanguagePack> InstalledPacks(string baseDirectory)
    {
        var dir = ResolveModelsDir(baseDirectory);
        if (dir == null) return Array.Empty<LanguagePack>();
        return Catalog
            .Where(p => File.Exists(Path.Combine(dir, p.ModelFile))
                     && File.Exists(Path.Combine(dir, p.DictFile)))
            .ToList();
    }

    /// <summary>
    /// Классификатор поворота строк скачивается в наш каталог моделей вместе с
    /// остальными. Комплектный файл пакета RapidOcrNet намеренно не
    /// используется: он кладётся рядом со СВОЕЙ сборкой и до потребителей не
    /// доезжает — на этом движок молча не поднимался.
    /// </summary>
    private string? FindClassifier() =>
        _modelsDir == null
            ? null
            : Directory.EnumerateFiles(_modelsDir, "*cls*.onnx").FirstOrDefault();

    /// <param name="renderDpi">
    /// Не используется: PaddleOCR сам приводит строки к нужному размеру.
    /// Параметр есть ради общего контракта с Tesseract, которому DPI важен.
    /// </param>
    public Task<OcrPageResult> RecognizeAsync(RenderedPageImage image, int renderDpi, CancellationToken ct) =>
        Task.Run(() => Recognize(image, ct), ct);

    private OcrPageResult Recognize(RenderedPageImage image, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // Молча вернуть пустой результат нельзя: снаружи это выглядит как
            // «страница пустая», хотя на деле движок не поднялся.
            var ocr = EnsureEngine()
                ?? throw new InvalidOperationException(_initError ?? "Движок PaddleOCR недоступен.");

            using var bitmap = ToSkBitmap(image);
            var result = ocr.Detect(bitmap, RapidOcrOptions.Default);

            var words = new List<OcrWord>();
            var scores = new List<float>();
            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                var score = block.CharScores is { Length: > 0 } ? block.CharScores.Average() : 0f;
                scores.Add(score);

                var xs = block.BoxPoints.Select(p => (double)p.X).ToList();
                var ys = block.BoxPoints.Select(p => (double)p.Y).ToList();
                words.Add(new OcrWord(
                    block.Text.Trim(),
                    xs.Min(), ys.Min(),
                    xs.Max() - xs.Min(), ys.Max() - ys.Min(),
                    score * 100f));
            }

            return new OcrPageResult(words, scores.Count > 0 ? scores.Average() * 100f : 0f);
        }
    }

    private RapidOcr? EnsureEngine()
    {
        if (_ocr != null) return _ocr;
        if (_initFailed) return null;

        var files = FindRecognizer();
        if (files == null)
        {
            _initFailed = true;
            _initError = "Языковой пакет распознавания не установлен.";
            return null;
        }

        var cls = FindClassifier();
        if (cls == null)
        {
            _initFailed = true;
            _initError = "Не найден классификатор поворота строк из комплекта RapidOcrNet.";
            return null;
        }

        try
        {
            var ocr = new RapidOcr();
            var det = Path.Combine(_modelsDir!, "PP-OCRv6_det_medium.onnx");
            ocr.InitModels(det, cls, files.Value.Model, files.Value.Dict);
            _ocr = ocr;
            return ocr;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _initError = "Движок PaddleOCR не инициализировался: " + ex.Message;
            return null;
        }
    }

    /// <summary>BGRA-растр страницы → SKBitmap, который ждёт RapidOcrNet.</summary>
    private static SkiaSharp.SKBitmap ToSkBitmap(RenderedPageImage image)
    {
        var info = new SkiaSharp.SKImageInfo(
            image.PixelWidth, image.PixelHeight,
            SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        var bitmap = new SkiaSharp.SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(
            image.Bgra, 0, bitmap.GetPixels(), image.Bgra.Length);
        return bitmap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _ocr?.Dispose();
            _ocr = null;
        }
    }
}
