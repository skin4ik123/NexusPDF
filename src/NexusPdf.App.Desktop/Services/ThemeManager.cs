using System.Windows;
using Microsoft.Win32;

namespace NexusPdf.App.Desktop.Services;

public static class ThemeManager
{
    /// <summary>Применяет тему: "light" | "dark" | "system" (по реестру Windows).</summary>
    public static void Apply(string theme)
    {
        var dark = theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };

        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var app = System.Windows.Application.Current;
        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.Ordinal) ||
                 d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.Ordinal)));
        if (existing != null)
            app.Resources.MergedDictionaries.Remove(existing);
        app.Resources.MergedDictionaries.Insert(0, dict);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
