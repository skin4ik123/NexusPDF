using System.Windows;
using System.Windows.Controls;
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

    private static readonly DependencyProperty AttachedPageProperty =
        DependencyProperty.RegisterAttached("AttachedPage", typeof(PageViewModel), typeof(ViewBehaviors));

    private static void OnRenderFullPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true)
        {
            element.Loaded += OnPageLoaded;
            element.Unloaded += OnPageUnloaded;
            element.DataContextChanged += OnPageContextChanged;
            UpdateAttachedPage(element);
        }
        else
        {
            element.Loaded -= OnPageLoaded;
            element.Unloaded -= OnPageUnloaded;
            element.DataContextChanged -= OnPageContextChanged;
            DetachPage(element);
        }
    }

    private static void OnPageLoaded(object sender, RoutedEventArgs e) => UpdateAttachedPage((FrameworkElement)sender);

    private static void OnPageUnloaded(object sender, RoutedEventArgs e) => DetachPage((FrameworkElement)sender);

    private static void OnPageContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateAttachedPage((FrameworkElement)sender);

    private static void UpdateAttachedPage(FrameworkElement element)
    {
        // Replace/Move and virtualized scrolling can recycle a loaded template
        // without another Loaded event. Attach the new model immediately and
        // detach the model we actually attached, rather than the new context.
        var page = element.IsLoaded ? element.DataContext as PageViewModel : null;
        if (ReferenceEquals(element.GetValue(AttachedPageProperty), page)) return;
        DetachPage(element);
        if (page == null) return;
        element.SetValue(AttachedPageProperty, page);
        var dpi = VisualTreeHelper.GetDpi(element).DpiScaleX;
        page.Owner.SetDpiScale(dpi);
        page.Attach(dpi);
    }

    private static void DetachPage(FrameworkElement element)
    {
        var page = element.GetValue(AttachedPageProperty) as PageViewModel;
        element.ClearValue(AttachedPageProperty);
        page?.Detach();
    }

    public static readonly DependencyProperty RenderThumbnailProperty =
        DependencyProperty.RegisterAttached("RenderThumbnail", typeof(bool), typeof(ViewBehaviors),
            new PropertyMetadata(false, OnRenderThumbnailChanged));

    public static void SetRenderThumbnail(DependencyObject element, bool value) =>
        element.SetValue(RenderThumbnailProperty, value);

    public static bool GetRenderThumbnail(DependencyObject element) =>
        (bool)element.GetValue(RenderThumbnailProperty);

    /// <summary>
    /// Миниатюра запрашивается, только когда карточка ВИДНА.
    ///
    /// Организатор страниц раскладывает карточки WrapPanel'ом, а он не
    /// виртуализируется: на документе в 333 страницы появляются все 333
    /// карточки сразу. Раньше каждая тут же просила миниатюру, и единственная
    /// очередь PDFium забивалась на несколько секунд — из-за этого «страницы
    /// очень долго рендерятся», а сама читаемая страница ждала за спиной
    /// трёхсот невидимых.
    ///
    /// Теперь запрос идёт при появлении в поле зрения: и при первом показе, и
    /// при прокрутке.
    /// </summary>
    private static void OnRenderThumbnailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true)
        {
            element.Loaded += OnThumbnailLoaded;
            element.Unloaded += OnThumbnailUnloaded;
            element.DataContextChanged += OnThumbnailContextChanged;
            if (element.IsLoaded) Watch(element);
        }
        else
        {
            element.Loaded -= OnThumbnailLoaded;
            element.Unloaded -= OnThumbnailUnloaded;
            element.DataContextChanged -= OnThumbnailContextChanged;
            Forget(element);
        }
    }

    private static void OnThumbnailLoaded(object sender, RoutedEventArgs e) => Watch((FrameworkElement)sender);

    private static void OnThumbnailUnloaded(object sender, RoutedEventArgs e) => Forget((FrameworkElement)sender);

    private static void OnThumbnailContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        Forget(element);
        if (element.IsLoaded) Watch(element);
    }

    /// <summary>Карточки, ждущие показа, по своему прокручиваемому списку.</summary>
    private static readonly Dictionary<ScrollViewer, HashSet<FrameworkElement>> Pending = new();

    private static void Watch(FrameworkElement element)
    {
        Forget(element);
        var scroller = FindScroller(element);
        if (scroller == null)
        {
            // Списка с прокруткой нет — значит, карточка и так на виду.
            Request(element);
            return;
        }

        if (!Pending.TryGetValue(scroller, out var set))
        {
            Pending[scroller] = set = new HashSet<FrameworkElement>();
            scroller.ScrollChanged += OnThumbnailScrollChanged;
        }
        set.Add(element);
        // Первый показ: список уже прокручен туда, где стоял.
        element.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => RequestVisible(scroller)));
    }

    private static void Forget(FrameworkElement element)
    {
        foreach (var (scroller, set) in Pending.ToArray())
        {
            set.Remove(element);
            StopWatchingIfEmpty(scroller, set);
        }
    }

    private static void OnThumbnailScrollChanged(object sender, ScrollChangedEventArgs e) =>
        RequestVisible((ScrollViewer)sender);

    private static void StopWatchingIfEmpty(ScrollViewer scroller, HashSet<FrameworkElement> set)
    {
        if (set.Count != 0) return;
        scroller.ScrollChanged -= OnThumbnailScrollChanged;
        Pending.Remove(scroller);
    }

    private static void RequestVisible(ScrollViewer scroller)
    {
        if (!Pending.TryGetValue(scroller, out var set) || set.Count == 0) return;

        // С запасом в экран сверху и снизу: пока человек листает, соседние
        // карточки успевают подготовиться, и он не видит пустых мест.
        var viewport = new Rect(0, -scroller.ViewportHeight,
            scroller.ViewportWidth, scroller.ViewportHeight * 3);

        foreach (var element in set.ToList())
        {
            if (!element.IsLoaded || !element.IsVisible) continue;
            try
            {
                var bounds = element.TransformToAncestor(scroller)
                    .TransformBounds(new Rect(element.RenderSize));
                if (!viewport.IntersectsWith(bounds)) continue;
            }
            catch (InvalidOperationException)
            {
                continue; // карточка уже вне дерева
            }

            Request(element);
            set.Remove(element);
        }
        StopWatchingIfEmpty(scroller, set);
    }

    private static void Request(FrameworkElement element)
    {
        if (element.DataContext is PageViewModel page)
            page.EnsureThumbnail();
    }

    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node != null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller) return scroller;
        }
        return null;
    }
}
