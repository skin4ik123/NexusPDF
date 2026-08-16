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
