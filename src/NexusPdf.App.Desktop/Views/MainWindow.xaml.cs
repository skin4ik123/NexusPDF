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

    private void OnMenuItem(object sender, RoutedEventArgs e) => MenuToggle.IsChecked = false;

    private void OnMenuClosed(object sender, EventArgs e) => MenuToggle.IsChecked = false;

    /// <summary>
    /// Меню открывается под панелью инструментов, поэтому его высота
    /// ограничивается остатком окна. Иначе список пунктов уезжает за нижний
    /// край экрана и до дальних пунктов не добраться вообще.
    /// </summary>
    private void OnMenuOpened(object sender, EventArgs e)
    {
        var available = ActualHeight - MenuToggle.TranslatePoint(new Point(0, MenuToggle.ActualHeight), this).Y;
        MenuScroll.MaxHeight = Math.Max(240, available - 24);
    }
}
