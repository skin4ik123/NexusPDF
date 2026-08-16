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
            // Без подключения к родительской консоли все сообщения ниже
            // пропадали, и от установщика оставался только код возврата.
            ParentConsole.AttachToParent();
            int exitCode;
            try
            {
                if (!string.IsNullOrWhiteSpace(options.CustomDir) &&
                    !System.IO.Path.IsPathRooted(options.CustomDir))
                {
                    Console.Error.WriteLine("NexusPdfSetup: /dir= requires a full path (code 87).");
                    Shutdown(87);
                    return;
                }

                var installed = InstalledProductInspector.Detect();
                if (installed == InstalledContext.PerUser && options.AllUsers ||
                    installed == InstalledContext.PerMachine && !options.AllUsers)
                {
                    Console.Error.WriteLine(
                        "NexusPdfSetup: the product is already installed in the other mode (per-user/per-machine). " +
                        "Remove the existing copy first (code 1638).");
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
