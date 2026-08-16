using NexusPdf.Imaging;

namespace NexusPdf.UnitTests;

/// <summary>
/// Выравнивание и чистка сканов. Проверяется на страницах с ИЗВЕСТНЫМ наклоном
/// и известным мусором: только так видно, что найден именно тот угол и стёрта
/// именно пыль, а не буквы.
/// </summary>
public sealed class ScanCleanupTests
{
    private const int W = 800;
    private const int H = 1000;

    /// <summary>
    /// Белая страница с «текстом»: 20 горизонтальных строк, накренённых ПО
    /// часовой стрелке на заданный угол (экранные координаты, ось Y вниз).
    /// Детектор для такой страницы обязан вернуть угол с обратным знаком —
    /// именно его надо применить, чтобы страницу выпрямить.
    /// </summary>
    private static byte[] Page(double clockwiseDegrees, bool text = true)
    {
        var bgra = new byte[W * H * 4];
        Array.Fill(bgra, (byte)255);
        if (!text) return bgra;

        var radians = clockwiseDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = W / 2.0;
        var cy = H / 2.0;

        // Строки рисуются в системе координат «ровной» страницы и переносятся
        // на холст поворотом — так угол задан точно, без интерполяции.
        for (var line = 0; line < 20; line++)
        {
            var lineY = 80 + line * 42;
            for (var thickness = 0; thickness < 9; thickness++)
            for (var x = 100; x < W - 100; x++)
            {
                // Пробелы между «словами», иначе строка — сплошная линия.
                if ((x / 40) % 5 == 4) continue;
                var y = lineY + thickness;
                var dx = x - cx;
                var dy = y - cy;
                var px = (int)Math.Round(cx + dx * cos - dy * sin);
                var py = (int)Math.Round(cy + dx * sin + dy * cos);
                if (px < 0 || py < 0 || px >= W || py >= H) continue;
                var o = ((long)py * W + px) * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = 20;
            }
        }
        return bgra;
    }

