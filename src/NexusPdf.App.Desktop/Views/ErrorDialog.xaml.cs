using System.Windows;

namespace NexusPdf.App.Desktop.Views;

public partial class ErrorDialog : Window
{
    private ErrorDialog(string title, string message, string details)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        DetailsText.Text = details;
    }

    public static void Show(Window? owner, string title, string message, string details)
    {
        var dialog = new ErrorDialog(title, message, details);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MessageText.Text + Environment.NewLine + Environment.NewLine + DetailsText.Text);
        }
        catch
        {
            // Буфер обмена бывает занят другим процессом — не критично.
        }
    }
}
