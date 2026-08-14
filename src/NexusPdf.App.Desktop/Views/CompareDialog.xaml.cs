using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using Serilog;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Визуальное сравнение двух PDF: список страниц со статусами и просмотр
/// пары с красной подсветкой отличий. Сводка считается сразу, растры пары —
/// по выбору страницы (память не растёт с размером документа).
/// </summary>
public partial class CompareDialog : Window
{
    private sealed record Row(PageCompareInfo Info, string Label, Brush Brush);

    private readonly IPdfRenderEngine _engine;
    private string? _firstPath;
    private string? _secondPath;
    private CompareSession? _session;
    private bool _running;
    private int _imageRequestId;

    private CompareDialog(IPdfRenderEngine engine)
    {
        InitializeComponent();
        _engine = engine;
    }

    public static void Run(Window? owner, IPdfRenderEngine engine, string? initialFirstPath)
    {
        var dialog = new CompareDialog(engine);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        if (initialFirstPath != null)
        {
            dialog._firstPath = initialFirstPath;
            dialog.FirstButton.Content = Path.GetFileName(initialFirstPath);
        }
        dialog.ShowDialog();
    }

    private void OnPickFirst(object sender, RoutedEventArgs e) => Pick(isFirst: true);
    private void OnPickSecond(object sender, RoutedEventArgs e) => Pick(isFirst: false);

    private void Pick(bool isFirst)
    {
        var dialog = new OpenFileDialog { Filter = Loc.Get("PdfFilter") };
        if (dialog.ShowDialog(this) != true) return;
        if (isFirst)
        {
            _firstPath = dialog.FileName;
            FirstButton.Content = Path.GetFileName(dialog.FileName);
        }
        else
        {
            _secondPath = dialog.FileName;
            SecondButton.Content = Path.GetFileName(dialog.FileName);
        }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_firstPath == null || _secondPath == null)
        {
            SummaryLabel.Text = Loc.Get("CompareNeedFiles");
            return;
        }

        _running = true;
        RunButton.IsEnabled = false;
        PageList.ItemsSource = null;
        ClearImages();
        try
        {
            if (_session != null)
            {
                await _session.DisposeAsync();
                _session = null;
            }
            try
            {
                _session = await CompareSession.OpenAsync(
                    _engine, _firstPath, null, _secondPath, null, CancellationToken.None);
            }
            catch (PdfPasswordRequiredException)
            {
                SummaryLabel.Text = Loc.Get("CompareProtected");
                return;
            }

            FirstLabel.Text = Path.GetFileName(_firstPath);
            SecondLabel.Text = Path.GetFileName(_secondPath);
            var progress = new Progress<(int Done, int Total)>(p =>
                SummaryLabel.Text = Loc.F("CompareProgress", p.Done, p.Total));
            var summary = await _session.AnalyzeAsync(progress, CancellationToken.None);

            var rows = summary.Pages.Select(info => new Row(
                info,
                Loc.F("ComparePageLabel", info.PageIndex + 1, StatusText(info)),
                info.IsDifferent ? Brushes.IndianRed : Brushes.SeaGreen)).ToList();
            PageList.ItemsSource = rows;
            SummaryLabel.Text = summary.DifferentPages == 0
                ? Loc.Get("CompareIdentical")
                : Loc.F("CompareSummary", summary.DifferentPages, summary.Pages.Count);

            var firstDiff = rows.FirstOrDefault(r => r.Info.IsDifferent) ?? rows.FirstOrDefault();
            if (firstDiff != null)
                PageList.SelectedItem = firstDiff;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка сравнения документов");
            SummaryLabel.Text = Loc.Get("CompareError") + " " + ex.Message;
        }
        finally
        {
            _running = false;
            RunButton.IsEnabled = true;
        }
    }

    private static string StatusText(PageCompareInfo info) => info switch
    {
        { OnlyInFirst: true } => Loc.Get("CompareOnlyFirst"),
        { OnlyInSecond: true } => Loc.Get("CompareOnlySecond"),
        { SizeMismatch: true } => Loc.F("CompareSizeMismatch", info.DiffPercent.ToString("0.#")),
        { IsDifferent: true } => Loc.F("CompareDiffPercent", info.DiffPercent.ToString("0.#")),
        _ => Loc.Get("CompareSame"),
    };

    private async void OnPageSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PageList.SelectedItem is not Row row || _session == null) return;
        var requestId = ++_imageRequestId;
        try
        {
            var images = await _session.GetPageImagesAsync(row.Info.PageIndex, CancellationToken.None);
            if (requestId != _imageRequestId) return; // выбрали другую страницу
            FirstImage.Source = ToBitmap(images.First);
            SecondImage.Source = ToBitmap(images.Second);
            var diff = ToDiffOverlay(images);
            FirstDiff.Source = diff;
            SecondDiff.Source = diff;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось показать пару страниц {Page}", row.Info.PageIndex);
            ClearImages();
        }
    }

    private void ClearImages()
    {
        FirstImage.Source = null;
        SecondImage.Source = null;
        FirstDiff.Source = null;
        SecondDiff.Source = null;
        FirstLabel.Text = "";
        SecondLabel.Text = "";
    }

    private static BitmapSource? ToBitmap(RenderedPageImage? image)
    {
        if (image == null) return null;
        var bitmap = BitmapSource.Create(
            image.PixelWidth, image.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, image.Bgra, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Полупрозрачный красный слой в местах отличий.</summary>
    private static BitmapSource? ToDiffOverlay(PageCompareImages images)
    {
        if (images.DiffMask is not { } mask) return null;
        var overlay = new byte[mask.Length * 4];
        for (var i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) continue;
            var o = i * 4;
            overlay[o + 2] = 0xE5; // R
            overlay[o + 3] = 0x8C; // A ≈ 55%
        }
        var bitmap = BitmapSource.Create(
            images.Width, images.Height, 96, 96,
            PixelFormats.Bgra32, null, overlay, images.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }
    }
}
