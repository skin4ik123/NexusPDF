using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace NexusPdf.Setup;

public sealed class SetupOptions
{
    public bool Silent { get; init; }
    public bool AllUsers { get; set; }
    public bool DesktopShortcut { get; set; } = true;
    public string? CustomDir { get; set; }

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions
        {
            Silent = args.Any(a =>
                a.Equals("/S", StringComparison.Ordinal) ||
                a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/qn", StringComparison.OrdinalIgnoreCase)),
        };
        foreach (var arg in args)
        {
            if (arg.Equals("/allusers", StringComparison.OrdinalIgnoreCase))
                options.AllUsers = true;
            else if (arg.Equals("/nodesktop", StringComparison.OrdinalIgnoreCase))
                options.DesktopShortcut = false;
            else if (arg.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))
                options.CustomDir = arg[5..].Trim('"');
        }
        return options;
    }

    public string EffectiveInstallDir => CustomDir ?? DefaultInstallDir(AllUsers);

    public static string DefaultInstallDir(bool allUsers) => allUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusPDF")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NexusPDF");
}

public sealed record InstallResult(int ExitCode, string LogPath);

public static class SetupEngine
{
    public const string ProductVersion = "0.2.0";

    /// <summary>Распаковывает встроенный MSI во временный каталог и возвращает путь.</summary>
    public static string ExtractMsi()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("NexusPdf.Setup.Payload.NexusPdf.msi");
        if (stream == null)
            throw new InvalidOperationException(
                "Установочный пакет не встроен в эту сборку (dev-версия Setup). " +
                "Соберите артефакты через ./build.ps1 -Msi.");

        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var msiPath = Path.Combine(dir, "NexusPdf.msi");
        using var file = File.Create(msiPath);
        stream.CopyTo(file);
        return msiPath;
    }

    public static string LoadLicenseText()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("NexusPdf.Setup.Payload.license.txt");
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static async Task<InstallResult> InstallAsync(SetupOptions options, string msiPath)
    {
        var logPath = Path.Combine(Path.GetTempPath(),
            $"NexusPdf-install-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var args = new List<string>
        {
            "/i", Quote(msiPath),
            "/qn", "/norestart",
            "/l*v", Quote(logPath),
            "DESKTOP_SHORTCUT=" + (options.DesktopShortcut ? "1" : "0"),
        };
        if (options.AllUsers)
            args.Add("ALLUSERS=1");
        else
            args.Add("MSIINSTALLPERUSER=1");
        if (options.CustomDir != null)
            args.Add("INSTALLFOLDER=" + Quote(options.CustomDir));

        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = string.Join(" ", args),
            UseShellExecute = options.AllUsers, // для per-machine нужен UAC
        };
        if (options.AllUsers)
            psi.Verb = "runas";
        else
            psi.CreateNoWindow = true;

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить msiexec.");
            await process.WaitForExitAsync();
            return new InstallResult(process.ExitCode, logPath);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Пользователь отклонил запрос прав администратора
            return new InstallResult(1602, logPath);
        }
    }

    public static void LaunchInstalledApp(SetupOptions options)
    {
        var exe = Path.Combine(options.EffectiveInstallDir, "NexusPdf.exe");
        if (File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = options.EffectiveInstallDir });
    }

    private static string Quote(string value) => "\"" + value + "\"";
}
