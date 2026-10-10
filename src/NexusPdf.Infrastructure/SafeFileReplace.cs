namespace NexusPdf.Infrastructure;

/// <summary>Почему не удалось записать файл: интерфейс переводит это в понятный текст.</summary>
public enum SaveFailure
{
    /// <summary>Причина не распознана: в тексте ошибки — то, что сообщила система.</summary>
    Other,

    /// <summary>У файла стоит «только чтение».</summary>
    ReadOnly,

    /// <summary>Нет прав на запись в папку или файл.</summary>
    AccessDenied,

    /// <summary>Файл занят другой программой (например, открыт в Acrobat).</summary>
    InUse,

    /// <summary>На диске не хватило места.</summary>
    DiskFull,

    /// <summary>Папка не найдена (переименована, отключён сетевой диск, вынута флешка).</summary>
    FolderMissing,

    /// <summary>Записанный файл не прошёл проверку — исходный не тронут.</summary>
    Validation,
}

/// <summary>
/// Сохранение не удалось. Исходный файл при этом гарантированно цел: запись
/// идёт во временный файл, и подмена делается только после его проверки.
/// </summary>
public sealed class SaveFailedException(SaveFailure failure, string path, Exception? inner = null)
    : IOException(failure + ": " + path + (inner != null ? " — " + inner.Message : ""), inner)
{
    public SaveFailure Failure { get; } = failure;
    public string Path { get; } = path;
}

/// <summary>Чем закончилась подмена: где теперь лежит прежняя версия файла.</summary>
/// <param name="RetiredPath">
/// Прежний файл, отодвинутый в сторону (скрытый, в той же папке), или null,
/// если файла раньше не было. Удаляет его вызывающий — после того как
/// освободит свои дескрипторы: открытый документ мог продолжать читать его.
/// </param>
public sealed record ReplaceOutcome(string? RetiredPath);

/// <summary>
/// Безопасная запись результата: сначала полностью пишем и проверяем временный
/// файл на том же томе, и только затем подменяем целевой. При любой ошибке
/// целевой файл остаётся нетронутым.
///
/// Подмена — двумя переименованиями: прежний файл отодвигается в сторону,
/// новый встаёт на его место; если второе не удалось, первое откатывается.
/// Переименование разрешено и для файла, который сама программа держит
/// открытым (он открыт с разрешением на удаление), поэтому открытый документ
/// не нужно закрывать до подмены — раньше его закрывали, и неудачная подмена
/// оставляла вкладку без файла: страницы переставали рисоваться, а повторное
/// сохранение падало.
/// </summary>
public static class SafeFileReplace
{
    public static Task WriteAndReplaceAsync(
        string targetPath,
        Func<string, Task> writeToTemp,
        Func<string, Task> validateTemp,
        bool keepBackup,
        CancellationToken ct) =>
        WriteAndReplaceAsync(targetPath, writeToTemp, validateTemp, beforeReplace: null, keepBackup, ct);

    /// <param name="beforeReplace">
    /// Вызывается после успешной проверки временного файла и непосредственно
    /// перед подменой цели. Прежняя версия файла удаляется сразу после подмены.
    /// </param>
    public static async Task WriteAndReplaceAsync(
        string targetPath,
        Func<string, Task> writeToTemp,
        Func<string, Task> validateTemp,
        Func<Task>? beforeReplace,
        bool keepBackup,
        CancellationToken ct)
    {
        var outcome = await WriteAndReplaceRetainingAsync(
            targetPath, writeToTemp, validateTemp,
            keepBackup ? targetPath + ".bak" : null, beforeReplace, ct).ConfigureAwait(false);
        if (outcome.RetiredPath != null)
            TryDelete(outcome.RetiredPath);
    }

    /// <summary>
    /// Запись с подменой, прежняя версия которой остаётся у вызывающего: её
    /// можно удалить только после того, как документ отпустит свой файл.
    /// </summary>
    /// <param name="backupPath">Куда положить копию прежней версии; null — не класть.</param>
    public static async Task<ReplaceOutcome> WriteAndReplaceRetainingAsync(
        string targetPath,
        Func<string, Task> writeToTemp,
        Func<string, Task> validateTemp,
        string? backupPath,
        Func<Task>? beforeReplace,
        CancellationToken ct)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var dir = Path.GetDirectoryName(fullTarget);
        if (string.IsNullOrEmpty(dir))
            throw new SaveFailedException(SaveFailure.FolderMissing, fullTarget);
        ThrowIfReadOnly(fullTarget);

