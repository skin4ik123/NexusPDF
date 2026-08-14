using System.Windows;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.Views;

public partial class PasswordSetDialog : Window
{
    private PasswordSetDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Password1.Focus();
    }

    /// <summary>Возвращает подтверждённый пароль или null при отмене. Пароль не журналируется.</summary>
    public static string? Show(Window? owner)
    {
        var dialog = new PasswordSetDialog { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Password1.Password : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Password1.Password.Length == 0)
        {
            ErrorLabel.Text = Loc.Get("PasswordEmpty");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }
        if (Password1.Password != Password2.Password)
        {
            ErrorLabel.Text = Loc.Get("PasswordMismatch");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
