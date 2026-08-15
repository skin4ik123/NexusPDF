using NexusPdf.Pdf.Abstractions;
using Tesseract;

namespace NexusPdf.Ocr;

/// <summary>Распознанное слово: текст, рамка в пикселях растра (от левого верхнего угла) и уверенность 0–100.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height, float Confidence);

public sealed record OcrPageResult(IReadOnlyList<OcrWord> Words, float MeanConfidence);

/// <summary>
/// Распознавание текста Tesseract (LSTM, rus+eng). Языковые модели ищутся в
/// tools\tessdata рядом с приложением и вверх по каталогам — как qpdf.
/// Пока моделей нет — движок честно недоступен, кнопка OCR не показывается.
/// TesseractEngine не потокобезопасен: все вызовы сериализуются общим замком.
/// </summary>
public sealed class TesseractOcrEngine : ITextRecognizer
{
    public const string Languages = "rus+eng";

    public string Id => "tesseract";

    public string DisplayName => "Tesseract 5 (быстрый)";

    private readonly string? _tessdataPath;
    private readonly object _gate = new();
    private TesseractEngine? _engine;
    private bool _engineFailed;
    private bool _disposed;

    public TesseractOcrEngine() : this(AppContext.BaseDirectory) { }

    public TesseractOcrEngine(string baseDirectory) => _tessdataPath = ResolveTessdataPath(baseDirectory);

    public bool IsAvailable => _tessdataPath != null && !_engineFailed;

    public string? UnavailableReason => IsAvailable
        ? null
        : _tessdataPath == null
            ? "Языковые модели OCR не найдены. Запустите tools/fetch-tessdata.ps1 или переустановите приложение."
            : "Движок Tesseract не инициализировался (подробности в журнале).";

    /// <summary>Поиск tools\tessdata с rus+eng рядом с приложением и до шести уровней вверх (запуск из bin/Debug).</summary>
    private static string? ResolveTessdataPath(string baseDirectory)
    {
        var dir = new DirectoryInfo(baseDirectory);
        for (var depth = 0; dir != null && depth < 7; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "tessdata");
            if (File.Exists(Path.Combine(candidate, "rus.traineddata")) &&
                File.Exists(Path.Combine(candidate, "eng.traineddata")))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Распознаёт растр страницы. Возвращает слова с рамками в пикселях
    /// исходного растра; пустые и «мусорные» слова уже отфильтрованы.
    /// </summary>
    public Task<OcrPageResult> RecognizeAsync(RenderedPageImage image, int renderDpi, CancellationToken ct) =>
        Task.Run(() => Recognize(image, renderDpi, ct), ct);

    private OcrPageResult Recognize(RenderedPageImage image, int renderDpi, CancellationToken ct)
    {
        if (_tessdataPath == null)
            throw new InvalidOperationException("Языковые модели OCR не найдены.");
        var bmp = EncodeBmp24(image, renderDpi);

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed)
                throw new ObjectDisposedException(nameof(TesseractOcrEngine));
            var engine = GetEngine();
            using var pix = Pix.LoadFromMemory(bmp);
            using var page = engine.Process(pix, PageSegMode.Auto);
            var words = new List<OcrWord>();
            using (var iterator = page.GetIterator())
            {
                iterator.Begin();
                do
                {
                    ct.ThrowIfCancellationRequested();
                    if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box))
                        continue;
                    var text = (iterator.GetText(PageIteratorLevel.Word) ?? "").Trim();
                    if (text.Length == 0 || box.Width <= 0 || box.Height <= 0)
                        continue;
                    var confidence = iterator.GetConfidence(PageIteratorLevel.Word);
                    words.Add(new OcrWord(text, box.X1, box.Y1, box.Width, box.Height, confidence));
                } while (iterator.Next(PageIteratorLevel.Word));
            }
            return new OcrPageResult(words, page.GetMeanConfidence() * 100f);
        }
    }

    private TesseractEngine GetEngine()
    {
        if (_engine != null)
            return _engine;
        if (_engineFailed)
            throw new InvalidOperationException(UnavailableReason);
        try
        {
            _engine = new TesseractEngine(_tessdataPath, Languages, EngineMode.LstmOnly);
            return _engine;
        }
        catch
        {
            // Нативные библиотеки не загрузились (нет VC++ runtime и т.п.) —
            // функция становится недоступной, а не падает при каждой попытке.
            _engineFailed = true;
            throw;
        }
    }

    /// <summary>
    /// BGRA-растр как несжатый 24-бит BMP (низ-вверх) для leptonica —
    /// без зависимости от System.Drawing. DPI пишется в заголовок, иначе
    /// tesseract считает изображение 70 dpi и хуже сегментирует.
    /// </summary>
    private static byte[] EncodeBmp24(RenderedPageImage image, int dpi)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var rowSize = (width * 3 + 3) & ~3;
        var dataSize = rowSize * height;
        var bmp = new byte[54 + dataSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt32(bmp, 2, 54 + dataSize);
        WriteInt32(bmp, 10, 54);
        WriteInt32(bmp, 14, 40);
        WriteInt32(bmp, 18, width);
        WriteInt32(bmp, 22, height);
        bmp[26] = 1; // planes
        bmp[28] = 24; // bpp
        var pelsPerMeter = (int)Math.Round(dpi * 39.3701);
        WriteInt32(bmp, 38, pelsPerMeter);
        WriteInt32(bmp, 42, pelsPerMeter);

        for (var y = 0; y < height; y++)
        {
            var src = (height - 1 - y) * image.Stride;
            var dst = 54 + y * rowSize;
            for (var x = 0; x < width; x++)
            {
                bmp[dst + x * 3 + 0] = image.Bgra[src + x * 4 + 0];
                bmp[dst + x * 3 + 1] = image.Bgra[src + x * 4 + 1];
                bmp[dst + x * 3 + 2] = image.Bgra[src + x * 4 + 2];
            }
        }
        return bmp;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _engine?.Dispose();
            _engine = null;
        }
    }
}
