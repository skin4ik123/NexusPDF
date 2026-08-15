using System.Windows;
using System.Windows.Input;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Запрос одной строки — например, имени профиля печати.</summary>
public partial class TextPromptDialog : Window
{
    private TextPromptDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>Введённая строка либо null, если пользователь отказался.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var dialog = new TextPromptDialog(title, prompt, initial);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.ValueBox.Text : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // Пустое имя — не ответ: закрывать окно с пустым результатом
        // значит молча ничего не сделать.
        if (string.IsNullOrWhiteSpace(ValueBox.Text)) return;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
    }
}
