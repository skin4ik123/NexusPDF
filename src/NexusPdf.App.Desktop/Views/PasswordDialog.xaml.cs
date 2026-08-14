using System.Windows;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.Views;

public partial class PasswordDialog : Window
{
    private PasswordDialog(string fileName, bool wrongAttempt)
    {
        InitializeComponent();
        PromptText.Text = Loc.F("PasswordPrompt", fileName);
        WrongText.Visibility = wrongAttempt ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>Возвращает пароль или null при отмене. Пароль не журналируется.</summary>
    public static string? Show(Window? owner, string fileName, bool wrongAttempt)
    {
        var dialog = new PasswordDialog(fileName, wrongAttempt) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.PasswordInput.Password : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
