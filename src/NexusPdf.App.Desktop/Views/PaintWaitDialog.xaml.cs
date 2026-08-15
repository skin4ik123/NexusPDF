using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Ожидание правки во внешнем редакторе. Программа НЕ блокируется ожиданием
/// закрытия редактора: сохранение файла отслеживается, импорт запускает
/// пользователь (или предлагается сразу после сохранения).
/// </summary>
public partial class PaintWaitDialog : Window
{
    private readonly ExternalImageEditor _editor;
    private readonly string _imagePath;
    private readonly DispatcherTimer _poll;
    private bool _hasChanges;

    private PaintWaitDialog(ExternalImageEditor editor, string imagePath, BitmapSource before)
    {
        InitializeComponent();
        _editor = editor;
        _imagePath = imagePath;
        BeforeImage.Source = before;
        StatusText.Text = Loc.Get("PaintWaitWaiting");

        // Событие файловой системы приходит и на промежуточные записи, поэтому
        // фактическую готовность проверяем опросом с проверкой блокировки файла.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _poll.Tick += (_, _) => CheckForChanges();
        _poll.Start();

        _editor.FileChanged += (_, _) => Dispatcher.BeginInvoke(CheckForChanges);
        _editor.EditorExited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            CheckForChanges();
            if (!_hasChanges)
                StatusText.Text = Loc.Get("PaintWaitEditorClosedNoChanges");
        });
    }

    /// <summary>Возвращает путь к отредактированному файлу или null (отмена).</summary>
    public static string? Run(Window? owner, ExternalImageEditor editor, string imagePath, BitmapSource before)
    {
        var dialog = new PaintWaitDialog(editor, imagePath, before);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? imagePath : null;
    }

    private void CheckForChanges()
    {
        if (!_editor.TryDetectCompletedSave())
            return;
        try
        {
            // Файл читается в память целиком: дальше редактор может его
            // перезаписать, а нам нужен снимок именно сохранённого состояния.
            var bytes = File.ReadAllBytes(_imagePath);
            using var stream = new MemoryStream(bytes);
            var preview = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            preview.Freeze();
            AfterImage.Source = preview;
            AfterPlaceholder.Visibility = Visibility.Collapsed;
            _hasChanges = true;
            ImportButton.IsEnabled = true;
            StatusGlyph.Text = "";
            StatusText.Text = Loc.Get("PaintWaitSaved");
        }
        catch (Exception ex)
        {
            // Частично записанный файл — просто ждём следующего опроса.
            Serilog.Log.Debug(ex, "Файл правки ещё не готов к чтению");
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        DialogResult = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _poll.Stop();
    }
}
