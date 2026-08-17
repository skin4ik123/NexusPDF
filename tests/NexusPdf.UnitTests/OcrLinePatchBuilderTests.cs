using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Заплатка под строкой — тот же кусок скана, но без букв. На фотографии
/// документа заливка одним цветом видна как дыра, и страница после замены
/// текста выглядит хуже, чем была.
/// </summary>
public sealed class OcrLinePatchBuilderTests
{
    private const int PageW = 200;
    private const int PageH = 200;

    private static RenderedPageImage Page(Func<int, int, (byte B, byte G, byte R)> color)
    {
        var stride = PageW * 4;
        var pixels = new byte[stride * PageH];
        for (var y = 0; y < PageH; y++)
        {
            for (var x = 0; x < PageW; x++)
            {
                var (b, g, r) = color(x, y);
                var o = y * stride + x * 4;
                pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; pixels[o + 3] = 0xFF;
            }
        }
        return new RenderedPageImage(PageW, PageH, stride, pixels);
    }

    private static void Ink(RenderedPageImage image, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var o = y * image.Stride + x * 4;
                image.Bgra[o] = 0x10; image.Bgra[o + 1] = 0x10; image.Bgra[o + 2] = 0x10;
            }
        }
    }

    /// <summary>Строка 100×20 в точке (50,60); один пиксель растра = один пункт.</summary>
    private static OcrTextLine Line(uint background = 0xFFFFFFFF) =>
        new("текст", 50, 60, 100, 20, background, 0xFF101010);

    private static (byte B, byte G, byte R) At(OcrLinePatch patch, int col, int row)
    {
        var o = (row * patch.PixelWidth + col) * 4;
        return (patch.Bgra[o], patch.Bgra[o + 1], patch.Bgra[o + 2]);
    }

    [Fact]
    public void Letters_Are_Removed_And_Background_Survives()
    {
        // Полосатый фон — это защитная сетка документа. Буквы должны исчезнуть,
        // а полосы — пройти сквозь место строки насквозь.
        var image = Page((_, y) => y % 4 == 0 ? ((byte)0x80, (byte)0xC0, (byte)0xFF) : ((byte)0xF8, (byte)0xF8, (byte)0xF8));
        for (var x = 56; x < 140; x += 8)
            Ink(image, x, 64, 3, 12);
        var line = Line(0xFFF8F8F8);

        var patch = OcrLinePatchBuilder.Build(image, 1, 1, line);

        Assert.NotNull(patch);
        var dark = 0;
        for (var row = 0; row < patch!.PixelHeight; row++)
        {
            for (var col = 0; col < patch.PixelWidth; col++)
            {
                if (At(patch, col, row).R < 0x60) dark++;
            }
        }
        Assert.Equal(0, dark); // ни одного «чернильного» пикселя не осталось
    }

    [Fact]
    public void Patch_Covers_Only_The_Band_With_Letters()
    {
        // Рамки распознавания заметно выше самих букв. Если резать по рамке,
        // заплатка съедает соседние строки абзаца.
        var image = Page((_, _) => ((byte)0xFF, (byte)0xFF, (byte)0xFF));
        for (var x = 56; x < 140; x += 8)
            Ink(image, x, 68, 3, 6); // буквы занимают лишь середину рамки
        var line = Line();

        var patch = OcrLinePatchBuilder.Build(image, 1, 1, line);

        Assert.NotNull(patch);
        Assert.True(patch!.HeightPt < line.HeightPt,
            $"заплатка ({patch.HeightPt:0.##}) не уже рамки строки ({line.HeightPt})");
        Assert.True(patch.YPt >= line.YPt,
            "заплатка залезла выше рамки строки — там уже соседняя строка");
    }

    [Fact]
    public void Patch_Keeps_The_Tint_Of_Coloured_Paper()
    {
        // Бежевый бланк: заплатка не должна быть белой.
        var image = Page((_, _) => ((byte)0xC0, (byte)0xDC, (byte)0xF0));
        Ink(image, 60, 64, 40, 10);
        var line = Line(0xFFF0DCC0);

        var patch = OcrLinePatchBuilder.Build(image, 1, 1, line);

        Assert.NotNull(patch);
        var (b, g, r) = At(patch!, patch!.PixelWidth / 2, patch.PixelHeight / 2);
        Assert.Equal(0xF0, r);
        Assert.Equal(0xDC, g);
        Assert.Equal(0xC0, b);
    }

    [Fact]
    public void Blank_Line_Gets_No_Patch()
    {
        // Стирать нечего — значит и класть поверх скана нечего.
        var image = Page((_, _) => ((byte)0xFF, (byte)0xFF, (byte)0xFF));

        Assert.Null(OcrLinePatchBuilder.Build(image, 1, 1, Line()));
    }
}
