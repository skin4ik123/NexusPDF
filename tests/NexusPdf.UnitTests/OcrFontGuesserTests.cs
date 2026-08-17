using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Подбор гарнитуры под оригинал. Раньше распознанный текст всегда писался
/// одним шрифтом, и документ с засечками после замены выглядел чужим.
/// </summary>
public sealed class OcrFontGuesserTests
{
    private const int PageW = 200;
    private const int PageH = 120;

    private static RenderedPageImage WhitePage()
    {
        var stride = PageW * 4;
        var pixels = new byte[stride * PageH];
        Array.Fill(pixels, (byte)0xFF);
        return new RenderedPageImage(PageW, PageH, stride, pixels);
    }

    private static void Ink(RenderedPageImage image, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var o = y * image.Stride + x * 4;
                image.Bgra[o] = 0; image.Bgra[o + 1] = 0; image.Bgra[o + 2] = 0;
            }
        }
    }

    /// <summary>Строка 100×20 в точке (40,40); один пиксель растра = один пункт.</summary>
    private static OcrTextLine Line() =>
        new("текст", 40, 40, 100, 20, 0xFFFFFFFF, 0xFF000000);

    /// <summary>Рисует вертикальные штрихи заданной толщины по всей высоте строки.</summary>
    private static void Strokes(RenderedPageImage image, int thickness)
    {
        for (var x = 44; x < 136; x += thickness + 6)
            Ink(image, x, 42, thickness, 16);
    }

    [Fact]
    public void Thin_Strokes_Are_Not_Bold()
    {
        var image = WhitePage();
        Strokes(image, 1);

        var guess = OcrFontGuesser.Of(image, 1, 1, Line());

        Assert.False(guess.Bold);
    }

    [Fact]
    public void Thick_Strokes_Are_Bold()
    {
        // Толстое перо при той же высоте строки — это и есть полужирное
        // начертание; заголовок скана обязан остаться заголовком.
        var image = WhitePage();
        Strokes(image, 5);

        var guess = OcrFontGuesser.Of(image, 1, 1, Line());

        Assert.True(guess.Bold);
    }

    [Fact]
    public void Plain_Strokes_Give_A_Sans_Face()
    {
        var image = WhitePage();
        Strokes(image, 2);

        var guess = OcrFontGuesser.Of(image, 1, 1, Line());

        Assert.Equal("Segoe UI", guess.Family);
        Assert.False(guess.Bold);
    }

    [Fact]
    public void Feet_At_The_Baseline_Give_A_Serif_Face()
    {
        // У шрифта с засечками низ буквы заканчивается горизонтальной лапкой,
        // поэтому у самой базовой линии чернил заметно больше, чем в середине.
        var image = WhitePage();
        Strokes(image, 2);
        for (var x = 44; x < 136; x += 8)
            Ink(image, x - 3, 57, 8, 3); // лапки в нижней полосе строки

        var guess = OcrFontGuesser.Of(image, 1, 1, Line());

        Assert.Equal("Times New Roman", guess.Family);
    }

    [Fact]
    public void Blank_Line_Falls_Back_To_The_Default_Face()
    {
        // Мерить нечего — берём шрифт без засечек: им набрано большинство
        // документов, и ошибка здесь незаметнее.
        var guess = OcrFontGuesser.Of(WhitePage(), 1, 1, Line());

        Assert.Equal("Segoe UI", guess.Family);
        Assert.False(guess.Bold);
    }
}
