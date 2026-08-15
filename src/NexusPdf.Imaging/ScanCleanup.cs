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

    /// <summary>Сколько плиток по большей стороне при оценке фона.</summary>
    private const int BackgroundTiles = 12;

    /// <summary>
    /// Выравнивание фона: серая или желтоватая бумага и тень от сгиба
    /// становятся ровно белыми, а текст остаётся тёмным.
    ///
    /// Страница делится на плитки, в каждой берётся яркость «светлой» части
    /// (90-й процентиль) — это и есть местный цвет бумаги. Значения между
    /// плитками сглаживаются, после чего каждый пиксель делится на свой фон.
    /// Так убирается неравномерность подсветки, которую простым порогом или
    /// общей яркостью не победить.
    /// </summary>
    public static void LevelBackground(byte[] bgra, int width, int height)
    {
        if (width < BackgroundTiles * 2 || height < BackgroundTiles * 2) return;

        var gray = GrayImage.FromBgra(bgra, width, height);
        var tilesX = BackgroundTiles;
        var tilesY = Math.Max(2, (int)Math.Round(BackgroundTiles * (double)height / width));
        var tileW = (double)width / tilesX;
        var tileH = (double)height / tilesY;
        var background = new double[tilesX * tilesY];

        var samples = new List<byte>();
        for (var ty = 0; ty < tilesY; ty++)
        for (var tx = 0; tx < tilesX; tx++)
        {
            samples.Clear();
            var x0 = (int)(tx * tileW);
            var x1 = (int)Math.Min(width, (tx + 1) * tileW);
            var y0 = (int)(ty * tileH);
            var y1 = (int)Math.Min(height, (ty + 1) * tileH);
            // Каждый третий пиксель: точность процентиля от этого не страдает.
            for (var y = y0; y < y1; y += 3)
            for (var x = x0; x < x1; x += 3)
                samples.Add(gray[x, y]);

            if (samples.Count == 0)
            {
                background[ty * tilesX + tx] = 255;
                continue;
            }
            samples.Sort();
            var p90 = samples[(int)(samples.Count * 0.9)];
            // Совсем тёмная плитка (например, фотография) фоном не считается:
            // делить на неё — значит выбелить картинку.
            background[ty * tilesX + tx] = Math.Max((int)p90, 96);
        }

        for (var y = 0; y < height; y++)
        {
            var gy = Math.Clamp(y / tileH - 0.5, 0, tilesY - 1.0);
            var y0 = (int)gy;
            var y1 = Math.Min(y0 + 1, tilesY - 1);
            var fy = gy - y0;

            for (var x = 0; x < width; x++)
            {
                var gx = Math.Clamp(x / tileW - 0.5, 0, tilesX - 1.0);
                var x0 = (int)gx;
                var x1 = Math.Min(x0 + 1, tilesX - 1);
                var fx = gx - x0;

                var top = background[y0 * tilesX + x0] +
                          (background[y0 * tilesX + x1] - background[y0 * tilesX + x0]) * fx;
                var bottom = background[y1 * tilesX + x0] +
                             (background[y1 * tilesX + x1] - background[y1 * tilesX + x0]) * fx;
                var level = top + (bottom - top) * fy;
                if (level < 1) continue;

                var gain = 255.0 / level;
                var o = ((long)y * width + x) * 4;
                for (var c = 0; c < 3; c++)
                    bgra[o + c] = (byte)Math.Clamp(bgra[o + c] * gain, 0, 255);
            }
        }
    }
}
