using System.IO;
using System.Globalization;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _forceClose;
    private WindowState _preFullScreenState;

    public MainViewModel ViewModel { get; }

    public static readonly DependencyProperty ShowResponsiveQuickPanelLabelsProperty =
        DependencyProperty.Register(nameof(ShowResponsiveQuickPanelLabels), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(true));

    public bool ShowResponsiveQuickPanelLabels
    {
        get => (bool)GetValue(ShowResponsiveQuickPanelLabelsProperty);
        private set => SetValue(ShowResponsiveQuickPanelLabelsProperty, value);
    }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.OwnerWindow = this;
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        FitToWorkArea();
        // Реестр команд проверяет себя в конструкторе: команда без обработчика
        // обязана обнаружиться на запуске, а не пустым пунктом меню у пользователя.
        _ = ViewModel.Ux;
        ViewModel.QuickPanel.Items.CollectionChanged += OnQuickPanelItemsChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        NexusPdf.App.Desktop.Services.Ux.DensityManager.Changed += OnDensityChanged;
        Closed += (_, _) =>
        {
            ViewModel.QuickPanel.Items.CollectionChanged -= OnQuickPanelItemsChanged;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            NexusPdf.App.Desktop.Services.Ux.DensityManager.Changed -= OnDensityChanged;
        };
        UpdateQuickPanelLabels();
    }

    /// <summary>
    /// Ширина быстрой панели с подписями, какой её увидела раскладка (NaN — ещё
    /// не видели). Прикидка по тексту не знает округления букв до пикселей
    /// (на экранах до 125 % текст рисуется «чётко») и округления раскладки — и
    /// занижала ширину: подписи оставались, а кнопки уходили за край окна.
    /// </summary>
    private double _labelledWidth = double.NaN;

    private void OnQuickPanelItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RemeasureQuickPanel();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowQuickPanelLabels)) RemeasureQuickPanel();
    }

    private void OnDensityChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(RemeasureQuickPanel));

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RemeasureQuickPanel();
    }

    private void OnQuickPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdateQuickPanelLabels();

    /// <summary>Состав, шрифт или плотность поменялись — прежняя ширина с подписями уже не та.</summary>
    private void RemeasureQuickPanel()
    {
        _labelledWidth = double.NaN;
        UpdateQuickPanelLabels();
    }

    private void UpdateQuickPanelLabels()
    {
        if (QuickPanelScroll == null || QuickPanelScroll.ActualWidth <= 0) return;
        if (!ViewModel.ShowQuickPanelLabels)
        {
            ShowResponsiveQuickPanelLabels = false;
            return;
        }
        var needed = double.IsNaN(_labelledWidth) ? EstimateLabelledWidth() : _labelledWidth;
        ShowResponsiveQuickPanelLabels = needed <= QuickPanelScroll.ActualWidth;
    }

    /// <summary>
    /// Подписи показаны — раскладка знает настоящую ширину панели. Не влезла —
    /// подписи убираются (остаются значки), а ширина запоминается: панель
    /// снова получит подписи, только когда для них действительно хватит места.
    /// </summary>
    private void OnQuickPanelScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (!ShowResponsiveQuickPanelLabels || (e.ExtentWidthChange == 0 && e.ViewportWidthChange == 0))
            return;
        _labelledWidth = QuickPanelScroll.ExtentWidth;
        if (QuickPanelScroll.ExtentWidth > QuickPanelScroll.ViewportWidth + 0.5)
            ShowResponsiveQuickPanelLabels = false;
    }

    /// <summary>Ширина панели с подписями до первой раскладки — тем же режимом текста, что на экране.</summary>
    private double EstimateLabelledWidth()
    {
        var padding = (Thickness)FindResource("UxToolPadding");
        var margin = (Thickness)FindResource("UxToolMargin");
        var glyphWidth = (double)FindResource("UxGlyphSize");
        var targetWidth = (double)FindResource("UxTouchTarget");
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mode = TextOptions.GetTextFormattingMode(this);
        return ViewModel.QuickPanel.Items.Sum(item => item.IsSeparator ? 13 :
            Math.Ceiling(Math.Max(targetWidth, padding.Left + padding.Right + margin.Left + margin.Right + 4 + glyphWidth + 6 +
                new FormattedText(item.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, FontSize, Brushes.Black, null, mode, dpi).WidthIncludingTrailingWhitespace)) + 1);
    }

    private void OnQuickPanelMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (QuickPanelScroll.ScrollableWidth <= 0) return;
        QuickPanelScroll.ScrollToHorizontalOffset(QuickPanelScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// Стартовый размер из разметки (1200×800) больше рабочей области на
    /// ноутбучных экранах вроде 1440×900 при системном масштабе 125–150 %:
    /// окно уезжало за край, и часть интерфейса была недоступна. Здесь оно
    /// вписывается в рабочую область, а на тесных экранах разворачивается.
    /// </summary>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
            return;

        var maximize = work.Width < Width + 40 || work.Height < Height + 40;

        // Размер «после Восстановить» и минимум тоже вписываются в экран: иначе
        // окно, развёрнутое на маленьком экране, восстанавливалось бы шире и
        // выше его и уходило кнопками за края.
        var width = Math.Max(NexusPdf.Ux.ScreenFit.FloorWidth, work.Width - 40);
        var height = Math.Max(NexusPdf.Ux.ScreenFit.FloorHeight, work.Height - 40);
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = Math.Min(Width, width);
        Height = Math.Min(Height, height);

        if (maximize)
            WindowState = WindowState.Maximized;
    }

    public static readonly RoutedCommand ToggleFullScreenCommand = new();

    static MainWindow()
    {
        CommandManager.RegisterClassCommandBinding(typeof(MainWindow),
            new CommandBinding(ToggleFullScreenCommand, (sender, _) => ((MainWindow)sender).ToggleFullScreen()));
    }

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullScreenState;
        }
        else
        {
            _preFullScreenState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Палитра команд: единственный способ найти команду, не зная, в какой
        // она вкладке. Ctrl+K перехватывается до полей ввода намеренно.
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.Ux.Invoke(NexusPdf.Ux.CommandIds.CommandPalette,
                new Services.Ux.UxTarget
                {
                    Context = ViewModel.Ux.Snapshot(),
                    Document = ViewModel.ActiveDocument,
                });
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        if (ViewModel.ActiveDocument is { PendingOverlay: not null } doc)
        {
            doc.CancelPlacement();
            e.Handled = true;
            return;
        }
        // Линейка: Esc сначала бросает начатое измерение, потом убирает инструмент —
        // откуда бы ни пришла клавиша, хоть из поля масштаба на панели.
        if (ViewModel.ActiveDocument is { IsMeasuring: true } measuring)
        {
            if (measuring.HasMeasureInProgress)
                measuring.CancelMeasurement();
            else
                measuring.StopMeasurement();
            e.Handled = true;
            return;
        }
        if (WindowStyle == WindowStyle.None)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose) return;

        e.Cancel = true;
        // Снимок открытых файлов ДО закрытия вкладок — иначе «восстановить
        // прошлую сессию» после чистого выхода всегда видела бы пустой список.
        ViewModel.SnapshotBeforeExit();
        if (await ViewModel.TryCloseAllAsync())
        {
            _forceClose = true;
            await Dispatcher.InvokeAsync(Close);
        }
    }

    /// <summary>
    /// Свои же страницы, которые тащат из организатора. Они несут и файл для
    /// Проводника, но внутри программы это не «брошенный файл»: принимать его
    /// как чужой PDF значило бы открыть копию собственных страниц.
    /// </summary>
    private static bool IsOwnPageDrag(IDataObject data) =>
        data.GetDataPresent(GongSolutions.Wpf.DragDrop.DragDrop.DataFormat.Name);

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !IsOwnPageDrag(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (IsOwnPageDrag(e.Data)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        e.Handled = true;
        await ViewModel.DropFilesOnWindowAsync(files);
    }

    /// <summary>Enter в поле масштаба применяет его сразу, не дожидаясь ухода фокуса.</summary>
    private void OnMeasureScaleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not System.Windows.Controls.TextBox box) return;
        box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel.ActiveDocument is not { } doc) return;
        if (int.TryParse(PageBox.Text.Trim(), out var page))
            doc.GoToPage(page);
        e.Handled = true;
    }

    /// <summary>
    /// Меню вкладки. Щелчок правой кнопкой сначала делает вкладку активной:
    /// иначе «Сохранить» из меню одной вкладки сохранило бы другую.
    /// </summary>
    /// <summary>
    /// Что тащат: страницы другого документа. Формат — тот же, которым
    /// пользуется перетаскивание внутри списка, поэтому отдельного режима
    /// «перенос между документами» пользователю включать не надо.
    /// </summary>
    private static (DocumentViewModel Source, List<int> Indices)? DraggedPages(IDataObject data)
    {
        var payload = data.GetData(GongSolutions.Wpf.DragDrop.DragDrop.DataFormat.Name);
        var pages = payload switch
        {
            PageViewModel single => new List<PageViewModel> { single },
            System.Collections.IEnumerable many => many.OfType<PageViewModel>().ToList(),
            _ => new List<PageViewModel>(),
        };
        if (pages.Count == 0) return null;
        var owner = pages[0].Owner;
        return (owner, pages.Where(p => ReferenceEquals(p.Owner, owner))
            .Select(p => p.LogicalIndex).ToList());
    }

    /// <summary>Вкладка, над которой сейчас держат страницы, и отсчёт до её открытия.</summary>
    private DocumentViewModel? _springTarget;
    private System.Windows.Threading.DispatcherTimer? _springTimer;

    /// <summary>
    /// Задержка перед тем, как вкладка под курсором откроется.
    ///
    /// Мгновенное переключение мешало бы просто проносить страницы мимо чужих
    /// вкладок, а слишком долгое заставляло бы ждать.
    /// </summary>
    private static readonly TimeSpan SpringDelay = TimeSpan.FromMilliseconds(600);

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (sender is not FrameworkElement { DataContext: DocumentViewModel target }) return;
        if (DraggedPages(e.Data) is not { } dragged) return;
        // На свою же вкладку бросать нечего — этим занимается сам список.
        if (ReferenceEquals(dragged.Source, target))
        {
            CancelSpring();
            return;
        }

        e.Effects = DragDropEffects.Copy;
        StartSpring(target, dragged.Source);
    }

    /// <summary>
    /// Задержаться над чужой вкладкой — значит открыть её, не отпуская
    /// страницы. Дальше человек ставит их в нужное место обычным
    /// перетаскиванием внутри организатора, а не «куда положат».
    ///
    /// Организатор у цели включается сам: ставить страницы в точное место,
    /// глядя на сплошной текст, невозможно.
    /// </summary>
    private void StartSpring(DocumentViewModel target, DocumentViewModel source)
    {
        if (ReferenceEquals(_springTarget, target)) return;
        CancelSpring();
        _springTarget = target;
        _springTimer = new System.Windows.Threading.DispatcherTimer { Interval = SpringDelay };
        _springTimer.Tick += (_, _) =>
        {
            CancelSpring();
            ViewModel.ActiveDocument = target;
            if (source.IsOrganizeMode) target.IsOrganizeMode = true;
        };
        _springTimer.Start();
    }

    private void CancelSpring()
    {
        _springTimer?.Stop();
        _springTimer = null;
        _springTarget = null;
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        CancelSpring();
        if (sender is not FrameworkElement { DataContext: DocumentViewModel target }) return;
        if (DraggedPages(e.Data) is not { } dragged) return;
        if (ReferenceEquals(dragged.Source, target)) return;
        // Брошено на саму вкладку — место не указано, значит в конец. Чтобы
        // поставить в нужное место, над вкладкой надо задержаться: она
        // откроется, и страницы ставятся уже среди её карточек.
        _ = ViewModel.DropPagesOnDocumentAsync(target, dragged.Source, dragged.Indices);
    }

    private void OnTabRightClick(object sender, MouseButtonEventArgs e)
    {
        DocumentViewModel? document = null;
        for (DependencyObject? node = e.OriginalSource as DependencyObject;
             node != null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.TabItem { DataContext: DocumentViewModel doc })
            {
                document = doc;
                break;
            }
            if (node is System.Windows.Controls.TabControl)
                break;
        }
        if (document == null) return;

        ViewModel.ActiveDocument = document;
        var hub = ViewModel.Ux;
        var target = new Services.Ux.UxTarget
        {
            Context = hub.Snapshot(NexusPdf.Ux.SelectionKind.Tab),
            Document = document,
        };
        if (UxContextMenu.Show(hub, target, DocTabs))
            e.Handled = true;
    }

    /// <summary>
    /// Меню программы. Собирается заново на каждое открытие: доступность
    /// команд и отметки панелей зависят от того, что происходит сейчас, а не
    /// от того, что было при запуске.
    /// </summary>
    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = AppMenuFactory.Build(ViewModel);
        menu.PlacementTarget = MenuToggle;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
