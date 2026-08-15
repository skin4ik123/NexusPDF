using System.Windows;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>
/// Плотность интерфейса: размеры кликабельных мест меняются на лету, без
/// перезапуска. Значения кладутся в ресурсы приложения, поэтому стили с
/// DynamicResource подхватывают их сразу — окно не пересобирается.
/// </summary>
public static class DensityManager
{
    public static UiMetrics Current { get; private set; } = UiMetrics.Comfortable;

    /// <summary>Пользователь выбрал плотность сам — автоматика больше не вмешивается.</summary>
    public static bool IsExplicit { get; private set; }

    public static event EventHandler? Changed;

    /// <param name="setting">"auto" | "compact" | "comfortable" | "touch".</param>
    public static void Apply(string? setting, bool touchUsedRecently = false)
    {
        IsExplicit = DensityPolicy.Parse(setting) != null;
        var density = DensityPolicy.Resolve(setting, touchUsedRecently, TouchInputWatcher.HasTouchScreen);
        Apply(UiMetrics.For(density));
    }

    public static void Apply(UiMetrics metrics)
    {
        Current = metrics;
        var resources = System.Windows.Application.Current?.Resources;
        if (resources == null) return;

        resources["UxTouchTarget"] = metrics.TouchTarget;
        resources["UxRowHeight"] = metrics.RowHeight;
        resources["UxGlyphSize"] = metrics.GlyphSize;
        resources["UxFontSize"] = metrics.FontSize;
        resources["UxToolPadding"] = new Thickness(metrics.PaddingX, metrics.PaddingY, metrics.PaddingX, metrics.PaddingY);
        resources["UxToolMargin"] = new Thickness(metrics.Gap, 0, metrics.Gap, 0);
        resources["UxMenuItemPadding"] = new Thickness(metrics.PaddingX, metrics.PaddingY, metrics.PaddingX, metrics.PaddingY);

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
