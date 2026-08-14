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
        {
            UpdatePlacementCursor();
            if (_vm?.PendingOverlay == null)
                ResetDrag(); // Esc/отмена посреди растягивания — рамка не должна залипнуть
        }
    }

    private void ResetDrag()
    {
        if (_dragPage != null)
            _dragPage.DragPreviewRect = null;
        _dragPage = null;
        _dragElement = null;
        if (PagesList.IsMouseCaptured)
            PagesList.ReleaseMouseCapture();
    }

    private void OnPagesLostCapture(object sender, MouseEventArgs e) => ResetDrag();

    private void UpdatePlacementCursor() =>
        PagesList.Cursor = _vm?.PendingOverlay != null ? Cursors.Cross : null;

    private PageViewModel? _dragPage;
    private FrameworkElement? _dragElement;
    private Point _dragStartPt;

    private (PageViewModel Page, FrameworkElement Element)? FindPageAt(object originalSource)
    {
        if (originalSource is not DependencyObject source) return null;
        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Border { DataContext: PageViewModel page } border)
                return (page, border);
            if (node is ListBox)
                break;
        }
        return null;
    }

    private void OnPagesPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.PendingOverlay == null) return;
        var hit = FindPageAt(e.OriginalSource);
        if (hit == null) return;
        var (page, element) = hit.Value;
        var scale = page.DisplayScale;
        if (scale <= 0) return;
        var position = e.GetPosition(element);

        if (_vm.PendingOverlay.RectFactory != null)
        {
            // Начало растягивания рамки.
            _dragPage = page;
            _dragElement = element;
            _dragStartPt = new Point(position.X / scale, position.Y / scale);
            PagesList.CaptureMouse();
        }
        else
        {
            _vm.PlacePendingOverlay(page, position.X / scale, position.Y / scale);
        }
        e.Handled = true;
    }

    private void OnPagesPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragPage == null || _dragElement == null) return;
        // Кнопку отпустили вне окна / capture отобран системой — сбрасываем,
        // иначе устаревший _dragStartPt породил бы ложную аннотацию.
        if (e.LeftButton != MouseButtonState.Pressed || !PagesList.IsMouseCaptured)
        {
            ResetDrag();
            return;
        }
        _dragPage.DragPreviewRect = DragRectPt(e);
    }

    private void OnPagesPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragPage == null || _dragElement == null) return;
        var page = _dragPage;
        var rect = DragRectPt(e);
        page.DragPreviewRect = null;
        _dragPage = null;
        _dragElement = null;
        PagesList.ReleaseMouseCapture();
        _vm?.PlacePendingRect(page, rect);
        e.Handled = true;
    }

    private Rect DragRectPt(MouseEventArgs e)
    {
        var scale = _dragPage!.DisplayScale;
        var position = e.GetPosition(_dragElement);
        var current = new Point(
            Math.Clamp(position.X / scale, 0, _dragPage.SizePt.WidthPoints),
            Math.Clamp(position.Y / scale, 0, _dragPage.SizePt.HeightPoints));
        return new Rect(_dragStartPt, current);
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
