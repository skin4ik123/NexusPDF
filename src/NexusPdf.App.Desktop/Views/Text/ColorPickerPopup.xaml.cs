using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>Цвет текста: шесть основных, недавние и свой — числом «#RRGGBB».</summary>
public partial class ColorPickerPopup : UserControl
{
    /// <summary>Те же шесть, что и в правке строки.</summary>
    private static readonly uint[] Swatches = [0xFF1B1C20, 0xFF6B7280, 0xFFD3282F, 0xFF2563EB, 0xFF15803D, 0xFFB45309];

    private const int MaxRecent = 6;
    private static readonly List<uint> RecentColors = new();

    public ColorPickerPopup(uint current)
    {
        InitializeComponent();
        foreach (var argb in Swatches)
            Palette.Children.Add(Swatch(argb));
        foreach (var argb in RecentColors)
            Recent.Children.Add(Swatch(argb));
        RecentLabel.Visibility = RecentColors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Hex.Text = TextBlockStyleCommands.FormatColor(current);
        Apply.Click += (_, _) => ApplyHex();
        Hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyHex();
            e.Handled = true;
        };
    }

    public event EventHandler<uint>? ColorPicked;

    private void ApplyHex()
    {
        if (TextBlockStyleCommands.TryParseColor(Hex.Text, out var argb))
            Pick(argb);
        else
            HexError.Visibility = Visibility.Visible;
    }

    private void Pick(uint argb)
    {
        RecentColors.Remove(argb);
        RecentColors.Insert(0, argb);
        if (RecentColors.Count > MaxRecent)
            RecentColors.RemoveRange(MaxRecent, RecentColors.Count - MaxRecent);
        ColorPicked?.Invoke(this, argb);
    }

    private Button Swatch(uint argb)
    {
        var name = TextBlockStyleCommands.FormatColor(argb);
        var button = new Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(2),
            Focusable = false,
            ToolTip = name,
            Content = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => Pick(argb);
        return button;
    }
}
