using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Выбор кодека под содержимое картинки. Проверяется не «алгоритм отработал»,
/// а три случая, ради которых он написан: фотографию нельзя сжимать без потерь
/// (она раздуется), скан нельзя держать цветным (переплата втрое), а схему
/// нельзя гнать через JPEG (ореолы вокруг линий и файл нередко БОЛЬШЕ).
/// </summary>
public sealed class ImageCodecChooserTests
{
    private const int Q = 75;

    /// <summary>BGRA-растр из функции цвета.</summary>
    private static byte[] Make(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b) = color(x, y);
            var o = (y * width + x) * 4;
            bgra[o] = b;
            bgra[o + 1] = g;
            bgra[o + 2] = r;
            bgra[o + 3] = 255;
        }
        return bgra;
    }

    private static uint Noise(int x, int y)
    {
        var seed = (uint)(x * 1664525 + y * 1013904223 + 777);
        return seed * 1664525 + 1013904223;
    }

    /// <summary>Фотография: плавные градиенты плюс шум сенсора.</summary>
    private static (byte, byte, byte) Photo(int x, int y)
    {
        var n = Noise(x, y);
        return ((byte)Math.Clamp(x / 3 + (int)((n >> 24) % 40), 0, 255),
                (byte)Math.Clamp(y / 3 + (int)((n >> 16) % 40), 0, 255),
                (byte)Math.Clamp((x + y) / 6 + (int)((n >> 8) % 40), 0, 255));
    }

    [Fact]
    public void Photograph_Is_Encoded_As_Colour_Jpeg()
    {
        var analysis = ImageCodecChooser.Analyze(Make(300, 300, Photo), 300, 300, Q);
        Assert.Equal(ImageContent.Photo, analysis.Content);
        Assert.Equal(ImageCodec.ColorJpeg, analysis.Jpeg.Codec);
        Assert.Equal(Q, analysis.Jpeg.Quality);
        Assert.False(analysis.TryLossless);
    }

    [Fact]
    public void Grey_Scan_Is_Encoded_As_Grey_Jpeg()
    {
        // Скан: серые полутона с шумом сканера, каналы почти совпадают.
        var analysis = ImageCodecChooser.Analyze(GreyScan(300, 300), 300, 300, Q);
        Assert.Equal(ImageContent.GrayScan, analysis.Content);
        Assert.True(analysis.Jpeg.IsGray);
    }

    /// <summary>
    /// Ключевой случай: у серого скана ВСЕ 256 оттенков, и счёт цветов принял бы
    /// его за фотографию либо за графику. Признак «сосед побайтово равен» не
    /// путается — шум сенсора не даёт совпадений.
    /// </summary>
    private static byte[] GreyScan(int w, int h) => Make(w, h, (x, y) =>
    {
        var n = Noise(x, y);
        var v = (byte)Math.Clamp(150 + (int)(n % 100) + (x % 13) * 6, 0, 255);
        return (v, (byte)Math.Clamp(v + 3, 0, 255), (byte)Math.Clamp(v - 4, 0, 255));
    });

    [Fact]
    public void Grey_Scan_Gets_Higher_Quality_At_The_Same_Weight()
    {
        // Цветовых плоскостей у серого нет — те же байты уходят в детали.
        var analysis = ImageCodecChooser.Analyze(GreyScan(200, 200), 200, 200, Q);
        Assert.True(analysis.Jpeg.Quality > Q, "Серому скану качество поднимается, а не остаётся прежним.");
        Assert.True(analysis.Jpeg.Quality <= 95, "Выше 95 качество JPEG уже только раздувает файл.");
    }

    [Fact]
    public void Screenshot_Of_An_Interface_Is_Graphics()
    {
        // Снимок экрана: плоские заливки и резкие границы «окон».
        var shot = Make(400, 300, (x, y) =>
        {
            if (y < 40) return ((byte)0x2D, (byte)0x2D, (byte)0x30);       // заголовок
            if (x % 120 < 4) return ((byte)0x00, (byte)0x78, (byte)0xD4);  // рамки
            return ((byte)0xFF, (byte)0xFF, (byte)0xFF);
        });
        var analysis = ImageCodecChooser.Analyze(shot, 400, 300, Q);
        Assert.Equal(ImageContent.Graphics, analysis.Content);
        Assert.True(analysis.TryLossless);
    }

    [Fact]
    public void Diagram_With_Flat_Fills_Is_Graphics()
    {
        var diagram = Make(400, 400, (x, y) =>
            ((x / 50 + y / 50) % 2 == 0)
                ? ((byte)0xFF, (byte)0xFF, (byte)0xFF)
                : ((byte)0x1F, (byte)0x6F, (byte)0xEB));
        Assert.Equal(ImageContent.Graphics, ImageCodecChooser.Analyze(diagram, 400, 400, Q).Content);
    }

    [Fact]
    public void A_Blank_Or_Broken_Image_Does_Not_Throw()
    {
        Assert.Equal(ImageCodec.ColorJpeg, ImageCodecChooser.Analyze(Array.Empty<byte>(), 0, 0, Q).Jpeg.Codec);
        Assert.Equal(ImageCodec.ColorJpeg, ImageCodecChooser.Analyze(new byte[8], 100, 100, Q).Jpeg.Codec);
    }

    [Fact]
    public void Lossless_Size_Is_Measured_Not_Guessed()
    {
        // Схема сжимается без потерь в разы; шум — почти никак. Это и есть та
        // разница, на которой принимается решение.
        var diagram = Make(300, 300, (x, y) =>
            (x / 60 + y / 60) % 2 == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));
        var flatBytes = ImageCodecChooser.EstimateLosslessBytes(diagram, 300, 300);
        var noisyBytes = ImageCodecChooser.EstimateLosslessBytes(Make(300, 300, Photo), 300, 300);

        var raw = 300L * 300 * 3;
        Assert.True(flatBytes < raw / 50, $"Схема обязана ужиматься в разы: {flatBytes} из {raw}.");
        Assert.True(noisyBytes > flatBytes * 20, $"Шум без потерь не жмётся: {noisyBytes} против {flatBytes}.");
    }

    [Fact]
    public void Lossless_Wins_Unless_It_Costs_Several_Times_More()
    {
        Assert.True(ImageCodecChooser.LosslessWins(5_000, 20_000));   // ещё и меньше JPEG
        Assert.True(ImageCodecChooser.LosslessWins(25_000, 10_000));  // дороже, но линии целы
        Assert.False(ImageCodecChooser.LosslessWins(90_000, 10_000)); // девятикратно — уже нет
    }

    [Fact]
    public void Result_Is_Applied_Only_When_It_Really_Is_Smaller()
    {
        Assert.True(ImageCodecChooser.IsWorthReplacing(100_000, 40_000));
        Assert.False(ImageCodecChooser.IsWorthReplacing(100_000, 120_000));
        // Выигрыш в доли процента не стоит потери качества.
        Assert.False(ImageCodecChooser.IsWorthReplacing(100_000, 99_000));
    }
}
