using System.Windows;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views;

public enum UnsavedChangesResult
{
    Save,
    DontSave,
    Cancel,
}

public partial class UnsavedChangesDialog : Window
{
    private sealed class Row
    {
        public required DocumentViewModel Document { get; init; }
        public string Title => Document.Title;
        public bool IsChecked { get; set; } = true;
    }

    private UnsavedChangesResult _result = UnsavedChangesResult.Cancel;
    private readonly List<Row> _rows;

    private UnsavedChangesDialog(IReadOnlyList<DocumentViewModel> documents)
    {
        InitializeComponent();
        _rows = documents.Select(d => new Row { Document = d }).ToList();
        DocList.ItemsSource = _rows;
    }

    public static UnsavedChangesResult ShowForSingle(Window? owner, DocumentViewModel document)
    {
        var (result, _) = ShowForMany(owner, new[] { document });
        return result;
    }

    public static (UnsavedChangesResult Result, IReadOnlyList<DocumentViewModel> ToSave) ShowForMany(
        Window? owner, IReadOnlyList<DocumentViewModel> documents)
    {
        var dialog = new UnsavedChangesDialog(documents) { Owner = owner };
        dialog.ShowDialog();
        var toSave = dialog._rows.Where(r => r.IsChecked).Select(r => r.Document).ToList();
        return (dialog._result, toSave);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _result = UnsavedChangesResult.Save;
        DialogResult = true;
    }

    private void OnDontSave(object sender, RoutedEventArgs e)
    {
        _result = UnsavedChangesResult.DontSave;
        DialogResult = true;
    }
}
