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

    public async ValueTask DisposeAsync() => await Engine.DisposeAsync();
}
