namespace NexusPdf.Infrastructure;

/// <summary>
/// Файл-метка незавершённой сессии: создаётся при старте, удаляется при чистом
/// выходе. Если при запуске метка уже есть — прошлая сессия завершилась аварийно.
/// </summary>
public static class CrashSentinel
{
    public static bool PreviousSessionCrashed()
    {
        return File.Exists(AppPaths.CrashSentinelFile);
    }

    public static void MarkSessionStarted()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.CrashSentinelFile, DateTimeOffset.Now.ToString("O"));
    }

    public static void MarkCleanExit()
    {
        try
        {
            File.Delete(AppPaths.CrashSentinelFile);
        }
        catch
        {
            // Не критично: в худшем случае при следующем запуске предложим восстановление.
        }
    }
}
