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
