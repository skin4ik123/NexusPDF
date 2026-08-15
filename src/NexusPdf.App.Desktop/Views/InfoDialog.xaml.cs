using System.Windows;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Сообщение об успешно завершённом действии. Отдельно от ErrorDialog:
/// сообщать об удачном результате окном с надписью «Ошибка» — неправильно.
/// </summary>
public partial class InfoDialog : Window
{
    private InfoDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    public static void Show(Window? owner, string title, string message)
    {
        var dialog = new InfoDialog(title, message);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }
}
