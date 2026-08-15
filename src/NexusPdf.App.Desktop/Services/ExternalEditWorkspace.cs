using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using NexusPdf.Infrastructure;
using Serilog;

namespace NexusPdf.App.Desktop.Services;

/// <summary>
/// Рабочая папка для правки во внешнем редакторе. Временное изображение —
/// это фрагмент документа пользователя, поэтому:
/// - лежит в папке приложения, а не в общем %TEMP%;
/// - имя случайное;
/// - доступ ограничен текущей учётной записью (наследование прав отключено);
/// - удаляется после импорта или отмены;
/// - остатки прошлых сеансов (после сбоя) удаляются при запуске.
/// Путь к файлу в журнал не пишется.
/// </summary>
public sealed class ExternalEditWorkspace : IDisposable
{
    private static string RootDir => Path.Combine(AppPaths.Root, "EditSessions");

    public string Folder { get; }
    public string ImagePath { get; }

    private bool _disposed;

    private ExternalEditWorkspace(string folder, string imagePath)
    {
        Folder = folder;
        ImagePath = imagePath;
    }

    /// <summary>Удаляет папки правки, оставшиеся от прошлых сеансов (например после сбоя).</summary>
    public static void CleanupOrphans()
    {
        try
        {
            if (!Directory.Exists(RootDir))
                return;
            foreach (var dir in Directory.EnumerateDirectories(RootDir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { /* занято другим процессом — оставляем */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось очистить временные папки правки");
        }
    }

    public static ExternalEditWorkspace Create(string fileNameHint)
    {
        Directory.CreateDirectory(RootDir);
        var folder = Path.Combine(RootDir, Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(folder);
        RestrictToCurrentUser(directory);

        // Имя файла видно пользователю в заголовке редактора — оставляем
        // осмысленный префикс, но добавляем случайную часть.
        var safeHint = string.Join("_", fileNameHint.Split(Path.GetInvalidFileNameChars()));
        if (safeHint.Length > 40)
            safeHint = safeHint[..40];
        var image = Path.Combine(folder, $"{safeHint}-{Guid.NewGuid():N}.png");
        return new ExternalEditWorkspace(folder, image);
    }

    private static void RestrictToCurrentUser(DirectoryInfo directory)
    {
        try
        {
            var security = new DirectorySecurity();
            // Наследуемые правила отключаются: иначе папку увидит любой, кому
            // открыт доступ к профилю (например, «Все» в неверно настроенной системе).
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var user = WindowsIdentity.GetCurrent().User;
            if (user != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl,
                    InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            directory.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // На некоторых файловых системах ACL недоступны — это не повод
            // отменять правку, но факт фиксируем.
            Log.Warning(ex, "Не удалось ограничить права временной папки правки");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось удалить временную папку правки");
        }
    }
}
