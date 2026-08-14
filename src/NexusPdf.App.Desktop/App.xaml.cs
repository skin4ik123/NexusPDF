using System.IO;
using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.App.Desktop.Views;
using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Pdfium;
using Serilog;

namespace NexusPdf.App.Desktop;

public partial class App : System.Windows.Application
{
    private AppServices? _services;
    private SingleInstanceService? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        var store = new JsonSettingsStore(AppPaths.SettingsFile);
        var settings = store.Load();

        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)).ToList();

        _singleInstance = new SingleInstanceService();
        if (settings.SingleInstance && !_singleInstance.IsPrimary)
        {
            if (SingleInstanceService.TrySendToPrimary(files))
            {
                Shutdown();
                return;
            }
        }

        Log.Logger = LoggingSetup.Create();
        Log.Information("NexusPDF запускается. Версия {Version}",
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");

        Loc.Load(settings.Language);
        ThemeManager.Apply(settings.Theme);

        var crashed = CrashSentinel.PreviousSessionCrashed();
        CrashSentinel.MarkSessionStarted();

        _services = new AppServices(new PdfiumRenderEngine(), settings, store);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Необработанное исключение интерфейса");
            ErrorDialog.Show(WindowManager.ActiveOrFirst(), Loc.Get("ErrorTitle"),
                args.Exception.Message, args.Exception.ToString());
            args.Handled = true;
        };

        var window = WindowManager.OpenWindow(_services, null);
        window.ViewModel.ShowCrashRestoreBanner = crashed && settings.LastSessionFiles.Count > 0;

        _singleInstance.StartServer(received =>
        {
            Dispatcher.Invoke(async () =>
            {
                var target = WindowManager.ActiveOrFirst();
                if (target == null) return;
                if (target.WindowState == WindowState.Minimized)
                    target.WindowState = WindowState.Normal; // Activate сам не разворачивает
                target.Activate();
                await target.ViewModel.OpenFilesAsync(received.Where(File.Exists));
            });
        });

        if (files.Count > 0)
            _ = window.ViewModel.OpenFilesAsync(files);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services != null)
            {
                // Список сессии уже поддерживается актуальным по ходу работы
                // (UpdateSessionSnapshot/SnapshotBeforeExit) — здесь окна уже
                // закрыты и пересчитывать его нельзя: получился бы пустой список.
                _services.SaveSettings();
                _services.DisposeAsync().AsTask().GetAwaiter().GetResult();

                // Метку чистого выхода снимает только полный запуск: вторичный
                // экземпляр (передал файлы и вышел) не должен стирать сентинел
                // живого первичного процесса.
                CrashSentinel.MarkCleanExit();
            }
            _singleInstance?.Dispose();
            Log.Information("NexusPDF завершён.");
            Log.CloseAndFlush();
        }
        catch
        {
            // Ошибки при выходе не должны показывать диалоги.
        }
        base.OnExit(e);
    }
}
