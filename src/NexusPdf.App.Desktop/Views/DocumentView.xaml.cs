using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views;

public partial class DocumentView : UserControl
{
    private ScrollViewer? _scroller;
    private DocumentViewModel? _vm;
    private bool _syncingThumbSelection;

    public DocumentView()
    {
        InitializeComponent();
        Loaded += (_, _) => HookScroller();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.ScrollToPageRequested -= OnScrollToPage;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = e.NewValue as DocumentViewModel;
        if (_vm != null)
        {
            _vm.ScrollToPageRequested += OnScrollToPage;
            _vm.PropertyChanged += OnVmPropertyChanged;
            UpdatePlacementCursor();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.PendingOverlay))
            UpdatePlacementCursor();
    }

    private void UpdatePlacementCursor() =>
        PagesList.Cursor = _vm?.PendingOverlay != null ? Cursors.Cross : null;

    private void OnPagesPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.PendingOverlay == null) return;
        if (e.OriginalSource is not DependencyObject source) return;

        // Ищем контейнер страницы (Border с DataContext = PageViewModel) вверх по дереву.
        FrameworkElement? pageElement = null;
        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: PageViewModel } fe && fe is Border)
            {
                pageElement = fe;
                break;
            }
            if (node is ListBox)
                break;
        }
        if (pageElement?.DataContext is not PageViewModel page) return;

        var position = e.GetPosition(pageElement);
        var scale = page.DisplayScale;
        if (scale <= 0) return;
        _vm.PlacePendingOverlay(page, position.X / scale, position.Y / scale);
        e.Handled = true;
    }

    private void HookScroller()
    {
        if (_scroller != null) return;
        _scroller = FindDescendant<ScrollViewer>(PagesList);
        if (_scroller == null) return;
        _scroller.ScrollChanged += (_, args) =>
        {
            if (_vm == null) return;
            _vm.ViewportWidth = args.ViewportWidth;
            _vm.ViewportHeight = args.ViewportHeight;
            _vm.UpdateCurrentPage(args.VerticalOffset, args.ViewportHeight);
        };
    }

    private void OnScrollToPage(object? sender, int pageIndex)
    {
        HookScroller();
        if (_vm == null || _scroller == null) return;
        if (_vm.IsOrganizeMode)
        {
            OrganizeList.ScrollIntoView(_vm.Pages[Math.Clamp(pageIndex, 0, _vm.Pages.Count - 1)]);
            return;
        }
        _scroller.ScrollToVerticalOffset(_vm.GetPageTop(pageIndex));
    }

    private void OnPagesMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm == null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _vm.SetZoom(_vm.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _syncingThumbSelection) return;
        // Реагируем только на выбор пользователем (мышь/клавиатура внутри списка).
        if (!ThumbList.IsKeyboardFocusWithin && !ThumbList.IsMouseOver) return;
        if (ThumbList.SelectedIndex >= 0)
        {
            _syncingThumbSelection = true;
            try
            {
                _vm.GoToPage(ThumbList.SelectedIndex + 1);
            }
            finally
            {
                _syncingThumbSelection = false;
            }
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                _vm.FindPreviousCommand.Execute(null);
            else
                _vm.FindNextCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.ToggleFindCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnOrganizeKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Delete)
        {
            _vm.DeleteSelectedCommand.Execute(OrganizeList.SelectedItems);
            e.Handled = true;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
