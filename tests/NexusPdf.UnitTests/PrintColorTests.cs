using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Цветовой режим печати. Проверяется то, ради чего он и делается программой,
/// а не драйвером: результат один и тот же на любом принтере и виден в
/// предпросмотре до печати.
/// </summary>
public sealed class PrintColorTests
{
    private const int W = 40;
    private const int H = 30;

    /// <summary>Лист с цветными пятнами и мягким градиентом.</summary>
    private static byte[] Sheet()
    {
        var bgra = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var o = (y * W + x) * 4;
            bgra[o] = (byte)(20 + x * 5 % 200);      // B
            bgra[o + 1] = (byte)(80 + y * 7 % 160);  // G
            bgra[o + 2] = (byte)(200 - x * 3 % 180); // R
            bgra[o + 3] = 255;
        }
        return bgra;
    }

    private static bool IsNeutral(byte[] bgra)
    {
        for (long i = 0; i + 3 < bgra.LongLength; i += 4)
            if (bgra[i] != bgra[i + 1] || bgra[i + 1] != bgra[i + 2]) return false;
        return true;
    }

    [Fact]
    public void Color_Mode_Leaves_The_Sheet_Untouched()
    {
        var sheet = Sheet();
        var before = (byte[])sheet.Clone();

        Assert.False(ColorConversion.Apply(sheet, ColorMode.Color, W));

        Assert.Equal(before, sheet);
    }

    /// <summary>«Решает драйвер» означает именно это: программа не трогает пиксели.</summary>
    [Fact]
    public void Printer_Default_Leaves_The_Sheet_Untouched()
    {
        var sheet = Sheet();
        var before = (byte[])sheet.Clone();

        Assert.False(ColorConversion.Apply(sheet, ColorMode.PrinterDefault, W));

        Assert.Equal(before, sheet);
    }

    [Fact]
    public void Grayscale_Makes_Every_Pixel_Neutral_And_Keeps_Brightness_Order()
    {
        var sheet = Sheet();
        var brightBefore = Luma(sheet, 5, 5);
        var darkBefore = Luma(sheet, 35, 25);

        Assert.True(ColorConversion.Apply(sheet, ColorMode.Grayscale, W));

        Assert.True(IsNeutral(sheet), "После обесцвечивания остались цветные пиксели.");
        // Светлое обязано остаться светлее тёмного: иначе это не оттенки серого,
        // а другая картинка.
        var brightAfter = sheet[(5 * W + 5) * 4];
        var darkAfter = sheet[(25 * W + 35) * 4];
        Assert.Equal(brightBefore > darkBefore, brightAfter > darkAfter);
    }

    private static int Luma(byte[] bgra, int x, int y)
    {
        var o = (y * W + x) * 4;
        return (bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114) / 1000;
    }

    [Fact]
    public void Monochrome_Leaves_Only_Black_And_White()
    {
        var sheet = Sheet();

        Assert.True(ColorConversion.Apply(sheet, ColorMode.Monochrome, W));

        for (long i = 0; i + 3 < sheet.LongLength; i += 4)
        {
            Assert.True(sheet[i] is 0 or 255, $"Полутон {sheet[i]} в монохромном режиме.");
            Assert.Equal(sheet[i], sheet[i + 1]);
            Assert.Equal(sheet[i], sheet[i + 2]);
        }
    }

    /// <summary>
    /// Серый прямоугольник обязан стать СМЕСЬЮ точек, а не сплошным пятном:
    /// именно этим рассеивание ошибки отличается от простого порога, и именно
    /// так монохромный принтер передаёт полутон.
    /// </summary>
    [Fact]
    public void Monochrome_Renders_Grey_As_A_Mix_Of_Dots()
    {
        var sheet = new byte[W * H * 4];
        for (long i = 0; i + 3 < sheet.LongLength; i += 4)
        {
            sheet[i] = sheet[i + 1] = sheet[i + 2] = 128;
            sheet[i + 3] = 255;
        }

        ColorConversion.Apply(sheet, ColorMode.Monochrome, W);

        var black = 0;
        var white = 0;
        for (long i = 0; i + 3 < sheet.LongLength; i += 4)
        {
            if (sheet[i] == 0) black++;
            else white++;
        }
        Assert.True(black > 0 && white > 0,
            $"Полутон вырожден в сплошное поле: чёрных {black}, белых {white}.");
        // Половинный серый — примерно поровну.
        var share = black / (double)(black + white);
        Assert.InRange(share, 0.25, 0.75);
    }

    [Fact]
    public void White_Paper_Stays_White_In_Every_Mode()
    {
        foreach (var mode in new[] { ColorMode.Grayscale, ColorMode.Monochrome })
        {
            var sheet = new byte[W * H * 4];
            Array.Fill(sheet, (byte)255);

            ColorConversion.Apply(sheet, mode, W);

            Assert.All(sheet, b => Assert.Equal(255, b));
        }
    }
}
