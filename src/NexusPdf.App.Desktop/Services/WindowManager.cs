using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.App.Desktop.Views;

namespace NexusPdf.App.Desktop.Services;

/// <summary>Учёт окон приложения: создание, перенос вкладок, сбор открытых файлов.</summary>
public static class WindowManager
{
    private static readonly List<MainWindow> Windows = new();

    /// <param name="pendingFiles">
    /// Сколько файлов будет открыто сразу после показа окна (аргументы
    /// командной строки, двойной щелчок в проводнике). Окно узнаёт об этом ДО
    /// первой отрисовки: иначе оно успевает показать стартовый экран
    /// «откройте файл» ровно тогда, когда файл уже открывается.
    /// </param>
    public static MainWindow OpenWindow(
        AppServices services, DocumentViewModel? initialDocument, int pendingFiles = 0)
    {
        var vm = new MainViewModel(services) { PendingOpens = pendingFiles };
        var window = new MainWindow(vm);
        Windows.Add(window);
        window.Closed += (_, _) => Windows.Remove(window);
        window.Show();

        if (initialDocument != null)
        {
            vm.Documents.Add(initialDocument);
            vm.ActiveDocument = initialDocument;
        }
        return window;
    }

    /// <summary>
    /// Показать окно, которому передали файл: развернуть из свёрнутого — в
    /// прежнее состояние (развёрнутое на весь экран остаётся таким, а не
    /// становится обычным, как при WindowState = Normal) — и вывести вперёд.
    /// </summary>
    public static void BringToFront(System.Windows.Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (window.WindowState == System.Windows.WindowState.Minimized)
        {
            if (handle != IntPtr.Zero)
                ShowWindow(handle, SwRestore);
            else
                window.WindowState = System.Windows.WindowState.Normal;
        }
        window.Activate();
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
    }

    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public static MainWindow? ActiveOrFirst() =>
        Windows.FirstOrDefault(w => w.IsActive) ?? Windows.FirstOrDefault();

    public static IEnumerable<MainViewModel> AllViewModels() =>
        Windows.Select(w => w.ViewModel);

    public static IReadOnlyList<string> CollectOpenFiles() =>
        Windows.SelectMany(w => w.ViewModel.Documents)
            .Select(d => d.FilePath)
            .Where(p => p != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
