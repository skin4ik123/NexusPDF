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

        // Отсчёт ведётся от СТАРТА ПРОЦЕССА, а не от этой строки: пользователь
        // ждёт с двойного щелчка в проводнике, и время загрузки среды —
        // такая же часть ожидания, как и наша работа.
        var startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        double Elapsed() => (DateTime.Now - startedAt).TotalMilliseconds;

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
        Log.Debug("Запуск: среда и настройки готовы за {Elapsed:N0} мс", Elapsed());

        Loc.Load(settings.Language);
        // Каждое окно красит свой системный заголовок под тему сразу, как
        // только появится: один обработчик на класс дешевле, чем помнить про
        // это в каждом из полутора десятков диалогов.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ThemeManager.ApplyTitleBar((Window)sender)));
        ThemeManager.Apply(settings.Theme);

        // Плотность интерфейса. В режиме «авто» она следует за тем, чем
        // работают сейчас: до первого касания — мышиная, после — пальцевая.
        Services.Ux.DensityManager.Apply(settings.UiDensity);
        Services.Ux.TouchInputWatcher.Start(settings.UiDensity);

        var crashed = CrashSentinel.PreviousSessionCrashed();
        CrashSentinel.MarkSessionStarted();

        _services = new AppServices(new PdfiumRenderEngine(), settings, store);
        Log.Debug("Запуск: службы созданы за {Elapsed:N0} мс", Elapsed());

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Необработанное исключение интерфейса");
            ErrorDialog.Show(WindowManager.ActiveOrFirst(), Loc.Get("ErrorTitle"),
                args.Exception.Message, args.Exception.ToString());
            args.Handled = true;
        };

        // Сбой в ФОНОВОЙ работе без этих подписок не попадал ни в журнал, ни к
        // пользователю: в программе десятки fire-and-forget задач (ввод в поля
        // форм, миниатюры, проверка подписей).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Необработанное исключение вне интерфейса (IsTerminating={Terminating})",
                    args.IsTerminating);
            else
                Log.Fatal("Необработанное исключение вне интерфейса: {Object}", args.ExceptionObject);
            Log.CloseAndFlush(); // процесс может завершиться сразу после обработчика
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Ошибка фоновой задачи осталась незамеченной");
            args.SetObserved(); // иначе финализатор завершил бы процесс
        };

        BindingErrorTracing.Attach();

        // Окно создаётся, уже ЗНАЯ, что файл открывается: иначе оно успевает
        // показать стартовый экран «перетащите файл», и пользователь на долю
        // секунды видит приглашение открыть то, что и так открывается.
        var window = WindowManager.OpenWindow(_services, null, pendingFiles: files.Count);
        window.ViewModel.ShowCrashRestoreBanner = crashed && settings.LastSessionFiles.Count > 0;
        Log.Debug("Запуск: окно показано за {Elapsed:N0} мс", Elapsed());

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
        {
            _ = window.ViewModel.OpenFilesAsync(files)
                .ContinueWith(_ => Log.Debug("Запуск: документ открыт за {Elapsed:N0} мс", Elapsed()),
                    TaskScheduler.Default);
        }
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
