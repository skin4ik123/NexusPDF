using System.Globalization;
using System.IO;
using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using Serilog;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Свойства документа: файл, страницы, метаданные /Info, заявленный PDF/A, защита, подписи.</summary>
public partial class DocPropertiesDialog : Window
{
    private sealed record Row(string Name, string Value);

    private DocPropertiesDialog() => InitializeComponent();

    public static async Task ShowAsync(Window? owner, DocumentViewModel document)
    {
        var dialog = new DocPropertiesDialog();
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;

        var rows = new List<Row>();
        var path = document.FilePath;
        dialog.FileNameLabel.Text = document.Title;
        try
        {
            if (path != null)
            {
                var info = new FileInfo(path);
                rows.Add(new Row(Loc.Get("PropsPath"), path));
                rows.Add(new Row(Loc.Get("PropsFileSize"),
                    $"{info.Length / 1024.0 / 1024.0:0.##} МБ ({info.Length:N0} Б)"));
            }
            rows.Add(new Row(Loc.Get("PropsPages"), document.PageCount.ToString(CultureInfo.InvariantCulture)));

            var meta = await document.Document.PrimaryHandle.GetMetadataAsync(CancellationToken.None);
            if (meta.PdfVersion.Length > 0)
                rows.Add(new Row(Loc.Get("PropsPdfVersion"), meta.PdfVersion));

            if (path != null)
            {
                var claim = PdfAClaimDetector.DetectClaim(path);
                rows.Add(new Row(Loc.Get("PropsPdfA"),
                    claim != null ? Loc.F("PropsPdfAClaimed", claim) : Loc.Get("PropsPdfANo")));
            }

            rows.Add(new Row(Loc.Get("PropsEncrypted"),
                document.Document.Password != null ? Loc.Get("PropsYes") : Loc.Get("PropsNo")));
            rows.Add(new Row(Loc.Get("PropsForms"),
                document.HasAcroForm ? Loc.Get("PropsYes") : Loc.Get("PropsNo")));
            rows.Add(new Row(Loc.Get("PropsSignatures"),
                document.HasSignatures
                    ? document.Signatures.Count.ToString(CultureInfo.InvariantCulture)
                    : Loc.Get("PropsNo")));

            AddIfNotEmpty(rows, "PropsDocTitle", meta.Title);
            AddIfNotEmpty(rows, "PropsAuthor", meta.Author);
            AddIfNotEmpty(rows, "PropsSubject", meta.Subject);
            AddIfNotEmpty(rows, "PropsCreator", meta.Creator);
            AddIfNotEmpty(rows, "PropsProducer", meta.Producer);
            AddIfNotEmpty(rows, "PropsCreated", FormatPdfDate(meta.CreationDate));
            AddIfNotEmpty(rows, "PropsModified", FormatPdfDate(meta.ModDate));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось собрать свойства документа");
            rows.Add(new Row(Loc.Get("ErrorTitle"), ex.Message));
        }

        dialog.Rows.ItemsSource = rows;
        dialog.ShowDialog();
    }

    private static void AddIfNotEmpty(List<Row> rows, string key, string value)
    {
        if (value.Length > 0)
            rows.Add(new Row(Loc.Get(key), value));
    }

    /// <summary>D:YYYYMMDDHHmmSS… → локальное представление; непонятный формат отдаётся как есть.</summary>
    private static string FormatPdfDate(string pdfDate)
    {
        if (pdfDate.Length < 10 || !pdfDate.StartsWith("D:", StringComparison.Ordinal))
            return pdfDate;
        var s = pdfDate[2..];
        if (!int.TryParse(s[..4], out var year) || !int.TryParse(s[4..6], out var month) ||
            !int.TryParse(s[6..8], out var day))
            return pdfDate;
        var hour = s.Length >= 10 && int.TryParse(s[8..10], out var h) ? h : 0;
        var minute = s.Length >= 12 && int.TryParse(s[10..12], out var m) ? m : 0;
        try
        {
            return new DateTime(year, month, day, hour, minute, 0).ToString("g", CultureInfo.CurrentCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return pdfDate;
        }
    }
}
