using System.IO;
using System.Windows;
using System.Windows.Input;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _forceClose;
    private WindowState _preFullScreenState;

    public MainViewModel ViewModel { get; }

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

        if (work.Width < Width + 40 || work.Height < Height + 40)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        Width = Math.Min(Width, work.Width - 40);
        Height = Math.Min(Height, work.Height - 40);
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

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var pdfs = files.Where(f => File.Exists(f) &&
            string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pdfs.Count > 0)
            await ViewModel.OpenFilesAsync(pdfs);
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
