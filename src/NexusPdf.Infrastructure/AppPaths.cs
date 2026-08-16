namespace NexusPdf.Infrastructure;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPDF");

    public static string LogsDir => Path.Combine(Root, "Logs");
    public static string RecoveryDir => Path.Combine(Root, "Recovery");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Временные PDF, собранные из перетащенных картинок. Живут, пока на них
    /// ссылаются страницы открытого документа; после сохранения содержимое уже
    /// в итоговом файле, и папку можно чистить при следующем запуске.
    /// </summary>
    public static string DroppedFilesFolder => Path.Combine(Root, "Dropped");

    /// <summary>
    /// Обработанные копии документов — результат чистки, пересжатия и
    /// оптимизации. Живут так же: пока на них ссылаются страницы открытой
    /// вкладки. После сохранения содержимое уже в итоговом файле.
    /// </summary>
    public static string ProcessedFolder => Path.Combine(Root, "Processed");

    public static string CrashSentinelFile => Path.Combine(Root, "session.lock");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RecoveryDir);
    }
}
