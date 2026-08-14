using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Infrastructure;

namespace NexusPdf.App.Desktop.Views;

public sealed record SignaturePick(LoadedImage Image, double WidthPercent);

public partial class SignatureLibraryDialog : Window
{
    private sealed class Row
    {
        public required SignatureTemplate Template { get; init; }
        public BitmapSource? Preview { get; init; }
        public string Name => Template.Name;
    }

    private readonly SignatureStore _store;
    private SignaturePick? _result;

    private SignatureLibraryDialog(SignatureStore store)
    {
        _store = store;
        InitializeComponent();
        Reload();
    }

    public static SignaturePick? Show(Window? owner, SignatureStore store)
    {
        var dialog = new SignatureLibraryDialog(store) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Reload()
    {
        var rows = new List<Row>();
        foreach (var template in _store.List())
        {
            // Для списка декодируется только эскиз (DecodePixelHeight): полные
            // растры многомегапиксельных подписей загружаются лениво при вставке.
            BitmapSource? preview = null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelHeight = 128;
                image.StreamSource = new MemoryStream(_store.Load(template));
                image.EndInit();
                image.Freeze();
                preview = image;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Шаблон подписи не читается: {Name}", template.Name);
            }
            rows.Add(new Row { Template = template, Preview = preview });
        }
        SigList.ItemsSource = rows;
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        var hasSelection = SigList.SelectedItem != null;
        InsertButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Loc.Get("ImageFilter") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Проверяем, что файл декодируется, и сохраняем исходные байты (DPAPI).
            _ = ImageLoader.FromFile(dialog.FileName);
            _store.Save(Path.GetFileNameWithoutExtension(dialog.FileName), File.ReadAllBytes(dialog.FileName));
            Reload();
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.F("ErrorOpenFile", Path.GetFileName(dialog.FileName)), ex.ToString());
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SigList.SelectedItem is not Row row) return;
        _store.Delete(row.Template);
        Reload();
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        if (SigList.SelectedItem is not Row row) return;
        try
        {
            var image = ImageLoader.FromBytes(_store.Load(row.Template));
            _result = new SignaturePick(image, WidthSlider.Value);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, Loc.Get("ErrorTitle"), Loc.F("ErrorOpenFile", row.Name), ex.ToString());
        }
    }
}
