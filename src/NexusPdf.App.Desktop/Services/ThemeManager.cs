using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace NexusPdf.App.Desktop.Services;

public static class ThemeManager
{
    /// <summary>Тёмная ли сейчас тема — нужно окнам, чтобы покрасить заголовок.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Применяет тему: "light" | "dark" | "system" (по реестру Windows).</summary>
    public static void Apply(string theme)
    {
        var dark = theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;

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

        // Заголовок окна рисует Windows, а не WPF: без этого поверх тёмной
        // программы оставалась белая полоса с кнопками — первое, что видно.
        foreach (Window window in app.Windows)
            ApplyTitleBar(window);
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Красит системный заголовок окна под тему. Вызывать после появления
    /// дескриптора окна (SourceInitialized) и при каждой смене темы.
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return; // окно ещё не создано — покрасится в SourceInitialized
            var value = IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            // Косметика: на старой сборке Windows атрибута нет — не повод падать.
            Serilog.Log.Debug(ex, "Не удалось покрасить заголовок окна");
        }
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
