using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.App.Desktop.Views;

namespace NexusPdf.App.Desktop.Services;

/// <summary>Учёт окон приложения: создание, перенос вкладок, сбор открытых файлов.</summary>
public static class WindowManager
{
    private static readonly List<MainWindow> Windows = new();

    public static MainWindow OpenWindow(AppServices services, DocumentViewModel? initialDocument)
    {
        var vm = new MainViewModel(services);
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
