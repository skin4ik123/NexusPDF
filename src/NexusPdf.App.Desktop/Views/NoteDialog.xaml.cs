using System.Windows;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.Views;

public sealed record NoteResult(string Contents, string Author);

public partial class NoteDialog : Window
{
    private NoteResult? _result;

    private NoteDialog()
    {
        InitializeComponent();
        AuthorBox.Text = Environment.UserName;
        Loaded += (_, _) => ContentsBox.Focus();
    }

    public static NoteResult? Show(Window? owner)
    {
        var dialog = new NoteDialog { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        var contents = ContentsBox.Text.Trim();
        if (contents.Length == 0)
        {
            ErrorLabel.Text = Loc.Get("AtTextEmpty");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }
        _result = new NoteResult(contents, AuthorBox.Text.Trim());
        DialogResult = true;
    }
}
