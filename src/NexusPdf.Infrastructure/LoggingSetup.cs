using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NexusPdf.Infrastructure;

public static class LoggingSetup
{
    /// <summary>
    /// Переменная окружения, включающая подробный журнал. Нужна ровно для
    /// одного случая: у пользователя что-то не работает, и надо увидеть
    /// подробности, не пересобирая программу.
    /// </summary>
    public const string VerboseVariable = "NEXUSPDF_LOG";

    /// <summary>
    /// Подробный ли журнал сейчас. Отладочный уровень пишет тайминги отрисовки
    /// и сборки списков страниц — на документе в тысячу страниц это тысячи
    /// строк, которые в обычной работе не нужны никому.
    /// </summary>
    public static bool IsVerbose =>
        string.Equals(Environment.GetEnvironmentVariable(VerboseVariable),
            "debug", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Файловый журнал приложения. Правило: в журнал не попадают содержимое
    /// документов и пароли; пути допустимы (локальный журнал пользователя).
    /// </summary>
    /// <param name="fileNamePrefix">
    /// Начало имени файла журнала. У консольной программы оно своё: приложение
    /// держит свой файл открытым монопольно, и общий файл на два процесса
    /// означал бы, что второй запуск падает при старте.
    /// </param>
    public static Logger Create(string fileNamePrefix = "nexuspdf-")
    {
        AppPaths.EnsureCreated();
        return new LoggerConfiguration()
            .MinimumLevel.Is(IsVerbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, fileNamePrefix + ".log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 8 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
