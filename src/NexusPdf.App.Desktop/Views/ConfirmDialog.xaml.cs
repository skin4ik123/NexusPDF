using System.Windows;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Подтверждение потенциально небезопасного или необратимого действия.
/// Показывает подробность (например, полный адрес ссылки) — пользователь
/// должен видеть, на что именно соглашается.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog() => InitializeComponent();

    public static bool Ask(Window? owner, string title, string question, string details, string acceptText)
    {
        var dialog = new ConfirmDialog();
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.QuestionText.Text = question;
        dialog.DetailsText.Text = details;
        dialog.DetailsBox.Visibility = details.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        dialog.AcceptButton.Content = acceptText;
        return dialog.ShowDialog() == true;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
}
