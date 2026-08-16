using System.Windows;
using System.Windows.Controls;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Application;
using NexusPdf.Imaging;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Растр текущей страницы для предпросмотра «до/после».</summary>
public sealed record ScanPreviewPage(byte[] Bgra, int PixelWidth, int PixelHeight, int PageNumber);

/// <summary>
/// Одно окно на всю подготовку документа: качество страниц, вес изображений и
/// структура файла.
///
/// Раньше это были три команды, каждая со своим «сохранить как»: чтобы почистить
/// скан и сжать его, приходилось делать документ дважды и складывать на диск
/// два промежуточных файла. Здесь всё выбирается сразу, применяется К ОТКРЫТОМУ
/// документу, и сохранение остаётся обычным — тогда и туда, куда захочет
/// пользователь.
///
/// Порядок шагов задаёт конвейер (<see cref="ProcessingPlan"/>), а не порядок
/// галочек в окне, и он показан пользователю прямо здесь: чистка кладёт растры
/// несжатыми, поэтому пересжатие обязано идти после неё, а не до.
/// </summary>
public partial class OptimizeDocumentDialog : Window
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

    private ScanPreviewPage? _page;
    private SkewEstimate _skew;
    private DocumentImageProfile _profile = DocumentImageProfile.Unknown;
    private bool _hasStructureEngine = true;
    private ProcessingPlan? _result;

    /// <summary>
    /// Разметка ещё разбирается. Галочки со значением по умолчанию поднимают
    /// Checked прямо во время разбора, и обработчик видит окно наполовину
    /// собранным: поля ниже по XAML ещё не существуют. Проверять каждое из них
    /// на null — это ловить одно и то же по одному; флаг закрывает разом.
    /// </summary>
    private bool _ready;

    private OptimizeDocumentDialog()
    {
        InitializeComponent();
        _ready = true;
    }

    /// <param name="page">Текущая страница: на ней показывается «до/после».</param>
    /// <param name="pageCount">Всего страниц — чтобы сказать, к чему применится.</param>
    /// <param name="profile">Что за документ: от него зависит «умный» режим сжатия.</param>
    /// <param name="hasStructureEngine">Есть ли qpdf; без него структуру трогать нечем.</param>
    public static ProcessingPlan? Show(
        Window? owner, ScanPreviewPage? page, int pageCount,
        DocumentImageProfile? profile, bool hasStructureEngine)
    {
        var dialog = new OptimizeDocumentDialog
        {
            _page = page,
            _profile = profile ?? DocumentImageProfile.Unknown,
            _hasStructureEngine = hasStructureEngine,
        };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;

        dialog.ScopeText.Text = Loc.F("OptimizeScope", pageCount);
        dialog.Prepare();
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Prepare()
    {
        ShowProfile();

        if (!_hasStructureEngine)
        {
            StructureBox.IsChecked = false;
            StructureBox.IsEnabled = false;
            StructureHint.Text = Loc.Get("OptimizeStructureUnavailable");
        }
        else
        {
            StructureHint.Text = Loc.Get("OptimizeStructureHint");
        }

        // Документ без сканов чистить незачем: галочка снимается заранее, чтобы
        // человек не ждал минуту ради нулевого результата.
        if (_profile.Pages > 0 && !_profile.LooksScanned)
            QualityBox.IsChecked = false;

        if (_page == null)
        {
            SkewText.Text = Loc.Get("EnhanceNoPage");
        }
        else
        {
            _skew = SkewDetector.Detect(_page.Bgra, _page.PixelWidth, _page.PixelHeight);
            SkewText.Text = _skew.IsWorthFixing
                ? Loc.F("EnhanceSkewFound", _page.PageNumber, Math.Abs(_skew.AngleDegrees).ToString("0.0"),
                    _skew.AngleDegrees > 0 ? Loc.Get("EnhanceCounterClockwise") : Loc.Get("EnhanceClockwise"))
                : Loc.F("EnhanceSkewNone", _page.PageNumber);
            BeforeImage.Source = ImageEncoder.ToBitmap(_page.Bgra, _page.PixelWidth, _page.PixelHeight);
        }

        UpdateHint();
        Refresh();
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

    private bool QualityOn => QualityBox.IsChecked == true;
    private bool CompressOn => CompressBox.IsChecked == true;

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Refresh();

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        CustomPanel.Visibility = SelectedPreset == CompressionPresetKind.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateHint();
        UpdateOrder();
    }

    /// <summary>Показывает КОНКРЕТНЫЕ числа выбранного режима, а не только название.</summary>
    private void UpdateHint()
    {
        if (!_ready) return;
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

    private void Refresh()
    {
        if (!_ready) return;

        QualityPanel.IsEnabled = QualityOn;
        CompressPanel.IsEnabled = CompressOn;
        BackgroundPanel.IsEnabled = QualityOn && BackgroundBox.IsChecked == true;

        var strength = (int)StrengthSlider.Value;
        StrengthValue.Text = strength.ToString();
        StrengthHint.Text = strength switch
        {
            <= 20 => Loc.Get("OptimizeStrengthGentle"),
            >= 80 => Loc.Get("OptimizeStrengthHard"),
            _ => Loc.Get("OptimizeStrengthNormal"),
        };

        UpdateOrder();
        UpdatePreview();
    }

    /// <summary>Очередь шагов словами — ровно та, что выполнит конвейер.</summary>
    private void UpdateOrder()
    {
        if (!_ready) return;
        var steps = new List<string>();
        if (QualityOn) steps.Add(Loc.Get("OptimizeStepQuality"));
        if (CompressOn) steps.Add(Loc.Get("OptimizeStepCompress"));
        if (StructureBox.IsChecked == true && _hasStructureEngine) steps.Add(Loc.Get("OptimizeStepStructure"));

        OrderText.Text = steps.Count == 0
            ? Loc.Get("OptimizeNothingChosen")
            : Loc.F("OptimizeOrder", string.Join(" → ", steps));
    }

    /// <summary>Набор галочек на момент запуска пересчёта.</summary>
    private readonly record struct PreviewRecipe(
        bool Quality, bool Deskew, bool Edges, bool Background, bool Despeckle,
        int Strength, bool Tint);

    /// <summary>
    /// Номер последнего запрошенного пересчёта. Ползунок силы шлёт событие на
    /// каждое деление, и без этого счётчика ответ более раннего пересчёта мог
    /// прийти последним и затереть картинку, соответствующую текущему положению.
    /// </summary>
    private int _previewGeneration;

    /// <summary>
    /// Предпросмотр считается ТЕМИ ЖЕ функциями, что применяются к документу,
    /// поэтому показанное и есть будущий результат, а не «примерно так».
    /// Сжатие и структуру на одной странице показать нечем — они меняют вес
    /// файла, а не картинку, и про это сказано в подписи.
    ///
    /// Считается в фоне. Раньше весь конвейер выполнялся прямо в обработчике, и
    /// на странице формата A4 при 300 точках на дюйм перетаскивание ползунка
    /// силы дёргалось: поток интерфейса был занят обработкой каждого деления.
    /// По той же причине надпись «Считаю…» не могла появиться на экране —
    /// перерисовывать её было некому.
    /// </summary>
    private async void UpdatePreview()
    {
        if (!_ready || _page == null) return;

        var recipe = new PreviewRecipe(
            QualityOn,
            DeskewBox.IsChecked == true && _skew.IsWorthFixing,
            EdgesBox.IsChecked == true,
            BackgroundBox.IsChecked == true,
            DespeckleBox.IsChecked == true,
            (int)StrengthSlider.Value,
            TintBox.IsChecked == true);

        var generation = ++_previewGeneration;
        var page = _page;
        var skew = _skew.AngleDegrees;
        BusyText.Visibility = Visibility.Visible;

        try
        {
            // Пауза гасит очередь промежуточных положений ползунка: считается
            // только то, на чём человек остановился.
            await Task.Delay(90);
            if (generation != _previewGeneration) return;

            var pixels = await Task.Run(() => Compute(page, recipe, skew));
            if (generation != _previewGeneration) return;

            AfterImage.Source = ImageEncoder.ToBitmap(pixels, page.PixelWidth, page.PixelHeight);
        }
        catch (Exception ex)
        {
            // Предпросмотр — вспомогательная вещь: окно должно остаться рабочим,
            // а применить настройки к документу можно и без картинки.
            Serilog.Log.Warning(ex, "Не удалось пересчитать предпросмотр оптимизации");
        }
        finally
        {
            if (generation == _previewGeneration)
                BusyText.Visibility = Visibility.Collapsed;
        }
    }

    private static byte[] Compute(ScanPreviewPage page, PreviewRecipe recipe, double skewAngle)
    {
        var pixels = (byte[])page.Bgra.Clone();
        var width = page.PixelWidth;
        var height = page.PixelHeight;

        if (recipe.Quality)
        {
            if (recipe.Deskew)
                pixels = ScanCleanup.Rotate(pixels, width, height, skewAngle);
            if (recipe.Edges)
                ScanCleanup.TrimDarkEdges(pixels, width, height);
            if (recipe.Background)
                ScanCleanup.LevelBackground(pixels, width, height,
                    new ScanCleanup.BackgroundOptions(recipe.Strength, recipe.Tint));
            if (recipe.Despeckle)
                ScanCleanup.Despeckle(pixels, width, height);
        }

        return pixels;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ScanEnhanceOptions? enhance = null;
        if (QualityOn && (DeskewBox.IsChecked == true || DespeckleBox.IsChecked == true ||
                          BackgroundBox.IsChecked == true || EdgesBox.IsChecked == true))
        {
            enhance = new ScanEnhanceOptions(
                Deskew: DeskewBox.IsChecked == true,
                Despeckle: DespeckleBox.IsChecked == true,
                LevelBackground: BackgroundBox.IsChecked == true,
                BackgroundStrength: (int)StrengthSlider.Value,
                NeutralizeTint: TintBox.IsChecked == true,
                TrimDarkEdges: EdgesBox.IsChecked == true);
        }

        PdfCompressionRequest? compress = null;
        if (CompressOn)
        {
            var customDpi = DpiCombo.SelectedIndex switch { 1 => 96.0, 2 => 72.0, _ => 150.0 };
            var customQuality = QualityCombo.SelectedIndex switch { 1 => 85, 2 => 60, _ => 75 };
            var settings = CompressionPresets.Resolve(SelectedPreset, _profile, customDpi, customQuality);
            compress = new PdfCompressionRequest(
                settings.Dpi, settings.Quality, settings.StructureOnly, SubsetFontsBox.IsChecked == true);
        }

        var plan = new ProcessingPlan(
            enhance, compress, StructureBox.IsChecked == true && _hasStructureEngine);
        if (plan.IsEmpty)
        {
            OrderText.Text = Loc.Get("OptimizeNothingChosen");
            return;
        }

        _result = plan;
        DialogResult = true;
    }
}
