using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
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
        Loaded += (_, _) =>
        {
            HookScroller();
            // Открылось модальное окно (или ушли в другую программу) — правку
            // на месте надо завершить. Само поле этого не заметит: оно живёт
            // отдельным окном поверх страницы и продолжало бы висеть поверх
            // того, что открылось.
            if (Window.GetWindow(this) is { } window)
            {
                window.Deactivated -= OnOwnerDeactivated;
                window.Deactivated += OnOwnerDeactivated;
            }
        };
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => CloseInlineEditor();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.ScrollToPageRequested -= OnScrollToPage;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.FormComboRequested -= OnFormComboRequested;
            _vm.ExternalLinkRequested -= OnExternalLinkRequested;
            _vm.InlineEditRequested -= OnInlineEditRequested;
        }
        CloseComboPopup();
        _vm = e.NewValue as DocumentViewModel;
        if (_vm != null)
        {
            _vm.ScrollToPageRequested += OnScrollToPage;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.FormComboRequested += OnFormComboRequested;
            _vm.ExternalLinkRequested += OnExternalLinkRequested;
            _vm.InlineEditRequested += OnInlineEditRequested;
            UpdatePlacementCursor();
            // Оглавление читается сразу: без него не видно, есть ли у документа
            // вкладка «Оглавление» вообще.
            _ = _vm.EnsureBookmarksAsync();
        }
    }

    /// <summary>Выбор узла оглавления — переход на его страницу.</summary>
    private void OnBookmarkSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm == null) return;
        if (e.NewValue is ViewModels.BookmarkViewModel bookmark)
            _vm.GoToBookmark(bookmark);
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
        // Поле правки на месте — отдельное окно поверх страницы, само оно не
        // закроется. Начали другое дело — правку надо завершить, иначе поле
        // висит поверх интерфейса и накрывает собой то, что открылось.
        if (e.PropertyName is nameof(DocumentViewModel.PendingOverlay)
            or nameof(DocumentViewModel.IsDrawing)
            or nameof(DocumentViewModel.IsFormMode)
            or nameof(DocumentViewModel.IsOrganizeMode)
            or nameof(DocumentViewModel.IsBusy))
        {
            CloseInlineEditor();
        }

        if (e.PropertyName == nameof(DocumentViewModel.PendingOverlay))
        {
            UpdatePlacementCursor();
            if (_vm?.PendingOverlay == null)
                ResetDrag(); // Esc/отмена посреди растягивания — рамка не должна залипнуть
        }
        else if (e.PropertyName == nameof(DocumentViewModel.IsDrawing))
        {
            UpdatePlacementCursor();
            if (_vm?.IsDrawing != true)
                ResetDrag(); // инструмент выключили посреди штриха
        }
    }

    private void ResetDrag()
    {
        // Захват отобрали (Alt+Tab, чужое окно) — рука не должна остаться
        // «зажатой»: страница поехала бы за курсором без нажатой кнопки.
        _panning = false;
        if (_dragPage != null)
            _dragPage.DragPreviewRect = null;
        _dragPage = null;
        _dragElement = null;
        // Незаконченный штрих не должен остаться висеть поверх страницы.
        if (_drawPage != null)
        {
            _drawPage.DrawPreview = null;
            _vm?.CancelStroke(_drawPage);
            _drawPage = null;
            _drawElement = null;
        }
        if (PagesList.IsMouseCaptured)
            PagesList.ReleaseMouseCapture();
    }

    private PageViewModel? _drawPage;
    private FrameworkElement? _drawElement;

    private void OnPagesLostCapture(object sender, MouseEventArgs e) => ResetDrag();

    private void UpdatePlacementCursor() =>
        PagesList.Cursor = _vm?.PendingOverlay != null || _vm?.IsDrawing == true
            ? Cursors.Cross
            : null;

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

    // ----- Правка текста прямо на странице -----

    private InlineTextEditor? _inlineEditor;

    /// <summary>
    /// Двойной клик по тексту открывает его на месте. Это основной путь правки:
    /// отдельный инструмент и модальное окно остались, но нужны теперь только
    /// когда в строку не попасть мышью.
    /// </summary>
    private bool TryBeginInlineEdit(MouseButtonEventArgs e)
    {
        if (_vm == null || _vm.IsBusy || _vm.IsFormMode || _vm.IsDrawing) return false;
        if (_vm.PendingOverlay != null) return false;

        var hit = FindPageAt(e.OriginalSource);
        if (hit == null) return false;
        var (page, element) = hit.Value;
        var scale = page.DisplayScale;
        if (scale <= 0) return false;

        var position = e.GetPosition(element);
        Serilog.Log.Debug("Правка на месте: страница {Page}, точка {X:0.#};{Y:0.#} пт, масштаб {Scale:0.###}",
            page.LogicalIndex + 1, position.X / scale, position.Y / scale, scale);
        _ = BeginInlineEditAsync(page, element, position.X / scale, position.Y / scale, scale);
        return true;
    }

    /// <summary>
    /// Просьба открыть правку от команды или контекстного меню — без мыши.
    /// Путь дальше тот же, что у двойного клика: одна правка текста, одна
    /// реализация.
    /// </summary>
    private void OnInlineEditRequested(object? sender, DocumentViewModel.InlineEditRequest request)
    {
        var element = FindPageElement(request.Page);
        if (element == null)
        {
            Serilog.Log.Warning("Правка на месте: страница {Page} не найдена на экране",
                request.Page.LogicalIndex + 1);
            return;
        }
        var scale = request.Page.DisplayScale;
        if (scale <= 0) return;
        _ = BeginInlineEditAsync(request.Page, element, request.XPt, request.YPt, scale, request.Explicit);
    }

    /// <summary>
    /// Рамка страницы на экране. Страница может быть за пределами видимой
    /// части списка — тогда её контейнера ещё нет, и его надо создать
    /// прокруткой, иначе поле ввода открывать не над чем.
    /// </summary>
    private FrameworkElement? FindPageElement(PageViewModel page)
    {
        var container = PagesList.ItemContainerGenerator.ContainerFromItem(page) as FrameworkElement;
        if (container == null)
        {
            PagesList.ScrollIntoView(page);
            PagesList.UpdateLayout();
            container = PagesList.ItemContainerGenerator.ContainerFromItem(page) as FrameworkElement;
        }
        return container == null ? null : FindPageBorder(container, page);
    }

    private static FrameworkElement? FindPageBorder(DependencyObject node, PageViewModel page)
    {
        if (node is Border { DataContext: PageViewModel found } border && ReferenceEquals(found, page))
            return border;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindPageBorder(VisualTreeHelper.GetChild(node, i), page) is { } hit)
                return hit;
        }
        return null;
    }

    private async Task BeginInlineEditAsync(
        PageViewModel page, FrameworkElement element, double xPt, double yPt, double scale,
        bool explicitRequest = false)
    {
        if (_vm == null) return;
        CloseInlineEditor();

        DocumentViewModel.InlineTextTarget? target;
        try
        {
            target = await _vm.FindInlineTextAsync(page, xPt, yPt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Поиск текста для правки на месте");
            return;
        }
        if (target == null)
        {
            Serilog.Log.Debug("Правка на месте: в точке {X:0.#};{Y:0.#} пт текста нет", xPt, yPt);
            // Двойной клик по пустому месту — обычное дело, ругаться не за что
            // (выделение слова при этом уже отработало). А вот когда правку
            // попросили кнопкой, промолчать нельзя: человек ждёт либо поля
            // ввода, либо объяснения, почему его нет.
            if (explicitRequest)
                _vm.ExplainNothingToEdit?.Invoke(page);
            return;
        }
        Serilog.Log.Debug("Правка на месте: найдено «{Text}», рамка {R}", target.Text, target.RectPt);

        // Строку внутри вложенного объекта иначе не изменить, и цена у такой
        // правки заметная — страница станет изображением. Решает человек, и
        // спросить надо ДО того, как он наберёт текст, а не после.
        if (target.NeedsRasterErase &&
            !ConfirmDialog.Ask(Window.GetWindow(this), Loc.Get("TextEditTitle"),
                Loc.Get("TextEditRasterNeeded"), Loc.Get("TextEditRasterNeededHint"),
                Loc.Get("TextEditRasterAccept")))
        {
            return;
        }

        // Рамка строки — в пунктах; на экране она в единицах WPF и масштабе страницы.
        var rect = new Rect(
            target.RectPt.X * scale, target.RectPt.Y * scale,
            target.RectPt.Width * scale, target.RectPt.Height * scale);

        var editor = InlineTextEditor.Open(
            element, rect, target.Text, target.FontName, target.FontSizePt, target.ColorArgb, scale);
        if (editor == null) return;
        _inlineEditor = editor;

        var captured = target;
        editor.Finished += async (_, result) =>
        {
            _inlineEditor = null;
            if (result == null) return;
            if (result.Text == captured.Text && !result.StyleChanged) return;

            // Проверять исходный шрифт нужно, только если его оставляют. Когда
            // гарнитуру меняют, буквы будет рисовать НОВЫЙ шрифт из каталога —
            // он с кириллицей и латиницей справляется по определению, и старая
            // проверка запрещала бы ровно тот случай, ради которого шрифт и
            // меняют: во встроенном подмножестве нужных букв нет.
            // Строка внутри вложенного объекта пишется заново шрифтом из
            // каталога, а не шрифтом исходного объекта, — спрашивать старый
            // шрифт, умеет ли он новые буквы, здесь бессмысленно: рисовать
            // будет не он. Раньше эта проверка запрещала как раз то, ради чего
            // такая правка и делается.
            if (!result.StyleChanged && !captured.NeedsRasterErase)
            {
                bool canRender;
                try
                {
                    canRender = await _vm.CanRenderInlineAsync(
                        page, captured, result.Text, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Проверка шрифта при правке на месте");
                    return;
                }
                if (!canRender)
                {
                    InfoDialog.Show(Window.GetWindow(this), Loc.Get("TextEditTitle"),
                        Loc.Get("TextEditFontMissingGlyphs"));
                    return;
                }
            }

            _vm.ApplyInlineText(page, captured, result.Text,
                result.FontFamily, result.Bold, result.Italic, result.FontSizePt, result.ColorArgb,
                VisualTreeHelper.GetDpi(this).DpiScaleX);
        };
    }

    /// <summary>
    /// Клик мимо строки ПРИМЕНЯЕТ правку, а не выбрасывает её: так ведут себя
    /// все поля правки на месте, и терять набранное из-за случайного клика
    /// человек не обязан. Отказаться можно клавишей Esc.
    /// </summary>
    private void CloseInlineEditor()
    {
        var editor = _inlineEditor;
        _inlineEditor = null;
        editor?.CommitFromOutside();
    }

    private void OnPagesPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;

        // Двойной клик обрабатывается ДО выделения текста: иначе второй щелчок
        // уходил бы в выделение слова и правка не начиналась бы никогда.
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && TryBeginInlineEdit(e))
        {
            e.Handled = true;
            return;
        }

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

        // Режим рисования: мышь ведёт штрих и не выделяет текст.
        if (_vm.IsDrawing && _vm.PendingOverlay == null)
        {
            var drawHit = FindPageAt(e.OriginalSource);
            if (drawHit == null) return;
            var (drawPage, drawElement) = drawHit.Value;
            var drawScale = drawPage.DisplayScale;
            if (drawScale <= 0) return;
            var drawPos = e.GetPosition(drawElement);
            _drawPage = drawPage;
            _drawElement = drawElement;
            _vm.BeginStroke(drawPage, drawPos.X / drawScale, drawPos.Y / drawScale);
            PagesList.CaptureMouse();
            PagesList.Focus();
            e.Handled = true;
            return;
        }

        if (_vm.PendingOverlay == null)
        {
            // Обычный просмотр: клик по ссылке — переход, иначе начало выделения текста.
            var readHit = FindPageAt(e.OriginalSource);
            if (readHit == null)
            {
                // Мимо страницы — поле вокруг неё. Тащить оттуда можно только
                // сам документ, и это ровно то, чего от пустого места и ждут.
                if (BeginPan(e)) e.Handled = true;
                return;
            }
            var (readPage, readElement) = readHit.Value;
            var readScale = readPage.DisplayScale;
            if (readScale <= 0) return;
            var readPos = e.GetPosition(readElement);
            var readPt = new Point(readPos.X / readScale, readPos.Y / readScale);

            // Выбранный объект перехватывает клик первым: иначе его нельзя
            // ни подвинуть, ни растянуть — рамка была бы украшением.
            var handle = _vm.HitObjectHandle(readPage, readPt.X, readPt.Y);
            if (handle != NexusPdf.Ux.ResizeHandle.None)
            {
                _objectPage = readPage;
                _objectElement = readElement;
                _objectStartPt = readPt;
                _vm.BeginObjectDrag(handle);
                PagesList.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_vm.SelectObjectAt(readPage, readPt.X, readPt.Y))
            {
                _objectPage = readPage;
                _objectElement = readElement;
                _objectStartPt = readPt;
                _vm.BeginObjectDrag(NexusPdf.Ux.ResizeHandle.Move);
                PagesList.CaptureMouse();
                PagesList.Focus();      // Delete и стрелки должны дойти до документа
                e.Handled = true;
                return;
            }

            // Клик мимо объектов снимает выделение: рамка не должна висеть
            // на объекте, о котором пользователь уже забыл.
            _vm.ClearObjectSelection();

            if (_vm.LinkAt(readPage, readPos.X, readPos.Y) is { } link)
            {
                _vm.ActivateLink(link);
                e.Handled = true;
                return;
            }

            // Пустое место страницы: там нечего выделять, зато есть что
            // подвинуть. Проверка по тому же кэшу, что и курсор, — рука не
            // может обещать одно, а нажатие делать другое.
            _ = _vm.EnsureTextAreasAsync(readPage);
            if (!_vm.HasTextAt(readPage, readPos.X, readPos.Y) && BeginPan(e))
            {
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

    // Перетаскивание выбранного объекта
    private PageViewModel? _objectPage;
    private FrameworkElement? _objectElement;
    private Point _objectStartPt;

    // ----- Перестановка разделов правой панели -----

    private Services.Ux.ToolGroup? _groupDrag;
    private FrameworkElement? _groupGrip;

    /// <summary>
    /// Перестановка разделов сделана своими руками, а не библиотекой
    /// перетаскивания: та вешает обработчики на элемент-источник, а внутри
    /// заголовка Expander мышь захватывает его ToggleButton, и события до
    /// источника уже не доходят. Здесь захват берётся сразу на ручке, поэтому
    /// движение отслеживается до самого отпускания.
    /// </summary>
    private void OnGroupGripDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Services.Ux.ToolGroup group } grip) return;
        _groupDrag = group;
        _groupGrip = grip;
        grip.Opacity = 1.0;
        grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGroupGripMove(object sender, MouseEventArgs e)
    {
        if (_groupDrag == null || _groupGrip == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
            ReleaseGroupGrip();
    }

    private void OnGroupGripUp(object sender, MouseButtonEventArgs e)
    {
        if (_groupDrag == null) return;
        var group = _groupDrag;
        var index = GroupIndexAt(e.GetPosition(ToolGroups));
        ReleaseGroupGrip();
        if (Window.GetWindow(this) is MainWindow window)
            window.ViewModel.Tools.MoveGroup(group, index);
        e.Handled = true;
    }

    private void OnGroupGripLost(object sender, MouseEventArgs e) => ReleaseGroupGrip();

    private void ReleaseGroupGrip()
    {
        if (_groupGrip != null)
        {
            _groupGrip.Opacity = 0.5;
            if (_groupGrip.IsMouseCaptured)
                _groupGrip.ReleaseMouseCapture();
        }
        _groupDrag = null;
        _groupGrip = null;
    }

    /// <summary>
    /// Куда встанет раздел, если отпустить здесь. Считается по серединам
    /// заголовков: пока курсор выше середины раздела, вставка идёт ПЕРЕД ним.
    /// </summary>
    private int GroupIndexAt(Point position)
    {
        for (var i = 0; i < ToolGroups.Items.Count; i++)
        {
            if (ToolGroups.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), ToolGroups).Y;
            if (position.Y < top + container.ActualHeight / 2)
                return i;
        }
        return ToolGroups.Items.Count;
    }

    // ----- Ведение страницы рукой -----

    private bool _panning;
    private Point _panStart;
    private Point _panOffset;

    /// <summary>
    /// Есть ли куда двигать. Рука над страницей, которая и так помещается
    /// целиком, обманывает: нажмёшь — ничего не произойдёт.
    /// </summary>
    private bool CanPan()
    {
        HookScroller();
        if (_scroller == null || _vm == null) return false;
        if (_vm.IsOrganizeMode || _vm.IsFormMode || _vm.IsDrawing || _vm.PendingOverlay != null)
            return false;
        return _scroller.ExtentHeight > _scroller.ViewportHeight + 1 ||
               _scroller.ExtentWidth > _scroller.ViewportWidth + 1;
    }

    private bool BeginPan(MouseButtonEventArgs e)
    {
        if (!CanPan() || _scroller == null) return false;
        _panning = true;
        _panStart = e.GetPosition(PagesList);
        _panOffset = new Point(_scroller.HorizontalOffset, _scroller.VerticalOffset);
        PagesList.Cursor = HandCursors.Grab;
        PagesList.CaptureMouse();
        PagesList.Focus();
        return true;
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        if (PagesList.IsMouseCaptured)
            PagesList.ReleaseMouseCapture();
        PagesList.Cursor = CanPan() ? HandCursors.Open : Cursors.Arrow;
    }

    private void OnPagesPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Перетаскивание выбранного объекта — раньше всего остального:
        // мышь захвачена именно им.
        if (_objectPage != null && _objectElement != null && _vm != null)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !PagesList.IsMouseCaptured)
            {
                EndObjectDrag(e, commit: false);
                return;
            }
            var scale = _objectPage.DisplayScale;
            if (scale > 0)
            {
                var pos = e.GetPosition(_objectElement);
                _vm.UpdateObjectDrag(pos.X / scale - _objectStartPt.X, pos.Y / scale - _objectStartPt.Y);
            }
            e.Handled = true;
            return;
        }

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

        // Ведение штриха.
        if (_drawPage != null && _drawElement != null && _vm != null)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !PagesList.IsMouseCaptured)
            {
                ResetDrag();
                return;
            }
            var drawScale = _drawPage.DisplayScale;
            if (drawScale > 0)
            {
                var pos = e.GetPosition(_drawElement);
                _vm.ContinueStroke(_drawPage, pos.X / drawScale, pos.Y / drawScale,
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            }
            e.Handled = true;
            return;
        }

        // Ведение страницы рукой.
        if (_panning)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !PagesList.IsMouseCaptured)
            {
                EndPan();
                return;
            }
            var now = e.GetPosition(PagesList);
            _scroller!.ScrollToHorizontalOffset(_panOffset.X - (now.X - _panStart.X));
            _scroller.ScrollToVerticalOffset(_panOffset.Y - (now.Y - _panStart.Y));
            e.Handled = true;
            return;
        }

        // Курсор-рука над ссылкой (попадание проверяется по кэшу, без вызовов движка).
        if (_vm != null && _dragPage == null)
        {
            Cursor wanted;
            if (FindPageAt(e.OriginalSource) is { } hoverHit)
            {
                var (hoverPage, hoverElement) = hoverHit;
                _ = _vm.EnsureLinksAsync(hoverPage);
                var hoverPos = e.GetPosition(hoverElement);
                _ = _vm.EnsureTextAreasAsync(hoverPage);
                var overLink = _vm.LinkAt(hoverPage, hoverPos.X, hoverPos.Y) != null;
                // Текстовый курсор — ТОЛЬКО над текстом. Раньше он стоял над всей
                // страницей, и программа выглядела так, будто везде можно печатать.
                wanted = overLink ? Cursors.Hand
                    : _vm.PendingOverlay != null ? Cursors.Cross
                    : _vm.HasTextAt(hoverPage, hoverPos.X, hoverPos.Y) ? Cursors.IBeam
                    // Пустое место страницы, которая не влезла в окно: отсюда
                    // её можно взять рукой и подвинуть.
                    : CanPan() ? HandCursors.Open
                    : Cursors.Arrow;
            }
            else
            {
                // Поле вокруг страницы: там ни текста, ни ссылок — только рука.
                wanted = _vm.PendingOverlay != null ? Cursors.Cross
                    : CanPan() ? HandCursors.Open
                    : Cursors.Arrow;
            }
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
        if (_panning)
        {
            EndPan();
            e.Handled = true;
            return;
        }

        if (_objectPage != null)
        {
            EndObjectDrag(e, commit: true);
            e.Handled = true;
            return;
        }

        if (_drawPage != null && _vm != null)
        {
            var strokePage = _drawPage;
            _drawPage = null;
            _drawElement = null;
            if (PagesList.IsMouseCaptured)
                PagesList.ReleaseMouseCapture();
            _vm.EndStroke(strokePage);
            e.Handled = true;
            return;
        }

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

    /// <summary>
    /// Завершение перетаскивания объекта. Правка применяется ОДНОЙ операцией
    /// на отпускание кнопки: применять её на каждое движение мыши значило бы
    /// перерисовывать страницу движком по десять раз в секунду и заваливать
    /// историю отмены.
    /// </summary>
    private void EndObjectDrag(MouseEventArgs e, bool commit)
    {
        var page = _objectPage;
        var element = _objectElement;
        _objectPage = null;
        _objectElement = null;
        if (PagesList.IsMouseCaptured)
            PagesList.ReleaseMouseCapture();
        if (_vm == null || page == null || element == null) return;

        if (!commit)
        {
            _vm.CancelObjectDrag();
            return;
        }

        var scale = page.DisplayScale;
        if (scale <= 0)
        {
            _vm.CancelObjectDrag();
            return;
        }
        var pos = e.GetPosition(element);
        _vm.CommitObjectDrag(pos.X / scale - _objectStartPt.X, pos.Y / scale - _objectStartPt.Y);
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
            _vm.UpdateCurrentPage(args.VerticalOffset, args.ViewportHeight);
            // Размер окна становится известен только здесь, поэтому первая
            // подгонка страницы по ширине делается в этот момент: открывать
            // документ в 100 %, когда лист шире окна, — плохая встреча.
            _vm.ApplyInitialFit(args.ViewportWidth);
            // И дальше на каждое изменение ширины: открылась панель справа —
            // документ обязан вписаться в остаток окна сам, а не уехать под неё.
            _vm.ApplyViewport(args.ViewportWidth, args.ViewportHeight);
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

    // ----- Контекстные меню -----

    /// <summary>
    /// Меню собирается из реестра команд, поэтому названия, значки и
    /// доступность совпадают с панелями. Окно ищется вверх по дереву: вкладку
    /// можно открепить в отдельное окно, и там своя модель.
    /// </summary>
    private Services.Ux.UxCommandHub? Hub =>
        (Window.GetWindow(this) as MainWindow)?.ViewModel.Ux;

    private void OnPagesRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ShowPageContextMenu(e.GetPosition(PagesList)))
            e.Handled = true;
    }

    /// <summary>
    /// Меню страницы в точке. Точка, а не источник события: то же меню
    /// открывается долгим удержанием пальца и кнопкой пера, где никакого
    /// «источника клика» нет.
    /// </summary>
    private bool ShowPageContextMenu(Point pointInList)
    {
        if (_vm == null || Hub is not { } hub) return false;
        var hit = FindPageAt(PagesList.InputHitTest(pointInList) as DependencyObject
                             ?? (object)PagesList);
        if (hit == null) return false;
        var (page, element) = hit.Value;
        var position = PagesList.TranslatePoint(pointInList, element);

        // Правая кнопка НЕ снимает выделение текста: иначе к моменту показа
        // меню размечать было бы уже нечего.
        var kind = NexusPdf.Ux.SelectionKind.Nothing;
        NexusPdf.Pdf.Abstractions.PdfPageLink? link = null;

        var scale = page.DisplayScale;
        if (scale > 0 && _vm.SelectObjectAt(page, position.X / scale, position.Y / scale))
        {
            // Щелчок по объекту сначала выбирает его: меню обязано относиться
            // к тому, по чему щёлкнули, а не к прошлому выделению.
            kind = _vm.SelectedObjectKind;
        }
        else if (_vm.LinkAt(page, position.X, position.Y) is { } hitLink)
        {
            kind = NexusPdf.Ux.SelectionKind.Link;
            link = hitLink;
        }
        else if (_vm.HasSelection && ReferenceEquals(_vm.SelectionPage, page) &&
                 // Рамка строки на мелком масштабе высотой всего несколько
                 // точек: требовать попадания в неё пиксель в пиксель — значит
                 // отдавать пользователю меню страницы вместо меню выделения.
                 page.SelectionRects.Any(r => Rect.Inflate(r, 6, 4).Contains(position)))
        {
            kind = NexusPdf.Ux.SelectionKind.Text;
        }

        var target = new Services.Ux.UxTarget
        {
            Context = hub.Snapshot(kind, new[] { page }),
            Document = _vm,
            Pages = new[] { page },
            Link = link,
        };
        return UxContextMenu.Show(hub, target, PagesList, pointInList);
    }

    private void OnThumbRightClick(object sender, MouseButtonEventArgs e) =>
        ShowPageMenu(ThumbList, e);

    private void OnOrganizeRightClick(object sender, MouseButtonEventArgs e) =>
        ShowPageMenu(OrganizeList, e);

    /// <summary>
    /// Меню страниц для списка миниатюр. Щелчок по странице вне выделения
    /// делает её единственной выбранной — иначе команда молча применилась бы
    /// не к той странице, по которой щёлкнули.
    /// </summary>
    private void ShowPageMenu(ListBox list, MouseButtonEventArgs e)
    {
        if (_vm == null || Hub is not { } hub) return;
        if (FindItemAt<PageViewModel>(e.OriginalSource) is not { } page) return;

        var selected = list.SelectedItems.OfType<PageViewModel>().ToList();
        if (!selected.Contains(page))
        {
            // У панели миниатюр выделение одиночное, и трогать там
            // SelectedItems запрещено самим WPF — отсюда и разные ветки.
            if (list.SelectionMode == SelectionMode.Single)
            {
                list.SelectedItem = page;
            }
            else
            {
                list.SelectedItems.Clear();
                list.SelectedItems.Add(page);
            }
            selected = new List<PageViewModel> { page };
        }

        var target = new Services.Ux.UxTarget
        {
            Context = hub.Snapshot(NexusPdf.Ux.SelectionKind.Page, selected),
            Document = _vm,
            Pages = selected,
        };
        if (UxContextMenu.Show(hub, target, list))
            e.Handled = true;
    }

    private void OnBookmarkRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || Hub is not { } hub) return;
        if (FindItemAt<BookmarkViewModel>(e.OriginalSource) is not { } bookmark) return;

        var target = new Services.Ux.UxTarget
        {
            Context = hub.Snapshot(NexusPdf.Ux.SelectionKind.Bookmark),
            Document = _vm,
            Bookmark = bookmark,
        };
        if (sender is FrameworkElement element && UxContextMenu.Show(hub, target, element))
            e.Handled = true;
    }

    // ----- Сенсорный ввод и перо -----

    private System.Windows.Threading.DispatcherTimer? _longPressTimer;
    private Point _touchStart;
    private bool _touchMoved;

    /// <summary>
    /// Щипок и протяжка. WPF умеет прокручивать список пальцем сам, но тогда
    /// он же и съедает манипуляцию, и щипок не доходит: поэтому прокрутка и
    /// масштаб считаются здесь вместе.
    /// </summary>
    private void OnPagesManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = PagesList;
        e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
        e.Handled = true;
    }

    private void OnPagesManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        if (_vm == null) return;
        HookScroller();
        if (_scroller == null) return;

        var scale = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) / 2;
        if (Math.Abs(scale - 1.0) > NexusPdf.Ux.TouchGestures.ZoomDeadZone)
        {
            // Щипок: масштаб идёт через ту же модель, что колесо и кнопки, —
            // иначе появились бы два разных «масштаба» с разными границами.
            _vm.SetZoom(NexusPdf.Ux.TouchGestures.ApplyZoom(_vm.Zoom, scale));
        }
        else
        {
            var translation = e.DeltaManipulation.Translation;
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - translation.Y);
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - translation.X);
        }
        e.Handled = true;
    }

    /// <summary>Инерция после броска: без неё прокрутка пальцем ощущается «залипшей».</summary>
    private void OnPagesInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
    {
        e.TranslationBehavior.DesiredDeceleration = 0.0016; // ≈ 1000 точек за 800 мс
        e.ExpansionBehavior.DesiredDeceleration = 0.0008;
        e.Handled = true;
    }

    private void OnPagesTouchDown(object sender, TouchEventArgs e)
    {
        _touchStart = e.GetTouchPoint(PagesList).Position;
        _touchMoved = false;
        StopLongPress();

        // Второй палец — это уже щипок, а не удержание.
        if (PagesList.TouchesOver.Count() > 1) return;

        _longPressTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(NexusPdf.Ux.TouchGestures.LongPressMs),
        };
        _longPressTimer.Tick += (_, _) =>
        {
            StopLongPress();
            if (!_touchMoved)
                ShowPageContextMenu(_touchStart);
        };
        _longPressTimer.Start();
    }

    private void OnPagesTouchMove(object sender, TouchEventArgs e)
    {
        var point = e.GetTouchPoint(PagesList).Position;
        var moved = Math.Abs(point.X - _touchStart.X) + Math.Abs(point.Y - _touchStart.Y);
        if (moved > NexusPdf.Ux.TouchGestures.MoveToleranceDip)
        {
            _touchMoved = true;
            StopLongPress();
        }
    }

    private void OnPagesTouchUp(object sender, TouchEventArgs e) => StopLongPress();

    private void StopLongPress()
    {
        _longPressTimer?.Stop();
        _longPressTimer = null;
    }

    /// <summary>
    /// Кнопка на пере — правая кнопка мыши. У пера её ищут именно там, где она
    /// есть физически, поэтому меню открывается в точке пера, а не у курсора.
    /// </summary>
    private void OnPagesStylusButtonDown(object sender, StylusButtonEventArgs e)
    {
        if (e.StylusButton.Guid != StylusPointProperties.BarrelButton.Id) return;
        if (ShowPageContextMenu(e.GetPosition(PagesList)))
            e.Handled = true;
    }

    /// <summary>
    /// Enter в поле свойств применяет значение. Без этого число применялось бы
    /// только при уходе фокуса, и было бы непонятно, приняли его или нет.
    /// </summary>
    private void OnObjectFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    // ----- Изменение ширины панелей -----

    private double _resizeStartX;
    private double _resizeStartWidth;

    private void OnPanelResizeStart(
        object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.Thumb { Tag: string name }) return;
        if ((Window.GetWindow(this) as MainWindow)?.ViewModel is not { } main) return;
        _resizeStartX = Mouse.GetPosition(this).X;
        _resizeStartWidth = main.PanelWidth(name);
    }

    /// <summary>
    /// Тянем край панели. Отсчёт идёт от положения мыши в самом окне, а не от
    /// сдвига ручки: ручка едет вместе с краем панели, и её собственный сдвиг
    /// на следующем шаге считается уже от нового места — панель от этого
    /// дёргается и ползёт не туда.
    /// </summary>
    private void OnPanelResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.Thumb { Tag: string name }) return;
        if ((Window.GetWindow(this) as MainWindow)?.ViewModel is not { } main) return;
        var offset = Mouse.GetPosition(this).X - _resizeStartX;
        main.SetPanelWidth(name, _resizeStartWidth, offset);
    }

    /// <summary>Ширина записывается по окончании перетаскивания, а не на каждый пиксель.</summary>
    private void OnPanelResizeDone(
        object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.ViewModel.SavePanelWidths();

    /// <summary>Esc в строке поиска очищает её, а не закрывает окно.</summary>
    private void OnToolsSearchKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        ClearToolsSearch();
        e.Handled = true;
    }

    private void OnToolsSearchClear(object sender, RoutedEventArgs e) => ClearToolsSearch();

    private void ClearToolsSearch()
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.ViewModel.Tools.Filter = "";
        ToolsSearch.Clear();
    }

    // ----- Перетаскивание файлов из Проводника в организатор -----

    private InsertionLineAdorner? _insertLine;

    /// <summary>
    /// Куда лягут страницы: индекс ближайшего промежутка между миниатюрами.
    /// Считается по СЕРЕДИНЕ страницы — правее середины значит «после неё».
    /// </summary>
    private int InsertIndexAt(Point position)
    {
        var index = OrganizeList.Items.Count;
        for (var i = 0; i < OrganizeList.Items.Count; i++)
        {
            if (OrganizeList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            if (!container.IsVisible) continue;
            var origin = container.TransformToAncestor(OrganizeList).Transform(new Point(0, 0));
            var bounds = new Rect(origin, new Size(container.ActualWidth, container.ActualHeight));
            if (position.Y < bounds.Bottom && position.X < bounds.X + bounds.Width / 2)
                return i;
            if (position.Y < bounds.Bottom)
                index = i + 1;
        }
        return index;
    }

    private void ShowInsertionLine(int index)
    {
        var layer = AdornerLayer.GetAdornerLayer(OrganizeList);
        if (layer == null) return;
        if (_insertLine == null)
        {
            _insertLine = new InsertionLineAdorner(OrganizeList);
            layer.Add(_insertLine);
        }

        var count = OrganizeList.Items.Count;
        var anchorIndex = Math.Clamp(index < count ? index : count - 1, 0, Math.Max(0, count - 1));
        if (OrganizeList.ItemContainerGenerator.ContainerFromIndex(anchorIndex) is not FrameworkElement container)
            return;
        var origin = container.TransformToAncestor(OrganizeList).Transform(new Point(0, 0));
        var bounds = new Rect(origin, new Size(container.ActualWidth, container.ActualHeight));
        // Перед страницей — по её левому краю, в самый конец — по правому.
        var x = index < count ? bounds.X : bounds.Right;
        _insertLine.Target = new Rect(x, bounds.Y, 1, bounds.Height);
    }

    private void HideInsertionLine()
    {
        if (_insertLine == null) return;
        AdornerLayer.GetAdornerLayer(OrganizeList)?.Remove(_insertLine);
        _insertLine = null;
    }

    private static bool HasDroppableFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0;

    private void OnOrganizeFileDragOver(object sender, DragEventArgs e)
    {
        if (!HasDroppableFiles(e.Data))
        {
            // Не файлы — это перетаскивание страниц внутри списка, им
            // занимается своя библиотека: мешать нельзя.
            HideInsertionLine();
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        ShowInsertionLine(InsertIndexAt(e.GetPosition(OrganizeList)));
    }

    private void OnOrganizeFileDragLeave(object sender, DragEventArgs e) => HideInsertionLine();

    private void OnOrganizeFileDrop(object sender, DragEventArgs e)
    {
        HideInsertionLine();
        if (!HasDroppableFiles(e.Data)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        e.Handled = true;
        var index = InsertIndexAt(e.GetPosition(OrganizeList));
        if (Window.GetWindow(this) is MainWindow main && _vm != null)
            _ = main.ViewModel.InsertDroppedFilesAsync(_vm, files, index);
    }

    // ----- Выделение рамкой в режиме систематизации -----

    private MarqueeAdorner? _marquee;
    private Point _marqueeStart;
    private List<PageViewModel>? _marqueeBase;

    /// <summary>
    /// Рамка начинается ТОЛЬКО с пустого места: нажатие на саму страницу — это
    /// перетаскивание, и подменять его выделением нельзя.
    /// </summary>
    private void OnOrganizeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindItemAt<PageViewModel>(e.OriginalSource) != null) return;
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) != null)
            return;

        _marqueeStart = e.GetPosition(OrganizeList);
        // Ctrl и Shift означают «добавить к выбранному», поэтому прежний выбор
        // запоминается; без них рамка начинает с чистого листа.
        var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _marqueeBase = additive
            ? OrganizeList.SelectedItems.OfType<PageViewModel>().ToList()
            : new List<PageViewModel>();
        if (!additive)
            OrganizeList.SelectedItems.Clear();

        var layer = AdornerLayer.GetAdornerLayer(OrganizeList);
        if (layer == null) return;
        _marquee = new MarqueeAdorner(OrganizeList);
        layer.Add(_marquee);
        OrganizeList.CaptureMouse();
        e.Handled = true;
    }

    private void OnOrganizeMouseMove(object sender, MouseEventArgs e)
    {
        if (_marquee == null) return;
        if (e.LeftButton != MouseButtonState.Pressed || !OrganizeList.IsMouseCaptured)
        {
            EndMarquee();
            return;
        }

        var current = e.GetPosition(OrganizeList);
        var rect = new Rect(_marqueeStart, current);
        _marquee.Rect = rect;
        ApplyMarqueeSelection(rect);
    }

    private void OnOrganizeMouseUp(object sender, MouseButtonEventArgs e) => EndMarquee();

    /// <summary>Страницы, задетые рамкой, попадают в выбор; остальные — нет.</summary>
    private void ApplyMarqueeSelection(Rect rect)
    {
        if (_marqueeBase == null) return;
        var chosen = new HashSet<PageViewModel>(_marqueeBase);
        foreach (var item in OrganizeList.Items.OfType<PageViewModel>())
        {
            if (OrganizeList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                continue;
            if (!container.IsVisible) continue;
            var origin = container.TransformToAncestor(OrganizeList).Transform(new Point(0, 0));
            var bounds = new Rect(origin, new Size(container.ActualWidth, container.ActualHeight));
            if (rect.IntersectsWith(bounds))
                chosen.Add(item);
        }

        // Список правится по разнице, а не пересобирается: замена целиком
        // сбрасывала бы прокрутку и мигала выделением на каждый пиксель.
        foreach (var item in OrganizeList.SelectedItems.OfType<PageViewModel>().ToList())
            if (!chosen.Contains(item))
                OrganizeList.SelectedItems.Remove(item);
        foreach (var item in chosen)
            if (!OrganizeList.SelectedItems.Contains(item))
                OrganizeList.SelectedItems.Add(item);
    }

    private void EndMarquee()
    {
        if (_marquee != null)
        {
            AdornerLayer.GetAdornerLayer(OrganizeList)?.Remove(_marquee);
            _marquee = null;
        }
        _marqueeBase = null;
        if (OrganizeList.IsMouseCaptured)
            OrganizeList.ReleaseMouseCapture();
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (DependencyObject? current = node; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    /// <summary>Элемент списка под курсором по его DataContext.</summary>
    private static T? FindItemAt<T>(object originalSource) where T : class
    {
        if (originalSource is not DependencyObject source) return null;
        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: T item })
                return item;
        }
        return null;
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

        // Выбранный объект: Delete удаляет, Esc снимает выделение, стрелки
        // двигают точнее мыши (с Ctrl — по одному пункту).
        if (_vm.HasObjectSelection)
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 1.0 : 5.0;
            switch (e.Key)
            {
                case Key.Delete or Key.Back:
                    _vm.DeleteSelectedObject();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    _vm.ClearObjectSelection();
                    e.Handled = true;
                    return;
                case Key.Left:
                    e.Handled = _vm.NudgeSelectedObject(-step, 0);
                    return;
                case Key.Right:
                    e.Handled = _vm.NudgeSelectedObject(step, 0);
                    return;
                case Key.Up:
                    e.Handled = _vm.NudgeSelectedObject(0, -step);
                    return;
                case Key.Down:
                    e.Handled = _vm.NudgeSelectedObject(0, step);
                    return;
            }
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
