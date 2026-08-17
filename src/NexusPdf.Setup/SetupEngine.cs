using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace NexusPdf.Setup;

public enum InstalledContext
{
    None,
    PerUser,
    PerMachine,
}

/// <summary>
/// Поиск уже установленной копии NexusPDF по UpgradeCode: Windows Installer
/// не выполняет мажорное обновление через границу контекстов (per-user ↔
/// per-machine), поэтому смену режима надо обнаруживать и блокировать заранее —
/// иначе появились бы две параллельные установки.
/// </summary>
public static class InstalledProductInspector
{
    private const string UpgradeCode = "{7C6E2B7A-9A1E-4F2B-B4B0-3E62D8C5A101}";
    private const uint ErrorNoMoreItems = 259;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string lpUpgradeCode, uint dwReserved, uint iProductIndex, StringBuilder lpProductBuf);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfo(string szProduct, string szProperty, StringBuilder lpValueBuf, ref uint pcchValueBuf);

    public static InstalledContext Detect()
    {
        var productCode = new StringBuilder(39);
        var status = MsiEnumRelatedProducts(UpgradeCode, 0, 0, productCode);
        if (status == ErrorNoMoreItems)
            return InstalledContext.None;
        if (status != 0)
            return InstalledContext.None; // не смогли определить — не блокируем установку

        var value = new StringBuilder(8);
        var length = (uint)value.Capacity;
        // INSTALLPROPERTY_ASSIGNMENTTYPE: "0" — per-user, "1" — per-machine
        if (MsiGetProductInfo(productCode.ToString(), "AssignmentType", value, ref length) != 0)
            return InstalledContext.None;
        return value.ToString() == "1" ? InstalledContext.PerMachine : InstalledContext.PerUser;
    }
}

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

    public string EffectiveInstallDir =>
        string.IsNullOrWhiteSpace(CustomDir) ? DefaultInstallDir(AllUsers) : CustomDir;

    public static string DefaultInstallDir(bool allUsers) => allUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusPDF")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NexusPDF");
}

public sealed record InstallResult(int ExitCode, string LogPath);

public static class SetupEngine
{
    /// <summary>
    /// Версия берётся ИЗ СБОРКИ, а не из константы: константу забыли обновить, и
    /// установщик четыре выпуска показывал чужой номер. Сборка же получает
    /// версию из Directory.Build.props, то есть из единственного места.
    /// </summary>
    public static string ProductVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }

    /// <summary>Распаковывает встроенный MSI во временный каталог и возвращает путь.</summary>
    public static string ExtractMsi()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("NexusPdf.Setup.Payload.NexusPdf.msi");
        if (stream == null)
            // Текст читает пользователь на экране «Installation did not finish»,
            // поэтому здесь не может быть команды сборки из репозитория: она
            // сообщала бы человеку то, чего он всё равно не сделает.
            throw new InvalidOperationException(
                "This installer file is incomplete: the installation package is missing from it. " +
                "Download NexusPdfSetup.exe again.");

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

    public static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static async Task<InstallResult> InstallAsync(SetupOptions options, string msiPath)
    {
        var logPath = Path.Combine(Path.GetTempPath(),
            $"NexusPdf-install-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        // Тихий режим никогда не показывает UAC: для per-machine требуем уже
        // повышенный процесс, иначе честно возвращаем 740 (elevation required).
        if (options.Silent && options.AllUsers && !IsElevated)
        {
            Console.Error.WriteLine(
                "NexusPdfSetup: /allusers in silent mode requires administrator rights (code 740).");
            return new InstallResult(740, logPath);
        }

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
        if (!string.IsNullOrWhiteSpace(options.CustomDir))
            args.Add("INSTALLFOLDER=" + Quote(options.CustomDir));

        var needRunas = options.AllUsers && !IsElevated; // интерактивный UAC только в GUI-режиме
        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = string.Join(" ", args),
            UseShellExecute = needRunas,
        };
        if (needRunas)
            psi.Verb = "runas";
        else
            psi.CreateNoWindow = true;

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start msiexec.");
            await process.WaitForExitAsync();
            if (process.ExitCode is 0 or 3010)
                NotifyShellAssociationsChanged();
            return new InstallResult(process.ExitCode, logPath);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Пользователь отклонил запрос прав администратора
            return new InstallResult(1602, logPath);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"NexusPdfSetup: could not start msiexec: {ex.Message}");
            return new InstallResult(1603, logPath);
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// Оболочка держит разбор ассоциаций в памяти, поэтому свежезарегистрированный
    /// обработчик эскизов она бы заметила только после перезапуска Проводника.
    /// Одно оповещение SHCNE_ASSOCCHANGED снимает вопрос: эскизы PDF появляются
    /// сразу после установки.
    /// </summary>
    private static void NotifyShellAssociationsChanged()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { /* оповещение необязательно: без него эскизы появятся позже */ }
    }

    public static void LaunchInstalledApp(SetupOptions options)
    {
        var exe = Path.Combine(options.EffectiveInstallDir, "NexusPdf.exe");
        if (File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = options.EffectiveInstallDir });
    }

    private static string Quote(string value) => "\"" + value + "\"";
}
