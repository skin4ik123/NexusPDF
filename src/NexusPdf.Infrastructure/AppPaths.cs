namespace NexusPdf.Infrastructure;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPDF");

    public static string LogsDir => Path.Combine(Root, "Logs");
    public static string RecoveryDir => Path.Combine(Root, "Recovery");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string CrashSentinelFile => Path.Combine(Root, "session.lock");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RecoveryDir);
    }
}
