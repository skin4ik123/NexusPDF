using Serilog;
using Serilog.Core;

namespace NexusPdf.Infrastructure;

public static class LoggingSetup
{
    /// <summary>
    /// Файловый журнал приложения. Правило: в журнал не попадают содержимое
    /// документов и пароли; пути допустимы (локальный журнал пользователя).
    /// </summary>
    public static Logger Create()
    {
        AppPaths.EnsureCreated();
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "nexuspdf-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 8 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
