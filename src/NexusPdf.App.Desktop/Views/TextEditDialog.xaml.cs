using System.Windows;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Правка существующей строки PDF. Проверка «шрифт умеет нарисовать это»
/// идёт по мере ввода: пользователь должен узнать о пропавших буквах здесь,
/// а не после сохранения файла.
/// </summary>
public partial class TextEditDialog : Window
{
    private Func<string, Task<bool>>? _canRender;
    private CancellationTokenSource? _checkCts;

    private TextEditDialog() => InitializeComponent();

    /// <summary>Возвращает новый текст или null, если пользователь отказался.</summary>
    public static string? Edit(
        Window? owner, string currentText, string fontName, bool isEmbedded, double fontSizePt,
        Func<string, Task<bool>> canRender)
    {
        var dialog = new TextEditDialog { _canRender = canRender };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.FontInfo.Text = Loc.F("TextEditFontInfo",
            fontName.Length > 0 ? fontName : Loc.Get("TextEditFontUnknown"),
            fontSizePt.ToString("0.#"),
            Loc.Get(isEmbedded ? "TextEditFontEmbedded" : "TextEditFontStandard"));
        dialog.EditBox.Text = currentText;
        dialog.EditBox.SelectAll();
        dialog.EditBox.Focus();
        return dialog.ShowDialog() == true ? dialog.EditBox.Text : null;
    }

    private async void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_canRender == null) return;
        _checkCts?.Cancel();
        var cts = new CancellationTokenSource();
        _checkCts = cts;
        var text = EditBox.Text;
        try
        {
            // Небольшая пауза: проверка идёт в движок, дёргать её на каждую
            // букву незачем.
            await Task.Delay(250, cts.Token);
            var ok = await _canRender(text);
            if (cts.IsCancellationRequested) return;
            WarningBox.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            WarningText.Text = Loc.Get("TextEditFontMissingGlyphs");
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил печатать — эта проверка больше не нужна.
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
