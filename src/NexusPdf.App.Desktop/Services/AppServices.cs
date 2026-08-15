using NexusPdf.Application;
using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Qpdf;

namespace NexusPdf.App.Desktop.Services;

/// <summary>Общие для всех окон сервисы приложения.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public AppServices(IPdfRenderEngine engine, AppSettings settings, JsonSettingsStore store)
    {
        Engine = engine;
        Settings = settings;
        SettingsStore = store;
        Cache = new RenderCache(settings.RenderCacheMegabytes);
        SaveService = new SaveService(engine);
        Qpdf = new QpdfEngine();
        Tools = new DocumentToolsService(engine, Qpdf, Qpdf);
        Print = new PrintService();
        Signatures = new SignatureStore();
        OcrEngine = CreateRecognizer(settings);
        Ocr = new OcrService(OcrEngine);
        Convert = new ConvertService(engine);
        Layers = new LayerService(Qpdf);
    }

    /// <summary>
    /// Движок распознавания по настройкам. PaddleOCR точнее и стоит по
    /// умолчанию, но если его модели не загружены — честно берётся Tesseract,
    /// а не выключается вся функция.
    /// </summary>
    private static NexusPdf.Ocr.ITextRecognizer CreateRecognizer(AppSettings settings)
    {
        if (!string.Equals(settings.OcrEngine, "tesseract", StringComparison.OrdinalIgnoreCase))
        {
            var paddle = new NexusPdf.Ocr.Paddle.PaddleOcrEngine(
                AppContext.BaseDirectory, settings.OcrLanguagePack);
            if (paddle.IsAvailable)
                return paddle;
            Serilog.Log.Information(
                "PaddleOCR недоступен ({Reason}), распознавание идёт на Tesseract",
                paddle.UnavailableReason);
            paddle.Dispose();
        }
        return new NexusPdf.Ocr.TesseractOcrEngine();
    }

    public IPdfRenderEngine Engine { get; }
    public AppSettings Settings { get; }
    public JsonSettingsStore SettingsStore { get; }
    public RenderCache Cache { get; }
    public SaveService SaveService { get; }
    public QpdfEngine Qpdf { get; }
    public DocumentToolsService Tools { get; }
    public PrintService Print { get; }
    public SignatureStore Signatures { get; }
    public NexusPdf.Ocr.ITextRecognizer OcrEngine { get; private set; }
    public OcrService Ocr { get; private set; }

    /// <summary>
    /// Смена движка или языкового пакета из интерфейса. Движок один на всё
    /// приложение: иначе распознавание из окна Paint осталось бы на старом
    /// языке, а модели (59 МБ детектор) грузились бы дважды.
    /// </summary>
    public void ApplyOcrSettings(string engineId, string languagePack)
    {
        if (string.Equals(Settings.OcrEngine, engineId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Settings.OcrLanguagePack, languagePack, StringComparison.OrdinalIgnoreCase) &&
            OcrEngine.IsAvailable)
            return;

        Settings.OcrEngine = engineId;
        Settings.OcrLanguagePack = languagePack;
        SaveSettings();

        var previous = OcrEngine;
        OcrEngine = CreateRecognizer(Settings);
        Ocr = new OcrService(OcrEngine);
        previous.Dispose();
    }
    public ConvertService Convert { get; }
    public LayerService Layers { get; }

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось сохранить настройки");
        }
    }

    public async ValueTask DisposeAsync()
    {
        OcrEngine.Dispose();
        await Engine.DisposeAsync();
    }
}
