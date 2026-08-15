using System.IO;
using System.Windows;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Список вложенных в документ файлов с возможностью сохранить их на диск.
/// Открывать вложение программа не умеет намеренно: вложение в PDF —
/// обычный способ доставки вредоносного файла, и решение о запуске должно
/// оставаться за пользователем и его антивирусом.
/// </summary>
public partial class AttachmentsDialog : Window
{
    /// <summary>Строка списка: имя и человекочитаемый размер.</summary>
    public sealed record Row(int Index, string Name, string SizeText);

    private IReadOnlyList<PdfAttachment> _attachments = Array.Empty<PdfAttachment>();
    private Func<int, Task<byte[]>>? _read;

    private AttachmentsDialog() => InitializeComponent();

    public static void Show(
        Window? owner, IReadOnlyList<PdfAttachment> attachments, Func<int, Task<byte[]>> read)
    {
        var dialog = new AttachmentsDialog { _attachments = attachments, _read = read };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.List.ItemsSource = attachments
            .Select(a => new Row(a.Index, a.Name, FormatSize(a.SizeBytes)))
            .ToList();
        dialog.ShowDialog();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} " + Loc.Get("UnitMb"),
        >= 1024 => $"{bytes / 1024.0:0.#} " + Loc.Get("UnitKb"),
        _ => $"{bytes} " + Loc.Get("UnitB"),
    };

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        SaveButton.IsEnabled = List.SelectedItem is Row;

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Row row || _read == null) return;

        // Путь выбирает пользователь: программа никуда не пишет сама.
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("AttachmentsSave"),
            FileName = SanitizeFileName(row.Name),
            Filter = Loc.Get("AllFilter"),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SaveButton.IsEnabled = false;
            var bytes = await _read(row.Index);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            Serilog.Log.Information("Вложение {Name} сохранено, байт: {Size}", row.Name, bytes.Length);
            ErrorDialog.Show(this, Loc.Get("AttachmentsTitle"),
                Loc.F("AttachmentsSaved", Path.GetFileName(dialog.FileName)), "");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось сохранить вложение {Name}", row.Name);
            ErrorDialog.Show(this, Loc.Get("AttachmentsTitle"), ex.Message, ex.ToString());
        }
        finally
        {
            SaveButton.IsEnabled = List.SelectedItem is Row;
        }
    }

    /// <summary>
    /// Имя из документа — недоверенные данные: из него убираются разделители
    /// пути и запрещённые символы, чтобы вложение не «сбежало» из выбранной
    /// пользователем папки.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray())
            .Trim();
        return cleaned.Length > 0 ? cleaned : "attachment.bin";
    }
}