    private static void Dot(byte[] bgra, int x, int y, int radius, byte value = 10)
    {
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var px = x + dx;
            var py = y + dy;
            if (px < 0 || py < 0 || px >= W || py >= H) continue;
            var o = ((long)py * W + px) * 4;
            bgra[o] = bgra[o + 1] = bgra[o + 2] = value;
        }
    }

    private static int DarkCount(byte[] bgra)
    {
        var n = 0;
        for (long i = 0; i + 2 < bgra.Length; i += 4)
            if (bgra[i] < 128) n++;
        return n;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-2.3)]
    [InlineData(4.0)]
    [InlineData(-5.5)]
    public void Skew_Is_Found_Within_A_Quarter_Of_A_Degree(double clockwise)
    {
        var estimate = SkewDetector.Detect(Page(clockwise), W, H);
        Assert.True(Math.Abs(estimate.AngleDegrees + clockwise) < 0.25,
            $"Крен {clockwise}° по часовой должен дать {-clockwise:0.00}°, " +
            $"а определён как {estimate.AngleDegrees:0.00}°.");
    }

    [Fact]
    public void A_Straight_Page_Is_Left_Alone()
    {
        var estimate = SkewDetector.Detect(Page(0.05), W, H);
        Assert.False(estimate.IsWorthFixing,
            "Наклон в сотые доли градуса не виден глазу — крутить страницу незачем.");
    }

    [Fact]
    public void A_Blank_Page_Does_Not_Invent_An_Angle()
    {
        var estimate = SkewDetector.Detect(Page(0, text: false), W, H);
        Assert.False(estimate.IsWorthFixing);
        Assert.Equal(0, estimate.AngleDegrees);
    }

    [Fact]
    public void Rotation_Straightens_The_Page()
    {
        var crooked = Page(3.0);
        var found = SkewDetector.Detect(crooked, W, H);
        Assert.True(found.IsWorthFixing);

        var fixedPage = ScanCleanup.Rotate(crooked, W, H, found.AngleDegrees);
        var after = SkewDetector.Detect(fixedPage, W, H);
        Assert.True(Math.Abs(after.AngleDegrees) < 0.25,
            $"После поворота осталось {after.AngleDegrees:0.00}°.");
    }

    [Fact]
    public void Rotation_Keeps_The_Page_Size_And_Paper_Colour()
    {
        var rotated = ScanCleanup.Rotate(Page(2.0), W, H, 2.0);
        Assert.Equal(W * H * 4, rotated.Length);
        // Угол холста после поворота обязан быть бумагой, а не чёрной дырой.
        Assert.True(rotated[0] > 200 && rotated[1] > 200 && rotated[2] > 200);
    }

    [Fact]
    public void Speckles_Are_Removed_And_Text_Is_Not()
    {
        var page = Page(0);
        var textPixels = DarkCount(page);
        for (var i = 0; i < 40; i++)
            Dot(page, 30 + i * 18, 20, 0);      // одиночные точки пыли
        for (var i = 0; i < 10; i++)
            Dot(page, 60 + i * 60, 980, 1);     // пятнышки 3x3
        var withDirt = DarkCount(page);
        Assert.True(withDirt > textPixels);

        var removed = ScanCleanup.Despeckle(page, W, H, maxSpeckleArea: 12);

        Assert.True(removed >= 45, $"Убрано пятен: {removed}, а насорено 50.");
        var afterText = DarkCount(page);
        Assert.True(afterText >= textPixels * 0.99,
            $"Текст пострадал: было {textPixels}, стало {afterText}.");
    }

    [Fact]
    public void Despeckle_Does_Not_Eat_Thin_Lines()
    {
        // Тонкая линия в 1 пиксель — это подчёркивание или рамка таблицы.
        var page = Page(0, text: false);
        for (var x = 50; x < 750; x++)
        {
            var o = ((long)500 * W + x) * 4;
            page[o] = page[o + 1] = page[o + 2] = 15;
        }
        var before = DarkCount(page);
        ScanCleanup.Despeckle(page, W, H, maxSpeckleArea: 12);
        Assert.Equal(before, DarkCount(page));
    }

    [Fact]
    public void Grey_Paper_Becomes_White_And_Text_Stays_Dark()
    {
        // Серая бумага с тенью: слева темнее, справа светлее.
        var page = Page(0);
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var o = ((long)y * W + x) * 4;
            if (page[o] > 128)
            {
                var shade = (byte)(150 + 60.0 * x / W);
                page[o] = page[o + 1] = page[o + 2] = shade;
            }
        }
        var paperBefore = page[((long)10 * W + 10) * 4];
        Assert.True(paperBefore < 200);

        ScanCleanup.LevelBackground(page, W, H);

        var paperAfter = page[((long)10 * W + 10) * 4];
        Assert.True(paperAfter > 245, $"Бумага осталась серой: {paperAfter}.");
        // Текст обязан остаться заметно темнее бумаги.
        var textPixel = page[((long)84 * W + 400) * 4];
        Assert.True(textPixel < 100, $"Текст выцвел до {textPixel}.");
    }

    /// <summary>
    /// Засвет от лампы — светлое пятно посреди листа. Именно на нём ломается
    /// «одна яркость на всю страницу»: пятно ярче бумаги, и общий уровень
    /// вытягивает бумагу вокруг него в серое. После чистки лист обязан стать
    /// РОВНЫМ: разброс бумаги по всей странице — единицы, а не десятки.
    /// </summary>
    [Fact]
    public void Glare_And_Shadow_Level_Out_To_An_Even_Sheet()
    {
        var page = Page(0);
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var o = ((long)y * W + x) * 4;
            if (page[o] < 128) continue;                 // текст не трогаем
            // Тень слева, засвет круглым пятном справа сверху.
            double v = 150 + 55.0 * x / W;
            var dx = x - 600.0;
            var dy = y - 300.0;
            var r = Math.Sqrt(dx * dx + dy * dy);
            if (r < 180) v += 55 * (1 - r / 180);
            page[o] = page[o + 1] = page[o + 2] = (byte)Math.Clamp(v, 0, 255);
        }

        ScanCleanup.LevelBackground(page, W, H);

        // Замеряем бумагу в шести далёких друг от друга местах, включая центр
        // засвета и самый тёмный угол.
        var probes = new (int X, int Y)[] { (20, 20), (770, 20), (20, 970), (770, 970), (600, 300), (400, 640) };
        foreach (var (x, y) in probes)
        {
            var v = page[((long)y * W + x) * 4];
            Assert.True(v >= 250,
                $"Бумага в ({x},{y}) осталась {v} — лист не стал ровно белым.");
        }
    }

    /// <summary>
    /// Жёлтая бумага. Общий серый уровень её только осветляет — оттенок
    /// остаётся, и «чистого» листа не выходит. Уровень по каждому каналу
    /// убирает и желтизну.
    /// </summary>
    [Fact]
    public void Yellow_Paper_Loses_Its_Tint()
    {
        var page = Page(0);
        for (long i = 0; i + 3 < page.Length; i += 4)
        {
            if (page[i] < 128) continue;
            page[i] = 176;      // B — жёлтая бумага синего отражает меньше
            page[i + 1] = 214;  // G
            page[i + 2] = 232;  // R
        }

        ScanCleanup.LevelBackground(page, W, H);

        var b = page[((long)10 * W + 10) * 4];
        var g = page[((long)10 * W + 10) * 4 + 1];
        var r = page[((long)10 * W + 10) * 4 + 2];
        Assert.True(Math.Abs(b - g) <= 2 && Math.Abs(g - r) <= 2,
            $"Оттенок остался: B={b} G={g} R={r}.");
        Assert.True(b >= 250, $"Бумага не добелена: {b}.");
    }

    /// <summary>Со снятием оттенка выключенным цвета остаются как были.</summary>
    [Fact]
    public void Tint_Removal_Can_Be_Switched_Off()
    {
        var page = Page(0, text: false);
        for (long i = 0; i + 3 < page.Length; i += 4)
        {
            page[i] = 176; page[i + 1] = 214; page[i + 2] = 232;
        }
        ScanCleanup.LevelBackground(page, W, H,
            new ScanCleanup.BackgroundOptions(Strength: 0, NeutralizeTint: false));

        var b = page[0];
        var r = page[2];
        Assert.True(r - b > 20, $"Оттенок снят, хотя не просили: B={b} R={r}.");
    }

    /// <summary>Тёмная кайма сканера стирается, а содержимое страницы — нет.</summary>
    [Fact]
    public void Scanner_Edge_Is_Trimmed_But_Content_Survives()
    {
        var page = Page(0);
        for (var y = 0; y < 14; y++)
        for (var x = 0; x < W; x++)
        {
            var o = ((long)y * W + x) * 4;
            page[o] = page[o + 1] = page[o + 2] = 12;
        }
        var textBefore = DarkCount(page) - 14 * W;

        var cleared = ScanCleanup.TrimDarkEdges(page, W, H);

        Assert.True(cleared >= 14, $"Очищено полос: {cleared}, а кайма в 14 строк.");
        // Кайма заливается цветом бумаги — здесь бумага белая.
        Assert.Equal(255, page[0]);
        Assert.Equal(textBefore, DarkCount(page));
    }

    /// <summary>
    /// Кайма плюс выравнивание фона — вместе, как их и применяет программа.
    ///
    /// Раньше здесь вылезала виньетка: полоса заливалась чистым белым, оценка
    /// бумаги в угловых плитках подскакивала, и настоящая бумага рядом уходила
    /// в серое. Проверяется именно угол — там это и было видно.
    /// </summary>
    [Fact]
    public void Trimming_The_Edge_Does_Not_Darken_The_Corner_Afterwards()
    {
        var page = Page(0);
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var o = ((long)y * W + x) * 4;
            if (page[o] < 128) continue;
            page[o] = page[o + 1] = page[o + 2] = (byte)(155 + 45.0 * x / W);
        }
        // Кайма: 12 строк сверху и 10 столбцов слева.
        for (var y = 0; y < 12; y++)
        for (var x = 0; x < W; x++)
        {
            var o = ((long)y * W + x) * 4;
            page[o] = page[o + 1] = page[o + 2] = 12;
        }
        for (var y = 0; y < H; y++)
        for (var x = 0; x < 10; x++)
        {
            var o = ((long)y * W + x) * 4;
            page[o] = page[o + 1] = page[o + 2] = 12;
        }

        Assert.True(ScanCleanup.TrimDarkEdges(page, W, H) >= 20);
        ScanCleanup.LevelBackground(page, W, H);

        foreach (var (x, y) in new[] { (2, 2), (20, 20), (60, 60), (5, 500), (400, 4) })
        {
            var v = page[((long)y * W + x) * 4];
            Assert.True(v >= 250, $"У края в ({x},{y}) осталось {v} — вылезла виньетка.");
        }
    }

    /// <summary>Без каймы обрезать нечего — страница обязана остаться нетронутой.</summary>
    [Fact]
    public void A_Clean_Page_Keeps_All_Its_Edges()
    {
        var page = Page(0);
        var before = DarkCount(page);
        Assert.Equal(0, ScanCleanup.TrimDarkEdges(page, W, H));
        Assert.Equal(before, DarkCount(page));
    }

    [Fact]
    public void Levelling_Does_Not_Bleach_A_Photograph()
    {
        // Тёмная фотография во всю страницу: «выровнять фон» не должно
        // превращать её в белый лист.
        var photo = new byte[W * H * 4];
        for (long i = 0; i + 3 < photo.Length; i += 4)
        {
            photo[i] = photo[i + 1] = photo[i + 2] = 60;
            photo[i + 3] = 255;
        }
        ScanCleanup.LevelBackground(photo, W, H);
        Assert.True(photo[0] < 200, $"Фотография выбелена до {photo[0]}.");
    }
}
