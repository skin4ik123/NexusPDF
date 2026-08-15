using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Сборка распознанных слов в строки. От неё зависит, будет ли редактируемый
/// текст удобным: правится строка целиком, а не каждое слово отдельно.
/// </summary>
public sealed class OcrLineBuilderTests
{
    private static OcrWordBox W(string text, double x, double y, double w = 40, double h = 12) =>
        new(text, x, y, w, h);

    [Fact]
    public void Words_On_One_Baseline_Become_A_Single_Line()
    {
        var lines = OcrLineBuilder.BuildLines(new[]
        {
            W("Мама", 100, 200),
            W("мыла", 145, 201),   //小 наклон строки не должен её разрывать
            W("раму", 190, 199),
        });

        var line = Assert.Single(lines);
        Assert.Equal("Мама мыла раму", line.Text);
        Assert.Equal(100, line.XPt, 1);
        Assert.Equal(130, line.WidthPt, 1); // от 100 до 230
    }

    [Fact]
    public void Different_Baselines_Stay_Separate_Lines()
    {
        var lines = OcrLineBuilder.BuildLines(new[]
        {
            W("Первая", 100, 100),
            W("строка", 145, 100),
            W("Вторая", 100, 140),
            W("строка", 145, 140),
        });

        Assert.Equal(2, lines.Count);
        Assert.Equal("Первая строка", lines[0].Text);
        Assert.Equal("Вторая строка", lines[1].Text);
        Assert.True(lines[0].YPt < lines[1].YPt, "строки идут сверху вниз");
    }

    [Fact]
    public void Wide_Gap_Is_A_Second_Column_Not_A_Space()
    {
        // Таблица: два столбца на одной высоте, между ними далеко.
        var lines = OcrLineBuilder.BuildLines(new[]
        {
            W("Наименование", 50, 300, 90, 12),
            W("1200", 400, 300, 30, 12),
        });

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.Text.Contains("Наименование 1200"));
    }

    [Fact]
    public void Words_Are_Ordered_Left_To_Right_Regardless_Of_Input_Order()
    {
        var lines = OcrLineBuilder.BuildLines(new[]
        {
            W("третье", 190, 200),
            W("первое", 100, 200),
            W("второе", 145, 200),
        });

        Assert.Equal("первое второе третье", Assert.Single(lines).Text);
    }

    [Fact]
    public void Empty_And_Degenerate_Words_Are_Ignored()
    {
        var lines = OcrLineBuilder.BuildLines(new[]
        {
            W("", 100, 100),
            W("   ", 150, 100),
            new OcrWordBox("нулевая", 200, 100, 0, 12),
            W("годное", 250, 100),
        });

        Assert.Equal("годное", Assert.Single(lines).Text);
    }

    [Fact]
    public void No_Words_Gives_No_Lines()
    {
        Assert.Empty(OcrLineBuilder.BuildLines(Array.Empty<OcrWordBox>()));
    }

    // ----- Подбор цветов -----

    private static RenderedPageImage SolidPage(int w, int h, byte b, byte g, byte r)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 0xFF;
        }
        return new RenderedPageImage(w, h, stride, pixels);
    }

    [Fact]
    public void Background_Is_Taken_From_Paper_Around_The_Line()
    {
        // Бежевая бумага: заплатка обязана быть бежевой, а не белой.
        var image = SolidPage(200, 200, 0xD0, 0xE0, 0xF0);
        var line = new OcrTextLine("текст", 50, 50, 60, 12);

        var background = OcrLineBuilder.SampleBackground(image, 1, 1, line);

        Assert.Equal(0xF0u, (background >> 16) & 0xFF);
        Assert.Equal(0xE0u, (background >> 8) & 0xFF);
        Assert.Equal(0xD0u, background & 0xFF);
    }

    [Fact]
    public void Ink_On_Light_Paper_Is_Dark()
    {
        var image = SolidPage(200, 200, 0xFF, 0xFF, 0xFF);
        // Тёмная полоса «букв» внутри строки.
        for (var y = 52; y < 60; y++)
        {
            for (var x = 52; x < 100; x++)
            {
                var o = y * image.Stride + x * 4;
                image.Bgra[o] = 0x20; image.Bgra[o + 1] = 0x20; image.Bgra[o + 2] = 0x20;
            }
        }
        var line = new OcrTextLine("текст", 50, 50, 60, 12);

        var background = OcrLineBuilder.SampleBackground(image, 1, 1, line);
        var ink = OcrLineBuilder.SampleInk(image, 1, 1, line, background);

        Assert.True((ink & 0xFF) < 0x60, "чернила должны быть тёмными на светлой бумаге");
    }

    [Fact]
    public void Ink_On_Dark_Paper_Is_Light()
    {
        // Светлый текст на тёмном фоне: если взять «самое тёмное», строка
        // станет невидимой — проверяем обратный случай.
        var image = SolidPage(200, 200, 0x20, 0x20, 0x20);
        for (var y = 52; y < 60; y++)
        {
            for (var x = 52; x < 100; x++)
            {
                var o = y * image.Stride + x * 4;
                image.Bgra[o] = 0xF0; image.Bgra[o + 1] = 0xF0; image.Bgra[o + 2] = 0xF0;
            }
        }
        var line = new OcrTextLine("текст", 50, 50, 60, 12);

        var background = OcrLineBuilder.SampleBackground(image, 1, 1, line);
        var ink = OcrLineBuilder.SampleInk(image, 1, 1, line, background);

        Assert.True((ink & 0xFF) > 0xA0, "на тёмной бумаге чернила должны быть светлыми");
    }
}
