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
            // MuPDF в списке обязателен: он занимает 74 МБ поставки, выполняет
            // всё сжатие и распространяется под AGPL-3.0, а такую лицензию
            // получатель копии должен видеть, а не искать.
            Loc.Get("AboutEngineCompress"),
            Loc.F("AboutEngineOcr", ocrEngine),
            Loc.Get("AboutEngineOffice"),
        });
        // Копирайт берётся ИЗ СБОРКИ по той же причине, что и версия: строку в
        // словаре забывают обновить, и окно годами показывает чужой год.
        LicenseText.Text = Loc.F("AboutLicense", ReadCopyright());
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

    private static string ReadCopyright() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>
    /// Ведёт на раздел поддержки сайта, а не показывает реквизиты в самой
    /// программе: способы оплаты меняются, а установленная копия обновляется
    /// редко, и зашитый в неё кошелёк однажды окажется чужим.
    /// </summary>
    private void OnSupport(object sender, RoutedEventArgs e) =>
        Services.ProjectLinks.Open(Services.ProjectLinks.Support);

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
