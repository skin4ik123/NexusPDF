using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// О программе. Версия берётся ИЗ СБОРКИ, а не из словаря: строку в словаре
/// забывали обновлять, и окно годами показывало версию, которой давно нет.
/// </summary>
public partial class AboutDialog : Window
{
    private AboutDialog(string ocrEngine)
    {
        InitializeComponent();
        VersionText.Text = Loc.F("AboutVersion", ReadVersion());
        EnginesText.Text = string.Join(Environment.NewLine, new[]
        {
            Loc.Get("AboutEngineRender"),
            Loc.Get("AboutEngineStructure"),
            Loc.F("AboutEngineOcr", ocrEngine),
        });
    }

    public static void Show(Window? owner, string ocrEngine)
    {
        var dialog = new AboutDialog(ocrEngine);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private static string ReadVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
        // NuGet дописывает к версии хеш коммита через «+» — в окне он лишний.
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }

    /// <summary>Список сторонних компонентов и их лицензий лежит рядом с программой.</summary>
    private void OnNotices(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        if (!File.Exists(path))
        {
            ErrorDialog.Show(this, Loc.Get("About"), Loc.F("AboutNoticesMissing", path), "");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, Loc.Get("About"), ex.Message, ex.ToString());
        }
    }
}
