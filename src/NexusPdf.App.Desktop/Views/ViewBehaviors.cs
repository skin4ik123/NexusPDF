using System.Windows;
using System.Windows.Media;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Ленивый рендеринг: контейнер страницы при появлении в визуальном дереве
/// запрашивает растр, при уходе — отпускает его (память контролирует LRU-кэш).
/// </summary>
public static class ViewBehaviors
{
    public static readonly DependencyProperty RenderFullPageProperty =
        DependencyProperty.RegisterAttached("RenderFullPage", typeof(bool), typeof(ViewBehaviors),
            new PropertyMetadata(false, OnRenderFullPageChanged));

    public static void SetRenderFullPage(DependencyObject element, bool value) =>
        element.SetValue(RenderFullPageProperty, value);

    public static bool GetRenderFullPage(DependencyObject element) =>
        (bool)element.GetValue(RenderFullPageProperty);

    private static void OnRenderFullPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.Loaded += (_, _) =>
        {
            if (element.DataContext is PageViewModel page)
                page.EnsureImage(VisualTreeHelper.GetDpi(element).DpiScaleX);
        };
        element.Unloaded += (_, _) =>
        {
            if (element.DataContext is PageViewModel page)
                page.ReleaseImage();
        };
    }

    public static readonly DependencyProperty RenderThumbnailProperty =
        DependencyProperty.RegisterAttached("RenderThumbnail", typeof(bool), typeof(ViewBehaviors),
            new PropertyMetadata(false, OnRenderThumbnailChanged));

    public static void SetRenderThumbnail(DependencyObject element, bool value) =>
        element.SetValue(RenderThumbnailProperty, value);

    public static bool GetRenderThumbnail(DependencyObject element) =>
        (bool)element.GetValue(RenderThumbnailProperty);

    private static void OnRenderThumbnailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.Loaded += (_, _) =>
        {
            if (element.DataContext is PageViewModel page)
                page.EnsureThumbnail();
        };
    }
}
