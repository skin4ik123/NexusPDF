namespace NexusPdf.Ux;

/// <summary>Готовые режимы сжатия — от «почти не трогать» до «максимум».</summary>
public enum CompressionPresetKind
{
    /// <summary>Разбирается в документе сам: скану — одно, вёрстке — другое.</summary>
    Smart,

    /// <summary>Бережно: заметно меньше, качество почти не страдает.</summary>
    Quality,

    /// <summary>Разумный компромисс.</summary>
    Balanced,

    /// <summary>Сильно: для рассылки и хранения, качество ощутимо ниже.</summary>
    Aggressive,

    /// <summary>Только структура: изображения не трогаются вовсе.</summary>
    Structure,

    /// <summary>Числа задаёт пользователь.</summary>
    Custom,
}

/// <summary>Что за документ перед нами — на этом строится «умный» режим.</summary>
/// <param name="Pages">Всего страниц.</param>
/// <param name="ImagesOnSampledPages">Изображений на просмотренных страницах.</param>
/// <param name="TextLengthOnSampledPages">Символов текста там же.</param>
/// <param name="SampledPages">Сколько страниц просмотрено (пробы хватает).</param>
/// <param name="AverageImageDpi">Среднее фактическое разрешение изображений.</param>
public sealed record DocumentImageProfile(
    int Pages, int ImagesOnSampledPages, int TextLengthOnSampledPages,
    int SampledPages, double AverageImageDpi)
{
    /// <summary>Сколько страниц имеет смысл просмотреть: дальше выводы не меняются.</summary>
    public const int SampleLimit = 12;

    /// <summary>
    /// Документ ведёт себя как скан: почти на каждой странице картинка, а
    /// текста почти нет. Именно у таких файлов сжатие изображений решает всё.
    /// </summary>
    public bool LooksScanned
    {
        get
        {
            var sampled = Math.Max(1, SampledPages);
            return ImagesOnSampledPages >= Math.Max(1, sampled / 2) &&
                   TextLengthOnSampledPages < 40 * sampled;
        }
    }

    public static DocumentImageProfile Unknown { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>Настройки сжатия: целевое разрешение, качество и режим.</summary>
public readonly record struct CompressionSettings(double Dpi, int Quality, bool StructureOnly);

/// <summary>
/// Пресеты сжатия.
///
/// Прежние числа (качество 35–56) были подобраны при движке, который молча НЕ
/// уменьшал изображения: низкое качество было единственным источником
/// выигрыша, и документы выходили мыльными. Теперь плотность честно приводится
/// к цели, и качество можно держать человеческое: основной выигрыш даёт
/// уменьшение лишних пикселей, а не растаптывание оставшихся.
///
/// Разница между «умным» и остальными не в движке, а в смелости: скан
/// переживает 130 DPI незаметно, а вёрстка с тонкими линиями требует больше.
/// Поэтому «умное» сначала смотрит на документ.
/// </summary>
public static class CompressionPresets
{
    public const double MinDpi = 50;
    public const double MaxDpi = 300;
    public const int MinQuality = 20;
    public const int MaxQuality = 95;

    public static CompressionSettings Resolve(
        CompressionPresetKind kind, DocumentImageProfile profile,
        double customDpi = 150, int customQuality = 75) => kind switch
    {
        CompressionPresetKind.Smart => profile.LooksScanned
            // У скана с высоким разрешением запас больше: 130 DPI на экране и
            // в офисной печати не отличить от 170, а весит он вдвое меньше.
            // Скан, который УЖЕ около 120 DPI, резать не из чего — цель выше
            // его плотности означает «не трогать», и это честно.
            ? (profile.AverageImageDpi > 160
                ? new CompressionSettings(130, 68, false)
                : new CompressionSettings(150, 70, false))
            : new CompressionSettings(150, 76, false),
        CompressionPresetKind.Quality => new CompressionSettings(220, 85, false),
        CompressionPresetKind.Balanced => new CompressionSettings(120, 60, false),
        CompressionPresetKind.Aggressive => new CompressionSettings(96, 45, false),
        CompressionPresetKind.Structure => new CompressionSettings(130, 60, true),
        _ => new CompressionSettings(
            Math.Clamp(customDpi, MinDpi, MaxDpi),
            Math.Clamp(customQuality, MinQuality, MaxQuality),
            false),
    };

    /// <summary>Ключ строки с названием режима.</summary>
    public static string TitleKey(CompressionPresetKind kind) => kind switch
    {
        CompressionPresetKind.Smart => "CompressPresetSmart",
        CompressionPresetKind.Quality => "CompressPresetQuality",
        CompressionPresetKind.Balanced => "CompressPresetBalanced",
        CompressionPresetKind.Aggressive => "CompressPresetAggressive",
        CompressionPresetKind.Structure => "CompressPresetStructure",
        _ => "CompressPresetCustom",
    };
}
