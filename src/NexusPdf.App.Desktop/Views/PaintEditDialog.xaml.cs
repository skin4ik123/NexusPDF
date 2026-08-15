using System.Windows;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Как вернуть отредактированную область на страницу.</summary>
public enum RegionReturnMode
{
    /// <summary>Наложить поверх: страница остаётся текстовой, прежнее содержимое под картинкой ОСТАЁТСЯ в файле.</summary>
    Overlay = 0,

    /// <summary>Заменить с уничтожением: страница становится изображением, прежнее содержимое исчезает целиком.</summary>
    DestroyOriginal = 1,
}

public sealed record PaintEditRequest(
    double Dpi, bool Grayscale, bool RunOcrAfter, RegionReturnMode RegionMode);

public partial class PaintEditDialog : Window
{
    private PaintEditRequest? _result;

    private PaintEditDialog() => InitializeComponent();

    /// <param name="wholePage">
    /// true — правится вся страница (её содержимое станет изображением);
    /// false — правится выделенная область (остальная страница не тронута).
    /// </param>
    public static PaintEditRequest? Show(Window? owner, bool wholePage, bool ocrAvailable)
    {
        var dialog = new PaintEditDialog();
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.HeaderText.Text = Loc.Get(wholePage ? "PaintEditTitlePage" : "PaintEditTitleRegion");
        dialog.WarningText.Text = Loc.Get(wholePage ? "PaintEditWarningPage" : "PaintEditWarningRegion");
        dialog.OcrCheck.Visibility = ocrAvailable ? Visibility.Visible : Visibility.Collapsed;
        dialog.OcrHint.Visibility = ocrAvailable ? Visibility.Visible : Visibility.Collapsed;
        dialog.RegionModePanel.Visibility = wholePage ? Visibility.Collapsed : Visibility.Visible;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        var dpi = DpiCombo.SelectedIndex switch { 0 => 150.0, 2 => 600.0, _ => 300.0 };
        _result = new PaintEditRequest(
            dpi, ColorCombo.SelectedIndex == 1, OcrCheck.IsChecked == true,
            ModeDestroy.IsChecked == true ? RegionReturnMode.DestroyOriginal : RegionReturnMode.Overlay);
        DialogResult = true;
    }
}
