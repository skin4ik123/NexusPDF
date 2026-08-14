namespace NexusPdf.Infrastructure;

/// <summary>
/// Безопасная запись результата: сначала полностью пишем и проверяем временный
/// файл на том же томе, и только затем атомарно подменяем целевой. При любой
/// ошибке целевой файл остаётся нетронутым.
/// </summary>
public static class SafeFileReplace
{
    public static async Task WriteAndReplaceAsync(
        string targetPath,
        Func<string, Task> writeToTemp,
        Func<string, Task> validateTemp,
        bool keepBackup,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (string.IsNullOrEmpty(dir))
            throw new IOException($"Некорректный путь сохранения: {targetPath}");
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir, "." + Path.GetFileName(targetPath) + ".nexustmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await writeToTemp(tempPath).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await validateTemp(tempPath).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (File.Exists(targetPath))
            {
                var backupPath = keepBackup ? targetPath + ".bak" : null;
                try
                {
                    File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Осиротевший временный файл не должен маскировать исходную ошибку.
            }
        }
    }
}
