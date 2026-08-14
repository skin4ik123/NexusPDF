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
        if (e.Key == Key.Escape && WindowStyle == WindowStyle.None)
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

    private void OnMenuItem(object sender, RoutedEventArgs e) => MenuToggle.IsChecked = false;

    private void OnMenuClosed(object sender, EventArgs e) => MenuToggle.IsChecked = false;
}
