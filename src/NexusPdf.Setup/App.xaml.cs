using System.Windows;

namespace NexusPdf.Setup;

public partial class App : System.Windows.Application
{
    private async void OnAppStartup(object sender, StartupEventArgs e)
    {
        var options = SetupOptions.Parse(e.Args);
        if (options.Silent)
        {
            // Тихий режим: /S [/allusers] [/dir=путь] [/nodesktop]
            int exitCode;
            try
            {
                if (!string.IsNullOrWhiteSpace(options.CustomDir) &&
                    !System.IO.Path.IsPathRooted(options.CustomDir))
                {
                    Console.Error.WriteLine("NexusPdfSetup: /dir= требует полный путь (код 87).");
                    Shutdown(87);
                    return;
                }

                var installed = InstalledProductInspector.Detect();
                if (installed == InstalledContext.PerUser && options.AllUsers ||
                    installed == InstalledContext.PerMachine && !options.AllUsers)
                {
                    Console.Error.WriteLine(
                        "NexusPdfSetup: продукт уже установлен в другом режиме (per-user/per-machine). " +
                        "Сначала удалите существующую копию (код 1638).");
                    Shutdown(1638);
                    return;
                }

                var msi = SetupEngine.ExtractMsi();
                var result = await SetupEngine.InstallAsync(options, msi);
                exitCode = result.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                exitCode = 1603;
            }
            Shutdown(exitCode);
            return;
        }

        MainWindow = new SetupWindow(options);
        MainWindow.Show();
    }
}
