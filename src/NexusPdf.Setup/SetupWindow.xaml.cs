using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NexusPdf.Setup;

public partial class SetupWindow : Window
{
    private static readonly string[] ProgressPhrases =
    {
        "Unpacking components…",
        "Copying files…",
        "Registering file types…",
        "Creating shortcuts…",
        "Almost done…",
    };

    private readonly SetupOptions _options;
    private readonly DispatcherTimer _phraseTimer;
    private int _phraseIndex;
    private bool _pathEditedByUser;
    private bool _installing;
    private string? _lastLogPath;
    private string? _lastError;

    public SetupWindow(SetupOptions options)
    {
        _options = options;
        InitializeComponent();
        LicenseText.Text = SetupEngine.LoadLicenseText();
        VersionText.Text = $"version {SetupEngine.ProductVersion}";
        PathBox.Text = SetupOptions.DefaultInstallDir(allUsers: true);
        _pathEditedByUser = false;

        // Windows Installer не выполняет обновление через границу контекстов:
        // если копия уже установлена, режим фиксируется на существующем.
        var installed = InstalledProductInspector.Detect();
        if (installed == InstalledContext.PerUser)
        {
            PerUserRadio.IsChecked = true;
            PerMachineRadio.IsEnabled = false;
            ContextNote.Text = "An existing per-user installation was found — it will be upgraded in the same mode, so PDF previews in Explorer stay off. To turn them on, remove the current copy and install for all users.";
            ContextNote.Visibility = Visibility.Visible;
        }
        else if (installed == InstalledContext.PerMachine)
        {
            PerMachineRadio.IsChecked = true;
            PerUserRadio.IsEnabled = false;
            ContextNote.Text = "An existing all-users installation was found — it will be upgraded in the same mode. To change the mode, remove the current copy first.";
            ContextNote.Visibility = Visibility.Visible;
        }

        _phraseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.7) };
        _phraseTimer.Tick += (_, _) =>
        {
            _phraseIndex = (_phraseIndex + 1) % ProgressPhrases.Length;
            ProgressStatus.Text = ProgressPhrases[_phraseIndex];
        };
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (PathBox == null || _pathEditedByUser) return;
        var allUsers = PerMachineRadio.IsChecked == true;
        var text = SetupOptions.DefaultInstallDir(allUsers);
        _suppressPathEvent = true;
        PathBox.Text = text;
        _suppressPathEvent = false;
    }

    private bool _suppressPathEvent;

    private void OnPathEdited(object sender, RoutedEventArgs e)
    {
        if (!_suppressPathEvent && PathBox.IsKeyboardFocused)
            _pathEditedByUser = true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "NexusPDF install folder",
            InitialDirectory = Path.GetDirectoryName(PathBox.Text) ?? "",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _pathEditedByUser = true;
            PathBox.Text = Path.Combine(dialog.FolderName, "NexusPDF");
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;

        _options.AllUsers = PerMachineRadio.IsChecked == true;
        _options.DesktopShortcut = DesktopCheck.IsChecked == true;
        var defaultDir = SetupOptions.DefaultInstallDir(_options.AllUsers);

        PathError.Visibility = Visibility.Collapsed;
        var rawPath = PathBox.Text.Trim();
        if (rawPath.Length == 0)
        {
            _options.CustomDir = null;
        }
        else
        {
            string fullPath;
            try
            {
                if (!Path.IsPathRooted(rawPath))
                    throw new ArgumentException("the path is not absolute");
                fullPath = Path.GetFullPath(rawPath);
            }
            catch
            {
                PathError.Text = "Enter a full path to a folder, for example C:\\Apps\\NexusPDF.";
                PathError.Visibility = Visibility.Visible;
                _installing = false;
                return;
            }
            _options.CustomDir = string.Equals(fullPath, defaultDir, StringComparison.OrdinalIgnoreCase)
                ? null
                : fullPath;
        }

        ShowPage(ProgressPage);
        HeaderText.Text = "Installing…";
        CloseButton.IsEnabled = false;
        _phraseIndex = 0;
        ProgressStatus.Text = ProgressPhrases[0];
        _phraseTimer.Start();

        try
        {
            var msi = await Task.Run(SetupEngine.ExtractMsi);
            var result = await SetupEngine.InstallAsync(_options, msi);
            _lastLogPath = result.LogPath;

            if (result.ExitCode is 0 or 3010)
            {
                HeaderText.Text = "Installation complete";
                if (result.ExitCode == 3010)
                    DoneText.Text = "NexusPDF is installed. A restart may be needed to finish.";
                LaunchButton.Visibility = LaunchCheck.IsChecked == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ShowPage(DonePage);
            }
            else if (result.ExitCode == 1602)
            {
                HeaderText.Text = "Install";
                ShowPage(OptionsPage); // пользователь отменил (или отклонил UAC) — вернуться к выбору
            }
            else
            {
                // Голый код Windows Installer человеку ничего не говорит, поэтому
                // самые частые из них названы словами; остальные остаются номером
                // для отчёта, который тут же можно скопировать кнопкой.
                var reason = result.ExitCode switch
                {
                    1602 => "Installation was cancelled.",
                    1603 => "Installation failed. Close NexusPDF if it is running and try again.",
                    1618 => "Another installation is already running. Wait for it to finish.",
                    1638 => "Another version of NexusPDF is installed. Remove it first.",
                    _ => $"Installation failed (Windows Installer code {result.ExitCode}).",
                };
                ShowError(reason + (_lastLogPath != null ? $"\nLog: {_lastLogPath}" : ""));
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            ShowError(ex.Message);
        }
        finally
        {
            _phraseTimer.Stop();
            CloseButton.IsEnabled = true;
            _installing = false;
        }
    }

    private void ShowError(string message)
    {
        HeaderText.Text = "Error";
        ErrorText.Text = message;
        ShowPage(ErrorPage);
    }

    private void ShowPage(FrameworkElement page)
    {
        OptionsPage.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Collapsed;
        DonePage.Visibility = Visibility.Collapsed;
        ErrorPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        HeaderText.Text = "Install";
        ShowPage(OptionsPage);
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = ErrorText.Text;
            if (_lastError != null) report += Environment.NewLine + _lastError;
            if (_lastLogPath != null && File.Exists(_lastLogPath))
                report += Environment.NewLine + "--- msiexec log (tail) ---" + Environment.NewLine +
                          string.Join(Environment.NewLine, File.ReadLines(_lastLogPath).TakeLast(80));
            Clipboard.SetText(report);
        }
        catch
        {
            // буфер обмена занят — не критично
        }
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        SetupEngine.LaunchInstalledApp(_options);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
