using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusPdf.App.Desktop.Localization;
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
            _vm.FormComboRequested -= OnFormComboRequested;
            _vm.ExternalLinkRequested -= OnExternalLinkRequested;
        }
        CloseComboPopup();
        _vm = e.NewValue as DocumentViewModel;
        if (_vm != null)
        {
            _vm.ScrollToPageRequested += OnScrollToPage;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.FormComboRequested += OnFormComboRequested;
            _vm.ExternalLinkRequested += OnExternalLinkRequested;
            UpdatePlacementCursor();
        }
    }

    /// <summary>
    /// Внешний адрес из документа открывается ТОЛЬКО после подтверждения и с
    /// показом полного адреса: документ — недоверенный источник.
    /// </summary>
    private void OnExternalLinkRequested(object? sender, string uri)
    {
        var safe = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                   (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps ||
                    parsed.Scheme == Uri.UriSchemeMailto);
        if (!safe)
        {
            ErrorDialog.Show(Window.GetWindow(this), Loc.Get("LinkTitle"),
                Loc.Get("LinkUnsupportedScheme"), uri);
            return;
        }

        if (!ConfirmDialog.Ask(Window.GetWindow(this), Loc.Get("LinkTitle"),
                Loc.Get("LinkConfirmQuestion"), uri, Loc.Get("LinkOpen")))
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed!.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть ссылку");
            ErrorDialog.Show(Window.GetWindow(this), Loc.Get("LinkTitle"), ex.Message, uri);
        }
    }

    // ----- Собственный попап выпадающего списка формы -----

    private System.Windows.Controls.Primitives.Popup? _comboPopup;

    private void OnFormComboRequested(object? sender, DocumentViewModel.FormComboRequest request)
    {
        CloseComboPopup();
        var vm = _vm;
        if (vm == null) return;

        var list = new ListBox
        {
            ItemsSource = request.Combo.Options,
            MaxHeight = 240,
            MinWidth = Math.Max(120, request.Combo.WidthPt * request.Page.DisplayScale),
        };
        list.SetResourceReference(BackgroundProperty, "InputBg");
        list.SetResourceReference(ForegroundProperty, "TextBrush");
        list.SelectedIndex = request.Combo.SelectedIndex; // ДО подписки: без ложного срабатывания
        list.SelectionChanged += async (_, _) =>
        {
            var index = list.SelectedIndex;
            CloseComboPopup();
            if (index >= 0)
                await vm.FormComboSelectAsync(request.Page, request.Combo, index, request.DpiScale);
        };

        var container = PagesList.ItemContainerGenerator.ContainerFromItem(request.Page) as FrameworkElement;
        var border = new Border
        {
            Child = list,
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        _comboPopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = container ?? PagesList,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = request.Combo.XPt * request.Page.DisplayScale,
            VerticalOffset = (request.Combo.YPt + request.Combo.HeightPt) * request.Page.DisplayScale,
            StaysOpen = false,
            Child = border,
            IsOpen = true,
        };
    }

    private void CloseComboPopup()
    {
        if (_comboPopup != null)
        {
            _comboPopup.IsOpen = false;
            _comboPopup = null;
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
        if (_vm == null) return;

        // Режим заполнения формы: клик уходит в поле PDF.
        if (_vm.IsFormMode && _vm.PendingOverlay == null)
        {
            var formHit = FindPageAt(e.OriginalSource);
            if (formHit != null)
            {
                var (formPage, formElement) = formHit.Value;
                var formScale = formPage.DisplayScale;
                if (formScale > 0)
                {
                    var formPos = e.GetPosition(formElement);
                    var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                    _ = _vm.FormClickAsync(formPage, formPos.X / formScale, formPos.Y / formScale, dpi);
                    PagesList.Focus(); // клавиатура должна идти в поле
                    e.Handled = true;
                }
            }
            return;
        }

        if (_vm.PendingOverlay == null)
        {
            // Обычный просмотр: клик по ссылке — переход, иначе начало выделения текста.
            var readHit = FindPageAt(e.OriginalSource);
            if (readHit == null) return;
            var (readPage, readElement) = readHit.Value;
            var readScale = readPage.DisplayScale;
            if (readScale <= 0) return;
            var readPos = e.GetPosition(readElement);

            if (_vm.LinkAt(readPage, readPos.X, readPos.Y) is { } link)
            {
                _vm.ActivateLink(link);
                e.Handled = true;
                return;
            }

            _selectionPage = readPage;
            _selectionElement = readElement;
            _selectionStarted = false;
            _ = BeginSelectionAsync(readPage, readPos.X / readScale, readPos.Y / readScale);
            PagesList.Focus(); // Ctrl+C должен дойти до документа
            return;
        }

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

    // ----- Выделение текста и ссылки -----

    private PageViewModel? _selectionPage;
    private FrameworkElement? _selectionElement;
    private bool _selectionStarted;

    private async Task BeginSelectionAsync(PageViewModel page, double xPt, double yPt)
    {
        if (_vm == null) return;
        _selectionStarted = await _vm.BeginTextSelectionAsync(page, xPt, yPt);
        if (!_selectionStarted)
            _vm.ClearTextSelection(); // клик по пустому месту снимает прежнее выделение
    }

    private void OnPagesPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Протяжка выделения текста.
        if (_selectionPage != null && _selectionElement != null && _vm != null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _selectionPage = null;
                _selectionElement = null;
            }
            else if (_selectionStarted)
            {
                var scale = _selectionPage.DisplayScale;
                if (scale > 0)
                {
                    var pos = e.GetPosition(_selectionElement);
                    _ = _vm.UpdateTextSelectionAsync(_selectionPage, pos.X / scale, pos.Y / scale);
                }
                e.Handled = true;
                return;
            }
        }

        // Курсор-рука над ссылкой (попадание проверяется по кэшу, без вызовов движка).
        if (_vm != null && _dragPage == null && FindPageAt(e.OriginalSource) is { } hoverHit)
        {
            var (hoverPage, hoverElement) = hoverHit;
            _ = _vm.EnsureLinksAsync(hoverPage);
            var hoverPos = e.GetPosition(hoverElement);
            var overLink = _vm.LinkAt(hoverPage, hoverPos.X, hoverPos.Y) != null;
            var wanted = overLink ? Cursors.Hand
                : _vm.PendingOverlay != null ? Cursors.Cross
                : Cursors.IBeam;
            if (PagesList.Cursor != wanted)
                PagesList.Cursor = wanted;
        }

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
        if (_selectionPage != null)
        {
            _selectionPage = null;
            _selectionElement = null;
            // Выделение остаётся на экране до следующего клика — его можно копировать.
        }
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

    private void OnPagesTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm is not { IsFormMode: true, HasActiveFormPage: true }) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var c in e.Text)
        {
            if (!char.IsControl(c))
                _ = _vm.FormCharAsync(c, dpi);
        }
        e.Handled = true;
    }

    private void OnPagesKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        // Копирование и выделение всей страницы работают в обычном просмотре.
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_vm.CopySelectionCommand.CanExecute(null))
            {
                _vm.CopySelectionCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && !_vm.IsFormMode)
        {
            _vm.SelectAllOnPageCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _vm.HasSelection)
        {
            _vm.ClearTextSelection();
            e.Handled = true;
            return;
        }

        // Дальше — ввод в поля формы; до первого клика в поле клавиатура
        // остаётся у прокрутки и навигации.
        if (_vm is not { IsFormMode: true, HasActiveFormPage: true }) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        switch (e.Key)
        {
            case Key.Back:
                _ = _vm.FormCharAsync((char)8, dpi);
                e.Handled = true;
                break;
            case Key.Delete:
                _ = _vm.FormKeyAsync(0x2E, dpi);
                e.Handled = true;
                break;
            case Key.Left:
                _ = _vm.FormKeyAsync(0x25, dpi);
                e.Handled = true;
                break;
            case Key.Right:
                _ = _vm.FormKeyAsync(0x27, dpi);
                e.Handled = true;
                break;
            case Key.Home:
                _ = _vm.FormKeyAsync(0x24, dpi);
                e.Handled = true;
                break;
            case Key.End:
                _ = _vm.FormKeyAsync(0x23, dpi);
                e.Handled = true;
                break;
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
