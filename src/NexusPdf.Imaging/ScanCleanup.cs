namespace NexusPdf.Imaging;

/// <summary>
/// Чистка отсканированной страницы: поворот, удаление мусора, выравнивание
/// фона. Всё работает с BGRA-растром страницы — тем же, что отдаёт отрисовка.
/// </summary>
public static class ScanCleanup
{
    /// <summary>
    /// Поворот вокруг центра с билинейной интерполяцией. Размер холста не
    /// меняется: страница обязана остаться того же формата, поэтому углы
    /// заполняются цветом бумаги, а не чёрным.
    /// </summary>
    /// <param name="angleDegrees">
    /// Угол ИСПРАВЛЕНИЯ: если <see cref="SkewDetector"/> нашёл наклон +1.5°,
    /// сюда передаётся он же — картинка повернётся обратно.
    /// </param>
    public static byte[] Rotate(byte[] bgra, int width, int height, double angleDegrees, byte paper = 255)
    {
        var result = new byte[bgra.Length];
        var radians = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = (width - 1) / 2.0;
        var cy = (height - 1) / 2.0;

        for (var y = 0; y < height; y++)
        {
            var dy = y - cy;
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                // Обратное отображение: для каждого пикселя результата ищем,
                // откуда он пришёл. Прямое оставляло бы дыры.
                var sx = cx + dx * cos + dy * sin;
                var sy = cy - dx * sin + dy * cos;
                var o = ((long)y * width + x) * 4;

                if (sx < 0 || sy < 0 || sx > width - 1 || sy > height - 1)
                {
                    result[o] = result[o + 1] = result[o + 2] = paper;
                    result[o + 3] = 255;
                    continue;
                }

                var x0 = (int)sx;
                var y0 = (int)sy;
                var x1 = Math.Min(x0 + 1, width - 1);
                var y1 = Math.Min(y0 + 1, height - 1);
                var fx = sx - x0;
                var fy = sy - y0;

                for (var c = 0; c < 4; c++)
                {
                    var p00 = bgra[((long)y0 * width + x0) * 4 + c];
                    var p10 = bgra[((long)y0 * width + x1) * 4 + c];
                    var p01 = bgra[((long)y1 * width + x0) * 4 + c];
                    var p11 = bgra[((long)y1 * width + x1) * 4 + c];
                    var top = p00 + (p10 - p00) * fx;
                    var bottom = p01 + (p11 - p01) * fx;
                    result[o + c] = (byte)Math.Clamp(top + (bottom - top) * fy, 0, 255);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Удаление точечного мусора сканера. Не размытием: медианный фильтр
    /// заодно съедает тонкие штрихи и точки над «ё». Ищутся СВЯЗНЫЕ пятна
    /// тёмных пикселей, и стираются только те, что мельче порога, — буква или
    /// линия связна и крупна, а пылинка нет.
    /// </summary>
    /// <param name="maxSpeckleArea">Наибольший размер пятна в пикселях, которое считается мусором.</param>
    /// <returns>Сколько пятен удалено.</returns>
    public static int Despeckle(byte[] bgra, int width, int height, int maxSpeckleArea = 12)
    {
        if (maxSpeckleArea <= 0 || width < 3 || height < 3) return 0;

        var gray = GrayImage.FromBgra(bgra, width, height);
        var threshold = gray.OtsuThreshold();
        var visited = new bool[gray.Pixels.Length];
        var component = new List<int>(maxSpeckleArea + 8);
        var stack = new Stack<int>();
        var removed = 0;

        for (var start = 0; start < gray.Pixels.Length; start++)
        {
            if (visited[start] || gray.Pixels[start] > threshold) continue;

            component.Clear();
            stack.Clear();
            stack.Push(start);
            visited[start] = true;
            var tooBig = false;

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                component.Add(index);
                if (component.Count > maxSpeckleArea)
                {
                    // Пятно уже крупнее мусора — дальше обходить незачем, но
                    // разметить его надо целиком, иначе соседние клетки снова
                    // начнут обход с середины той же буквы.
                    tooBig = true;
                }

                var x = index % width;
                var y = index / width;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var n = ny * width + nx;
                    if (visited[n] || gray.Pixels[n] > threshold) continue;
                    visited[n] = true;
                    stack.Push(n);
                }
            }

            if (tooBig) continue;

            foreach (var index in component)
            {
                var o = (long)index * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = 255;
                bgra[o + 3] = 255;
            }
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Насколько сильно чистить фон и что именно с ним делать.
    /// </summary>
    /// <param name="Strength">
    /// 0…100. Определяет, какую «почти белую» яркость считать чистой бумагой:
    /// чем сильнее, тем шире полоса, добеливаемая до ровного 255. На 0
    /// выполняется только выравнивание освещённости, без добеливания.
    /// </param>
    /// <param name="NeutralizeTint">
    /// Считать цвет бумаги по каждому каналу отдельно. Желтизна старой бумаги
    /// и синева от лампы — это РАЗНЫЙ уровень фона в R, G и B; общий серый
    /// уровень их только осветляет, а оттенок оставляет.
    /// </param>
    public sealed record BackgroundOptions(int Strength = 60, bool NeutralizeTint = true);

    /// <summary>Сколько пикселей приходится на плитку оценки фона.</summary>
    private const int TargetTilePixels = 80;

    /// <summary>Ниже этой яркости «бумаги» на странице нет — чистить нечего.</summary>
    private const int NoPaperBelow = 100;

    /// <summary>
    /// Выравнивание фона: серая или желтоватая бумага, тень от сгиба и засветы
    /// от лампы становятся ровно белыми, а текст остаётся тёмным.
    ///
    /// Страница делится на плитки, в каждой берётся яркость «светлой» части
    /// (90-й процентиль) — это и есть местный цвет бумаги. Плитки мелкие: пятно
    /// засвета размером с ладонь крупная сетка усредняет вместе с нормальной
    /// бумагой, и пятно остаётся видно. Уровень плитки при этом не отпускается
    /// далеко от общестраничного — иначе плитка, целиком попавшая внутрь
    /// фотографии или чёрной заливки, «выбелила» бы её до бумаги.
    ///
    /// Дальше каждый пиксель делится на свой фон (ПОКАНАЛЬНО, если снимается
    /// оттенок), и результат растягивается так, чтобы всё почти-белое стало
    /// ровно белым. Без этого шага деление даёт бумагу 240…252 — на экране это
    /// по-прежнему грязно-серый лист, а не чистый.
    /// </summary>
    public static void LevelBackground(byte[] bgra, int width, int height,
        BackgroundOptions? options = null)
    {
        var settings = options ?? new BackgroundOptions();
        var tilesX = TileCount(width);
        var tilesY = TileCount(height);
        if (width < tilesX * 2 || height < tilesY * 2) return;

        var channels = settings.NeutralizeTint ? 3 : 1;
        var tileW = (double)width / tilesX;
        var tileH = (double)height / tilesY;

        // [канал][плитка]; при общем уровне канал один и берётся яркость.
        var background = new double[channels][];
        var globals = new double[channels];
        for (var c = 0; c < channels; c++)
            background[c] = new double[tilesX * tilesY];

        var histogram = new int[256];
        var globalHistogram = new int[256];

        for (var c = 0; c < channels; c++)
        {
            Array.Clear(globalHistogram);
            for (var ty = 0; ty < tilesY; ty++)
            for (var tx = 0; tx < tilesX; tx++)
            {
                Array.Clear(histogram);
                var x0 = (int)(tx * tileW);
                var x1 = (int)Math.Min(width, (tx + 1) * tileW);
                var y0 = (int)(ty * tileH);
                var y1 = (int)Math.Min(height, (ty + 1) * tileH);
                var count = 0;

                // Каждый третий пиксель: точность процентиля от этого не страдает.
                for (var y = y0; y < y1; y += 3)
                for (var x = x0; x < x1; x += 3)
                {
                    var o = ((long)y * width + x) * 4;
                    if (o + 2 >= bgra.Length) continue;
                    var v = settings.NeutralizeTint
                        ? bgra[o + c]
                        : (bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114) / 1000;
                    histogram[v]++;
                    globalHistogram[v]++;
                    count++;
                }

                background[c][ty * tilesX + tx] = count == 0 ? 255 : Percentile(histogram, count, 0.90);
            }
            globals[c] = Percentile(globalHistogram, Math.Max(1, Sum(globalHistogram)), 0.90);
        }

        // Страница без светлой бумаги — это фотография или чёрная заливка во
        // весь лист. Ей выравнивание фона только навредит.
        for (var c = 0; c < channels; c++)
            if (globals[c] < NoPaperBelow) return;

        for (var c = 0; c < channels; c++)
        {
            var low = globals[c] * 0.55;
            var high = globals[c] * 1.35;
            var tile = background[c];
            for (var i = 0; i < tile.Length; i++)
                tile[i] = Math.Clamp(tile[i], Math.Max(low, 64), high);
            background[c] = Smooth(tile, tilesX, tilesY);
        }

        // Белая точка: всё ярче неё — чистая бумага. Растяжка «подтягивает» её
        // к 255, поэтому лёгкий шум сканера вокруг уровня бумаги пропадает
        // совсем, а не остаётся сеточкой светло-серых точек.
        var strength = Math.Clamp(settings.Strength, 0, 100);
        var white = 255.0 - strength * 0.28;
        var black = strength * 0.10;
        var span = Math.Max(1.0, white - black);

        for (var y = 0; y < height; y++)
        {
            var gy = Math.Clamp(y / tileH - 0.5, 0, tilesY - 1.0);
            var ry0 = (int)gy;
            var ry1 = Math.Min(ry0 + 1, tilesY - 1);
            var fy = gy - ry0;

            for (var x = 0; x < width; x++)
            {
                var gx = Math.Clamp(x / tileW - 0.5, 0, tilesX - 1.0);
                var rx0 = (int)gx;
                var rx1 = Math.Min(rx0 + 1, tilesX - 1);
                var fx = gx - rx0;
                var o = ((long)y * width + x) * 4;
                if (o + 2 >= bgra.Length) continue;

                for (var c = 0; c < 3; c++)
                {
                    var tile = background[channels == 1 ? 0 : c];
                    var top = tile[ry0 * tilesX + rx0] +
                              (tile[ry0 * tilesX + rx1] - tile[ry0 * tilesX + rx0]) * fx;
                    var bottom = tile[ry1 * tilesX + rx0] +
                                 (tile[ry1 * tilesX + rx1] - tile[ry1 * tilesX + rx0]) * fx;
                    var level = top + (bottom - top) * fy;
                    if (level < 1) continue;

                    var levelled = bgra[o + c] * 255.0 / level;
                    bgra[o + c] = (byte)Math.Clamp((levelled - black) * 255.0 / span, 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// Тёмная кайма по краю листа: щель между крышкой сканера и бумагой, или
    /// чёрный фон вокруг снятой на камеру страницы. Стирается только узкая
    /// полоса и только пока строка почти сплошь тёмная — как только начинается
    /// содержимое, обход прекращается.
    ///
    /// Полоса заливается ЦВЕТОМ БУМАГИ этой страницы, а не белым. Разница не
    /// косметическая: выравнивание фона идёт следом и считает уровень бумаги по
    /// плиткам, а белая полоса поднимает уровень в угловых плитках так, что
    /// настоящая бумага рядом уходит в серое — вместо каймы появляется
    /// виньетка. С цветом бумаги угловая плитка ничем не отличается от прочих,
    /// и добеливание доводит до белого всё разом.
    /// </summary>
    /// <returns>Сколько строк и столбцов очищено.</returns>
    public static int TrimDarkEdges(byte[] bgra, int width, int height,
        double maxMarginFraction = 0.05)
    {
        if (width < 16 || height < 16) return 0;

        var gray = GrayImage.FromBgra(bgra, width, height);
        var dark = Math.Max(48, gray.OtsuThreshold() / 2);
        var maxRows = Math.Max(1, (int)(height * maxMarginFraction));
        var maxCols = Math.Max(1, (int)(width * maxMarginFraction));

        bool RowIsEdge(int y)
        {
            var n = 0;
            for (var x = 0; x < width; x++)
                if (gray[x, y] <= dark) n++;
            return n > width * 0.6;
        }
        bool ColumnIsEdge(int x)
        {
            var n = 0;
            for (var y = 0; y < height; y++)
                if (gray[x, y] <= dark) n++;
            return n > height * 0.6;
        }

        // Сначала ТОЛЬКО меряем: заливать по ходу нельзя — цвет бумаги берётся
        // из-за каймы, и надо знать, где она кончается со всех сторон.
        var top = 0;
        while (top < maxRows && RowIsEdge(top)) top++;
        var bottom = 0;
        while (bottom < maxRows && RowIsEdge(height - 1 - bottom)) bottom++;
        var left = 0;
        while (left < maxCols && ColumnIsEdge(left)) left++;
        var right = 0;
        while (right < maxCols && ColumnIsEdge(width - 1 - right)) right++;
        if (top + bottom + left + right == 0) return 0;

        var innerX0 = Math.Min(left, width - 1);
        var innerX1 = Math.Max(innerX0, width - 1 - right);
        var innerY0 = Math.Min(top, height - 1);
        var innerY1 = Math.Max(innerY0, height - 1 - bottom);

        // Цвет берётся МЕСТНЫЙ — самая светлая точка рядом, уже за каймой.
        // Общий на всю страницу цвет ломал бы освещение: на листе с тенью слева
        // край получил бы яркость правого края, и после выравнивания фона там
        // вылезала бы серая виньетка.
        for (var y = 0; y < top; y++)
        for (var x = 0; x < width; x++)
            FillPixel(bgra, ((long)y * width + x) * 4,
                Paper(bgra, width, Math.Clamp(x, innerX0, innerX1), innerY0, 0, 1, innerY1));
        for (var y = height - bottom; y < height; y++)
        for (var x = 0; x < width; x++)
            FillPixel(bgra, ((long)y * width + x) * 4,
                Paper(bgra, width, Math.Clamp(x, innerX0, innerX1), innerY1, 0, -1, innerY0));
        for (var x = 0; x < left; x++)
        for (var y = 0; y < height; y++)
            FillPixel(bgra, ((long)y * width + x) * 4,
                Paper(bgra, width, innerX0, Math.Clamp(y, innerY0, innerY1), 1, 0, innerX1));
        for (var x = width - right; x < width; x++)
        for (var y = 0; y < height; y++)
            FillPixel(bgra, ((long)y * width + x) * 4,
                Paper(bgra, width, innerX1, Math.Clamp(y, innerY0, innerY1), -1, 0, innerX0));

        return top + bottom + left + right;
    }

    /// <summary>Насколько далеко за каймой искать бумагу.</summary>
    private const int PaperProbe = 24;

    /// <summary>
    /// Цвет бумаги рядом с точкой: самый светлый пиксель на коротком отрезке
    /// вглубь листа. Самый светлый, а не средний, — потому что на отрезок может
    /// попасть буква, и усреднение с ней дало бы серую полосу.
    /// </summary>
    private static (byte B, byte G, byte R) Paper(
        byte[] bgra, int width, int x, int y, int stepX, int stepY, int limit)
    {
        (byte B, byte G, byte R) best = (255, 255, 255);
        var bestLuma = -1;
        for (var i = 0; i < PaperProbe; i++)
        {
            var px = x + stepX * i;
            var py = y + stepY * i;
            if (stepX > 0 && px > limit) break;
            if (stepX < 0 && px < limit) break;
            if (stepY > 0 && py > limit) break;
            if (stepY < 0 && py < limit) break;
            if (px < 0 || py < 0) break;

            var o = ((long)py * width + px) * 4;
            if (o + 2 >= bgra.Length) break;
            var luma = (bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114) / 1000;
            if (luma <= bestLuma) continue;
            bestLuma = luma;
            best = (bgra[o], bgra[o + 1], bgra[o + 2]);
        }
        return best;
    }

    private static void FillPixel(byte[] bgra, long offset, (byte B, byte G, byte R) colour)
    {
        if (offset + 3 >= bgra.Length) return;
        bgra[offset] = colour.B;
        bgra[offset + 1] = colour.G;
        bgra[offset + 2] = colour.R;
        bgra[offset + 3] = 255;
    }

    /// <summary>Сетка плиток по стороне: цель — плитка около 80 пикселей.</summary>
    private static int TileCount(int side) =>
        Math.Clamp((int)Math.Round(side / (double)TargetTilePixels), 4, 48);

    private static int Sum(int[] histogram)
    {
        var total = 0;
        foreach (var n in histogram) total += n;
        return total;
    }

    /// <summary>Процентиль по 256-корзинной гистограмме — без сортировки выборки.</summary>
    private static double Percentile(int[] histogram, int count, double fraction)
    {
        var target = (long)(count * fraction);
        long seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen > target) return v;
        }
        return 255;
    }

    /// <summary>
    /// Сглаживание сетки фона 3×3. Без него мелкие плитки дают на ровной
    /// бумаге еле заметную шахматную клетку — на печати она видна.
    /// </summary>
    private static double[] Smooth(double[] tiles, int tilesX, int tilesY)
    {
        var result = new double[tiles.Length];
        for (var y = 0; y < tilesY; y++)
        for (var x = 0; x < tilesX; x++)
        {
            var sum = 0.0;
            var n = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= tilesX || ny >= tilesY) continue;
                sum += tiles[ny * tilesX + nx];
                n++;
            }
            result[y * tilesX + x] = sum / n;
        }
        return result;
    }
}