        var name = Path.GetFileName(fullTarget);
        var tempPath = Path.Combine(dir, "." + name + ".nexustmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                Directory.CreateDirectory(dir);
                await writeToTemp(tempPath).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Ошибки самой записи (место, права, занятость) — понятной
                // причиной; ошибки содержимого идут к вызывающему как есть.
                throw Classify(ex is IOException or UnauthorizedAccessException ? ex : ex.InnerException!, fullTarget);
            }
            ct.ThrowIfCancellationRequested();

            try
            {
                await validateTemp(tempPath).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SaveFailedException(SaveFailure.Validation, fullTarget, ex);
            }
            ct.ThrowIfCancellationRequested();

            if (beforeReplace != null)
                await beforeReplace().ConfigureAwait(false);

            if (!File.Exists(fullTarget))
            {
                try
                {
                    File.Move(tempPath, fullTarget);
                }
                catch (Exception ex)
                {
                    throw Classify(ex, fullTarget);
                }
                return new ReplaceOutcome(null);
            }

            var original = new FileInfo(fullTarget);
            var created = original.CreationTimeUtc;
            var attributes = original.Attributes;
            var retired = Path.Combine(dir, "." + name + ".nexusold-" + Guid.NewGuid().ToString("N"));

            try
            {
                File.Move(fullTarget, retired);
            }
            catch (Exception ex)
            {
                throw Classify(ex, fullTarget);
            }

            try
            {
                File.Move(tempPath, fullTarget);
            }
            catch (Exception ex)
            {
                // Новый файл на место не встал — прежний возвращается, как был.
                try
                {
                    File.Move(retired, fullTarget);
                }
                catch (Exception restore)
                {
                    Serilog.Log.Fatal(restore,
                        "Прежняя версия {Path} не вернулась на место, она лежит в {Retired}", fullTarget, retired);
                }
                throw Classify(ex, fullTarget);
            }

            // Новая версия наследует дату создания и атрибуты прежней (кроме
            // скрытости служебного имени): для человека это тот же файл.
            try
            {
                File.SetCreationTimeUtc(fullTarget, created);
                File.SetAttributes(fullTarget, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Temporary));
                File.SetAttributes(retired, File.GetAttributes(retired) | FileAttributes.Hidden);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Атрибуты файла не перенесены: {Path}", fullTarget);
            }

            if (backupPath != null)
            {
                try
                {
                    File.Copy(retired, backupPath, overwrite: true);
                    File.SetAttributes(backupPath, File.GetAttributes(backupPath) & ~FileAttributes.Hidden);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Резервная копия {Backup} не создана", backupPath);
                }
            }
            return new ReplaceOutcome(retired);
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

    /// <summary>
    /// Файл «только для чтения» не перезаписывается — и об этом надо сказать
    /// ДО записи, а не после неё безликим «не удалось сохранить».
    /// </summary>
    public static void ThrowIfReadOnly(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).IsReadOnly)
                throw new SaveFailedException(SaveFailure.ReadOnly, path);
        }
        catch (SaveFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Classify(ex, path);
        }
    }

    /// <summary>Удаление прежней версии: лучшая попытка, без исключений.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Прежняя версия файла пока занята: {Path}", path);
            return false;
        }
    }

    private static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException ||
        ex.InnerException is IOException or UnauthorizedAccessException;

    /// <summary>Системная ошибка записи → понятная причина.</summary>
    public static SaveFailedException Classify(Exception ex, string path)
    {
        if (ex is SaveFailedException known) return known;
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        const int DiskFull = unchecked((int)0x80070070);
        const int HandleDiskFull = unchecked((int)0x80070027);

        var failure = ex switch
        {
            UnauthorizedAccessException => SaveFailure.AccessDenied,
            DirectoryNotFoundException => SaveFailure.FolderMissing,
            DriveNotFoundException => SaveFailure.FolderMissing,
            IOException io when io.HResult is SharingViolation or LockViolation => SaveFailure.InUse,
            IOException io when io.HResult is DiskFull or HandleDiskFull => SaveFailure.DiskFull,
            _ => SaveFailure.Other,
        };
        return new SaveFailedException(failure, path, ex);
    }
}
