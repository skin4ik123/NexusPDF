using System.Windows;
using System.Windows.Controls;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views;

/// <param name="Dpi">Целевое разрешение изображений.</param>
/// <param name="Quality">Качество кодирования с потерями.</param>
/// <param name="StructureOnly">Изображения не трогать.</param>
/// <param name="SubsetFonts">Урезать встроенные шрифты.</param>
public sealed record CompressImagesRequest(
    double Dpi, int Quality, bool StructureOnly = false, bool SubsetFonts = true);

public partial class CompressImagesDialog : Window
{
    private static readonly CompressionPresetKind[] Order =
    {
        CompressionPresetKind.Smart,
        CompressionPresetKind.Quality,
        CompressionPresetKind.Balanced,
        CompressionPresetKind.Aggressive,
        CompressionPresetKind.Structure,
        CompressionPresetKind.Custom,
    };

    private CompressImagesRequest? _result;
    private DocumentImageProfile _profile = DocumentImageProfile.Unknown;

    private CompressImagesDialog() => InitializeComponent();

    /// <param name="profile">
    /// Что за документ. От него зависит «умный» режим, и он же показывается
    /// пользователю: «сжимать нечего» лучше узнать ДО того, как ждать минуту.
    /// </param>
    public static CompressImagesRequest? Show(Window? owner, DocumentImageProfile? profile = null)
    {
        var dialog = new CompressImagesDialog { _profile = profile ?? DocumentImageProfile.Unknown };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowProfile();
        dialog.UpdateHint();
        dialog.ShowDialog();
        return dialog._result;
    }

    private void ShowProfile()
    {
        if (_profile.Pages == 0)
        {
            ProfileText.Text = Loc.Get("CompressProfileUnknown");
            return;
        }
        ProfileText.Text = _profile.LooksScanned
            ? Loc.F("CompressProfileScanned", _profile.AverageImageDpi.ToString("0"))
            : Loc.F("CompressProfileLayout", _profile.ImagesOnSampledPages, _profile.SampledPages);
    }

    private CompressionPresetKind SelectedPreset =>
        Order[Math.Clamp(PresetCombo.SelectedIndex, 0, Order.Length - 1)];

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomPanel == null) return; // до InitializeComponent событий нет
        CustomPanel.Visibility = SelectedPreset == CompressionPresetKind.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateHint();
    }

    /// <summary>Показывает КОНКРЕТНЫЕ числа выбранного режима, а не только название.</summary>
    private void UpdateHint()
    {
        if (PresetHint == null) return;
        var kind = SelectedPreset;
        if (kind == CompressionPresetKind.Custom)
        {
            PresetHint.Text = Loc.Get("CompressPresetCustomHint");
            return;
        }
        var settings = CompressionPresets.Resolve(kind, _profile);
        PresetHint.Text = settings.StructureOnly
            ? Loc.Get("CompressPresetStructureHint")
            : Loc.F("CompressPresetNumbers", settings.Dpi.ToString("0"), settings.Quality);
    }

    private void OnCompress(object sender, RoutedEventArgs e)
    {
        var kind = SelectedPreset;
        var customDpi = DpiCombo.SelectedIndex switch { 1 => 96.0, 2 => 72.0, _ => 150.0 };
        var customQuality = QualityCombo.SelectedIndex switch { 1 => 85, 2 => 60, _ => 75 };
        var settings = CompressionPresets.Resolve(kind, _profile, customDpi, customQuality);

        _result = new CompressImagesRequest(
            settings.Dpi, settings.Quality, settings.StructureOnly,
            SubsetFontsBox.IsChecked == true);
        DialogResult = true;
    }
}
