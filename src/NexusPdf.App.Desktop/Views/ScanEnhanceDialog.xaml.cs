using System.Windows;
using System.Windows.Media.Imaging;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Imaging;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Растр текущей страницы для предпросмотра.</summary>
public sealed record ScanPreviewPage(byte[] Bgra, int PixelWidth, int PixelHeight, int PageNumber);

public partial class ScanEnhanceDialog : Window
{
    private ScanPreviewPage? _page;
    private SkewEstimate _skew;
    private ScanEnhanceOptions? _result;

    private ScanEnhanceDialog() => InitializeComponent();

    /// <param name="page">Текущая страница: на ней и показывается «до/после».</param>
    /// <param name="pageCount">Всего страниц — чтобы сказать, к чему применится.</param>
    public static ScanEnhanceOptions? Show(Window? owner, ScanPreviewPage? page, int pageCount)
    {
        var dialog = new ScanEnhanceDialog { _page = page };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;

        dialog.ScopeText.Text = Loc.F("EnhanceScope", pageCount);
        dialog.Prepare();
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Prepare()
    {
        if (_page == null)
        {
            SkewText.Text = Loc.Get("EnhanceNoPage");
            return;
        }

        _skew = SkewDetector.Detect(_page.Bgra, _page.PixelWidth, _page.PixelHeight);
        SkewText.Text = _skew.IsWorthFixing
            ? Loc.F("EnhanceSkewFound", _page.PageNumber, Math.Abs(_skew.AngleDegrees).ToString("0.0"),
                _skew.AngleDegrees > 0 ? Loc.Get("EnhanceCounterClockwise") : Loc.Get("EnhanceClockwise"))
            : Loc.F("EnhanceSkewNone", _page.PageNumber);

        BeforeImage.Source = ImageEncoder.ToBitmap(_page.Bgra, _page.PixelWidth, _page.PixelHeight);
        UpdatePreview();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>
    /// Предпросмотр считается ТЕМИ ЖЕ функциями, что применяются к документу,
    /// поэтому показанное и есть будущий результат, а не «примерно так».
    /// </summary>
    private void UpdatePreview()
    {
        if (_page == null || AfterImage == null) return;

        var pixels = (byte[])_page.Bgra.Clone();
        var width = _page.PixelWidth;
        var height = _page.PixelHeight;

        if (DeskewBox.IsChecked == true && _skew.IsWorthFixing)
            pixels = ScanCleanup.Rotate(pixels, width, height, _skew.AngleDegrees);
        if (BackgroundBox.IsChecked == true)
            ScanCleanup.LevelBackground(pixels, width, height);
        if (DespeckleBox.IsChecked == true)
            ScanCleanup.Despeckle(pixels, width, height);

        AfterImage.Source = ImageEncoder.ToBitmap(pixels, width, height);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (DeskewBox.IsChecked != true && DespeckleBox.IsChecked != true &&
            BackgroundBox.IsChecked != true)
        {
            SkewText.Text = Loc.Get("EnhanceNothingChosen");
            return;
        }

        _result = new ScanEnhanceOptions(
            Deskew: DeskewBox.IsChecked == true,
            Despeckle: DespeckleBox.IsChecked == true,
            LevelBackground: BackgroundBox.IsChecked == true);
        DialogResult = true;
    }
}
