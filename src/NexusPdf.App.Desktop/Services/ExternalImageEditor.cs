using System.Diagnostics;
using System.IO;
using Serilog;

namespace NexusPdf.App.Desktop.Services;

/// <summary>
/// Запуск внешнего редактора изображений (по умолчанию — Microsoft Paint) и
/// отслеживание сохранения файла.
///
/// Путь к Paint НЕ задан жёстко: в Windows 11 это упакованное приложение, и
/// «C:\Windows\System32\mspaint.exe» может отсутствовать. Порядок поиска:
/// сохранённый пользователем редактор → mspaint.exe через разрешение оболочки
/// → системный обработчик «Изменить» для PNG.
///
/// Программа НЕ ждёт закрытия редактора: пользователь может сохранить файл, не
/// закрывая Paint. Изменение отслеживается по файлу, импорт запускается
/// пользователем или предлагается автоматически после сохранения.
/// </summary>
public sealed class ExternalImageEditor : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _path;
    private DateTime _lastKnownWrite;
    private long _lastKnownSize;
    private Process? _process;

    public event EventHandler? FileChanged;
    public event EventHandler? EditorExited;

    public ExternalImageEditor(string imagePath)
    {
        _path = imagePath;
        var info = new FileInfo(imagePath);
        _lastKnownWrite = info.LastWriteTimeUtc;
        _lastKnownSize = info.Length;

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(imagePath)!, Path.GetFileName(imagePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => FileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Настроенный пользователем редактор (полный путь) или null.</summary>
    public static string? PreferredEditorPath { get; set; }

    /// <summary>
    /// Запускает редактор. false — редактор не найден (вызывающая сторона
    /// предлагает выбрать другой или отменить операцию).
    /// </summary>
    public bool Launch()
    {
        foreach (var start in BuildLaunchAttempts(_path))
        {
            try
            {
                var process = Process.Start(start);
                if (process == null)
                    continue;
                _process = process;
                try
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (_, _) => EditorExited?.Invoke(this, EventArgs.Empty);
                }
                catch (InvalidOperationException)
                {
                    // Процесс уже завершился (оболочка передала файл открытому окну).
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Редактор не запустился этим способом, пробуем следующий");
            }
        }
        return false;
    }

    private static IEnumerable<ProcessStartInfo> BuildLaunchAttempts(string path)
    {
        if (PreferredEditorPath is { Length: > 0 } preferred && File.Exists(preferred))
        {
            yield return new ProcessStartInfo(preferred, $"\"{path}\"") { UseShellExecute = false };
        }

        // Разрешение через оболочку: работает и для упакованного Paint
        // (алиас в %LOCALAPPDATA%\Microsoft\WindowsApps).
        yield return new ProcessStartInfo("mspaint.exe", $"\"{path}\"") { UseShellExecute = true };

        // Системный обработчик «Изменить» для PNG.
        yield return new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" };
    }

    /// <summary>Есть ли доступный редактор (для честного показа команды в интерфейсе).</summary>
    public static bool IsEditorAvailable()
    {
        if (PreferredEditorPath is { Length: > 0 } preferred && File.Exists(preferred))
            return true;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            try
            {
                if (File.Exists(Path.Combine(dir, "mspaint.exe")))
                    return true;
            }
            catch (ArgumentException)
            {
                // Некорректный элемент PATH — пропускаем.
            }
        }
        return false;
    }

    /// <summary>
    /// Файл изменился И дописан до конца (не занят редактором). Частично
    /// записанный PNG импортировать нельзя.
    /// </summary>
    public bool TryDetectCompletedSave()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
                return false;
            if (info.LastWriteTimeUtc == _lastKnownWrite && info.Length == _lastKnownSize)
                return false;

            // Проверка на завершённость записи: файл должен открываться
            // монопольно и иметь ненулевой размер.
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (stream.Length == 0)
                    return false;
            }

            _lastKnownWrite = info.LastWriteTimeUtc;
            _lastKnownSize = info.Length;
            return true;
        }
        catch (IOException)
        {
            return false; // редактор ещё пишет — импорт откладывается
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _process?.Dispose();
    }
}
