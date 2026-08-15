using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Режимы сжатия. Проверяется не «функция вернула число», а обещания, которые
/// пользователь читает в окне: бережный режим действительно бережнее
/// максимального, «только структура» действительно не трогает изображения, а
/// «умный» отличает скан от вёрстки.
/// </summary>
public sealed class CompressionPresetsTests
{
    private static DocumentImageProfile Scan(double dpi) =>
        new(Pages: 40, ImagesOnSampledPages: 12, TextLengthOnSampledPages: 0,
            SampledPages: 12, AverageImageDpi: dpi);

    private static DocumentImageProfile Layout() =>
        new(Pages: 40, ImagesOnSampledPages: 3, TextLengthOnSampledPages: 9000,
            SampledPages: 12, AverageImageDpi: 300);

    [Fact]
    public void A_Scan_Is_Told_Apart_From_A_Layout()
    {
        Assert.True(Scan(300).LooksScanned);
        Assert.False(Layout().LooksScanned);
    }

    [Fact]
    public void A_Text_Document_Without_Images_Is_Not_A_Scan()
    {
        var text = new DocumentImageProfile(10, 0, 20000, 10, 0);
        Assert.False(text.LooksScanned);
    }

    [Fact]
    public void Smart_Presses_A_High_Resolution_Scan_Harder()
    {
        var dense = CompressionPresets.Resolve(CompressionPresetKind.Smart, Scan(300));
        var modest = CompressionPresets.Resolve(CompressionPresetKind.Smart, Scan(120));
        Assert.True(dense.Dpi < modest.Dpi,
            "У скана с запасом разрешения его и надо забирать.");
        Assert.True(dense.Quality <= modest.Quality);
    }

    [Fact]
    public void Smart_Is_Careful_With_A_Layout()
    {
        var layout = CompressionPresets.Resolve(CompressionPresetKind.Smart, Layout());
        var scan = CompressionPresets.Resolve(CompressionPresetKind.Smart, Scan(300));
        Assert.True(layout.Dpi > scan.Dpi, "Тонкие линии вёрстки нельзя резать как скан.");
        Assert.False(layout.StructureOnly);
    }

    [Fact]
    public void Gentle_Is_Gentler_Than_Balanced_And_That_Than_Maximum()
    {
        var gentle = CompressionPresets.Resolve(CompressionPresetKind.Quality, Layout());
        var balanced = CompressionPresets.Resolve(CompressionPresetKind.Balanced, Layout());
        var max = CompressionPresets.Resolve(CompressionPresetKind.Aggressive, Layout());

        Assert.True(gentle.Dpi > balanced.Dpi && balanced.Dpi > max.Dpi);
        Assert.True(gentle.Quality > balanced.Quality && balanced.Quality > max.Quality);
    }

    [Fact]
    public void Structure_Only_Really_Does_Not_Touch_Images()
    {
        Assert.True(CompressionPresets.Resolve(CompressionPresetKind.Structure, Scan(300)).StructureOnly);
        foreach (var kind in new[]
                 {
                     CompressionPresetKind.Smart, CompressionPresetKind.Quality,
                     CompressionPresetKind.Balanced, CompressionPresetKind.Aggressive,
                     CompressionPresetKind.Custom,
                 })
            Assert.False(CompressionPresets.Resolve(kind, Scan(300)).StructureOnly);
    }

    [Fact]
    public void Custom_Numbers_Are_Kept_Within_Sane_Limits()
    {
        var wild = CompressionPresets.Resolve(CompressionPresetKind.Custom, Layout(), 5000, 200);
        Assert.Equal(CompressionPresets.MaxDpi, wild.Dpi);
        Assert.Equal(CompressionPresets.MaxQuality, wild.Quality);

        var tiny = CompressionPresets.Resolve(CompressionPresetKind.Custom, Layout(), 1, 1);
        Assert.Equal(CompressionPresets.MinDpi, tiny.Dpi);
        Assert.Equal(CompressionPresets.MinQuality, tiny.Quality);
    }

    [Fact]
    public void Custom_Keeps_What_The_User_Typed()
    {
        var mine = CompressionPresets.Resolve(CompressionPresetKind.Custom, Layout(), 96, 60);
        Assert.Equal(96, mine.Dpi);
        Assert.Equal(60, mine.Quality);
    }

    [Fact]
    public void An_Unexamined_Document_Does_Not_Get_Scan_Settings()
    {
        // «Неизвестно» обязано вести себя как вёрстка: испортить скан не так
        // страшно, как порезать чертёж.
        var unknown = CompressionPresets.Resolve(CompressionPresetKind.Smart, DocumentImageProfile.Unknown);
        Assert.Equal(CompressionPresets.Resolve(CompressionPresetKind.Smart, Layout()), unknown);
    }

    [Fact]
    public void Every_Mode_Has_Its_Own_Caption()
    {
        var keys = Enum.GetValues<CompressionPresetKind>().Select(CompressionPresets.TitleKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
