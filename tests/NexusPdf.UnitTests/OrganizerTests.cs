using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>Организатор страниц: подписи диапазонов и размер миниатюр.</summary>
public sealed class OrganizerTests
{
    [Fact]
    public void Consecutive_Pages_Collapse_Into_A_Range()
    {
        // Индексы с нуля → номера с единицы.
        Assert.Equal("1, 3–5, 8", PageRanges.Format(new[] { 0, 2, 3, 4, 7 }));
        Assert.Equal("2", PageRanges.Format(new[] { 1 }));
        Assert.Equal("1–3", PageRanges.Format(new[] { 2, 0, 1, 1 }));   // порядок и повторы не важны
        Assert.Equal("", PageRanges.Format(Array.Empty<int>()));
    }

    [Fact]
    public void Thumbnail_Raster_Grows_In_Steps_And_Never_Below_The_Old_Size()
    {
        Assert.Equal(150, ThumbnailSizing.PixelWidth(150, 1.0));
        Assert.Equal(150, ThumbnailSizing.PixelWidth(90, 1.0));
        Assert.Equal(200, ThumbnailSizing.PixelWidth(160, 1.0));   // ступень по 50
        Assert.Equal(200, ThumbnailSizing.PixelWidth(150, 1.25));  // плотный экран — больше пикселей
        Assert.Equal(450, ThumbnailSizing.PixelWidth(420, 1.0));
    }

    [Fact]
    public void Card_Width_Stays_Within_Sane_Limits()
    {
        Assert.Equal(ThumbnailSizing.MinWidth, ThumbnailSizing.Clamp(10));
        Assert.Equal(ThumbnailSizing.MaxWidth, ThumbnailSizing.Clamp(5000));
        Assert.Equal(ThumbnailSizing.DefaultWidth, ThumbnailSizing.Clamp(double.NaN));
        Assert.Equal(200, ThumbnailSizing.Clamp(200));
    }
}
