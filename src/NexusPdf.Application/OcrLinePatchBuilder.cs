using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Готовит заплатку под строку распознанного текста: тот же кусок скана, но
/// БЕЗ БУКВ.
///
/// Зачем не заливка одним цветом: у настоящего документа под строкой не пустая
/// бумага. Защитная сетка паспорта, линовка бланка, тень от сгиба, оттенок
/// фотографии — любой ровный прямоугольник поверх этого виден как дыра, и
/// страница после замены текста выглядит хуже, чем была. Поэтому заплатка
/// строится из самого растра: пиксели чернил заменяются тем, что стоит над и
/// под ними в том же столбце, а всё остальное остаётся как есть. Узор проходит
/// сквозь строку, буквы исчезают.
///
/// По вертикали заплатка обрезается по фактической полосе чернил, а не по
/// рамке распознавания: рамки заметно выше букв, и заплатка по рамке съедала
/// соседние строки абзаца.
///
/// Готовая заплатка ужимается примерно до 120 точек на дюйм: фон и так мягкий,
/// а полноразмерный кусок растра на каждую строку — это десятки мегабайт на
/// страницу в истории отмены и в самом файле.
/// </summary>
public static class OcrLinePatchBuilder
{
    /// <summary>Разрешение готовой заплатки. Ниже — узор фона начинает мылиться.</summary>
    private const double PatchDpi = 120.0;

    /// <summary>
    /// Насколько темнее (светлее) бумаги должен быть пиксель, чтобы счесть его
    /// чернилами. Слишком низкий порог съедает сам узор фона, слишком
    /// высокий — оставляет от букв серые тени.
    /// </summary>
    private const double InkThresholdShare = 0.3;

    /// <summary>Доля от самой «чернильной» строки пикселей, ниже которой строка считается пустой.</summary>
    private const double BandRowShare = 0.12;

    /// <summary>
    /// Строит заплатку для строки. null — если мерить нечего (нет контраста
    /// или чернил); тогда вызывающий остаётся на заливке одним цветом.
    /// </summary>
    /// <param name="image">Растр страницы.</param>
    /// <param name="pxPerPtX">Пикселей на пункт по горизонтали.</param>
    /// <param name="pxPerPtY">Пикселей на пункт по вертикали.</param>
    /// <param name="line">Строка в отображаемых пунктах; её цвета — опорные.</param>
    public static OcrLinePatch? Build(
        RenderedPageImage image, double pxPerPtX, double pxPerPtY, OcrTextLine line)
    {
        var pad = line.PadPt;
        var x0 = Clamp((int)Math.Floor((line.XPt - pad) * pxPerPtX), 0, image.PixelWidth);
        var x1 = Clamp((int)Math.Ceiling((line.XPt + line.WidthPt + pad) * pxPerPtX), 0, image.PixelWidth);
        var y0 = Clamp((int)Math.Floor((line.YPt - pad) * pxPerPtY), 0, image.PixelHeight);
        var y1 = Clamp((int)Math.Ceiling((line.YPt + line.HeightPt + pad) * pxPerPtY), 0, image.PixelHeight);
        var w = x1 - x0;
        var h = y1 - y0;
        if (w < 2 || h < 2)
            return null;

        var paperLuma = Luma(line.BackgroundArgb);
        var inkLuma = Luma(line.InkArgb);
        var contrast = Math.Abs(paperLuma - inkLuma);
        if (contrast < 12)
            return null; // контраста нет — трогать этот кусок скана незачем
        var inkIsDarker = inkLuma < paperLuma;
        var threshold = paperLuma + (inkIsDarker ? -1 : 1) * contrast * InkThresholdShare;

        var isInk = new bool[w * h];
        var rowInk = new int[h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var src = (y0 + y) * image.Stride + (x0 + x) * 4;
                var luma = Luma(image.Bgra[src + 2], image.Bgra[src + 1], image.Bgra[src]);
                if (inkIsDarker ? luma < threshold : luma > threshold)
                {
                    isInk[y * w + x] = true;
                    rowInk[y]++;
                }
            }
        }

        // Края букв сглажены сканером: ровно по порогу остаётся серая кайма,
        // и на месте строки виден призрак прежнего текста. Расширяем маску на
        // толщину этой каймы — она пропорциональна разрешению растра.
        var grow = Math.Max(1, (int)Math.Round(Math.Max(pxPerPtX, pxPerPtY) / 2.0));

        // Рамка строки внутри полосы с запасом — по ней выбирается «своя»
        // полоса букв среди попавших в кадр соседей.
        var boxTop = Clamp((int)Math.Round(line.YPt * pxPerPtY) - y0, 0, h - 1);
        var boxBottom = Clamp((int)Math.Round((line.YPt + line.HeightPt) * pxPerPtY) - y0, 0, h - 1);

        var band = FindBand(rowInk, grow, boxTop, boxBottom);
        if (band == null)
            return null; // чернил не нашлось — стирать нечего
        var (bandTop, bandBottom) = band.Value;

        // Дальше работаем ТОЛЬКО с полосой букв: всё, что выше и ниже, —
        // соседние строки абзаца, и трогать их нельзя.
        var bh = bandBottom - bandTop + 1;
        var strip = new byte[w * bh * 4];
        var mask = new bool[w * bh];
        for (var y = 0; y < bh; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var src = (y0 + bandTop + y) * image.Stride + (x0 + x) * 4;
                var dst = (y * w + x) * 4;
                strip[dst] = image.Bgra[src];
                strip[dst + 1] = image.Bgra[src + 1];
                strip[dst + 2] = image.Bgra[src + 2];
                strip[dst + 3] = 0xFF;
                mask[y * w + x] = isInk[(bandTop + y) * w + x];
            }
        }

        Dilate(mask, w, bh, grow);
        Inpaint(strip, mask, w, bh, line.BackgroundArgb);

        var stepX = Math.Max(1, (int)Math.Round(pxPerPtX * 72.0 / PatchDpi));
        var stepY = Math.Max(1, (int)Math.Round(pxPerPtY * 72.0 / PatchDpi));
        var (pixels, outW, outH) = Downscale(strip, w, bh, stepX, stepY);

        return new OcrLinePatch(
            pixels, outW, outH,
            x0 / pxPerPtX, (y0 + bandTop) / pxPerPtY,
            w / pxPerPtX, bh / pxPerPtY);
    }

    /// <summary>
    /// Полоса строк, занятая буквами ЭТОЙ строки.
    ///
    /// В кадр вместе со строкой попадают куски соседних — и брать самую
    /// «чернильную» полосу нельзя: рамки распознавания смещены относительно
    /// букв, и заплатка уезжала на строку ниже, стирая чужой текст и оставляя
    /// свой. Поэтому все полосы кадра перебираются и берётся та, что БОЛЬШЕ
    /// ВСЕГО перекрывается с рамкой строки. null — чернил нет вовсе.
    /// </summary>
    private static (int Top, int Bottom)? FindBand(int[] rowInk, int margin, int boxTop, int boxBottom)
    {
        var peak = 0;
        foreach (var count in rowInk)
            if (count > peak) peak = count;
        if (peak == 0)
            return null;

        var threshold = Math.Max(1, (int)(peak * BandRowShare));
        var gap = Math.Max(1, margin);

        var best = (Top: -1, Bottom: -1, Overlap: -1, Ink: 0L);
        var start = -1;
        var empty = 0;
        long ink = 0;
        for (var y = 0; y <= rowInk.Length; y++)
        {
            var filled = y < rowInk.Length && rowInk[y] >= threshold;
            if (filled)
            {
                if (start < 0) { start = y; ink = 0; }
                empty = 0;
                ink += rowInk[y];
                continue;
            }
            if (start < 0)
                continue;
            // Короткие пропуски внутри полосы — это промежутки между
            // надстрочными и подстрочными элементами, а не конец строки.
            if (y < rowInk.Length && ++empty <= gap)
                continue;

            var end = y - empty;
            var overlap = Math.Min(end, boxBottom) - Math.Max(start, boxTop) + 1;
            if (overlap > best.Overlap || (overlap == best.Overlap && ink > best.Ink))
                best = (start, end, overlap, ink);
            start = -1;
            empty = 0;
        }

        if (best.Top < 0 || best.Overlap <= 0)
            return null;

        // Запас на расширение маски — иначе кайма буквы упрётся в край полосы.
        return (Math.Max(0, best.Top - margin), Math.Min(rowInk.Length - 1, best.Bottom + margin));
    }

    /// <summary>
    /// Расширяет маску чернил на <paramref name="radius"/> пикселей.
    /// Раздельно по осям (сначала по строкам, потом по столбцам) — результат
    /// тот же, что у квадратного окна, а работы линейно, а не квадратично.
    /// </summary>
    private static void Dilate(bool[] mask, int w, int h, int radius)
    {
        var pass = new bool[mask.Length];

        for (var y = 0; y < h; y++)
        {
            var run = -1; // сколько пикселей назад встретились чернила
            for (var x = 0; x < w; x++)
            {
                if (mask[y * w + x]) run = 0; else if (run >= 0) run++;
                if (run >= 0 && run <= radius) pass[y * w + x] = true;
            }
            run = -1;
            for (var x = w - 1; x >= 0; x--)
            {
                if (mask[y * w + x]) run = 0; else if (run >= 0) run++;
                if (run >= 0 && run <= radius) pass[y * w + x] = true;
            }
        }

        for (var x = 0; x < w; x++)
        {
            var run = -1;
            for (var y = 0; y < h; y++)
            {
                if (pass[y * w + x]) run = 0; else if (run >= 0) run++;
                if (run >= 0 && run <= radius) mask[y * w + x] = true;
            }
            run = -1;
            for (var y = h - 1; y >= 0; y--)
            {
                if (pass[y * w + x]) run = 0; else if (run >= 0) run++;
                if (run >= 0 && run <= radius) mask[y * w + x] = true;
            }
        }
    }

    /// <summary>
    /// Затягивает пиксели чернил тем, что стоит над и под ними в том же
    /// столбце. Два прохода по столбцу вместо поиска соседа для каждого
    /// пикселя: строк на странице много, и квадратичный алгоритм здесь
    /// заметен на глаз.
    /// </summary>
    private static void Inpaint(byte[] strip, bool[] isInk, int w, int h, uint paperArgb)
    {
        var aboveColor = new int[h * 3];
        var aboveDist = new int[h];
        var belowColor = new int[h * 3];
        var belowDist = new int[h];
        var paperB = (byte)paperArgb;
        var paperG = (byte)(paperArgb >> 8);
        var paperR = (byte)(paperArgb >> 16);

        for (var x = 0; x < w; x++)
        {
            var haveAbove = false;
            int ab = 0, ag = 0, ar = 0, aDistance = 0;
            for (var y = 0; y < h; y++)
            {
                if (!isInk[y * w + x])
                {
                    var o = (y * w + x) * 4;
                    ab = strip[o]; ag = strip[o + 1]; ar = strip[o + 2];
                    aDistance = 0;
                    haveAbove = true;
                }
                else if (haveAbove) aDistance++;

                aboveDist[y] = haveAbove ? aDistance : int.MaxValue;
                aboveColor[y * 3] = ab; aboveColor[y * 3 + 1] = ag; aboveColor[y * 3 + 2] = ar;
            }

            var haveBelow = false;
            int bb = 0, bg = 0, br = 0, bDistance = 0;
            for (var y = h - 1; y >= 0; y--)
            {
                if (!isInk[y * w + x])
                {
                    var o = (y * w + x) * 4;
                    bb = strip[o]; bg = strip[o + 1]; br = strip[o + 2];
                    bDistance = 0;
                    haveBelow = true;
                }
                else if (haveBelow) bDistance++;

                belowDist[y] = haveBelow ? bDistance : int.MaxValue;
                belowColor[y * 3] = bb; belowColor[y * 3 + 1] = bg; belowColor[y * 3 + 2] = br;
            }

            for (var y = 0; y < h; y++)
            {
                if (!isInk[y * w + x])
                    continue;
                var o = (y * w + x) * 4;
                var da = aboveDist[y];
                var db = belowDist[y];
                if (da == int.MaxValue && db == int.MaxValue)
                {
                    // Столбец целиком «чернильный»: жирная вертикальная линия
                    // или рамка. Ставим цвет бумаги — врать меньше нечем.
                    strip[o] = paperB; strip[o + 1] = paperG; strip[o + 2] = paperR;
                    continue;
                }
                if (da == int.MaxValue) { da = db; db = 0; }
                else if (db == int.MaxValue) { db = da; da = 0; }

                // Ближе к верхнему соседу — больше его цвета: так удалённая
                // буква не даёт ступеньки на границе с бумагой.
                var k = da + db == 0 ? 0.5 : (double)da / (da + db);
                strip[o] = Mix(aboveColor[y * 3], belowColor[y * 3], k);
                strip[o + 1] = Mix(aboveColor[y * 3 + 1], belowColor[y * 3 + 1], k);
                strip[o + 2] = Mix(aboveColor[y * 3 + 2], belowColor[y * 3 + 2], k);
            }
        }
    }

    private static (byte[] Pixels, int Width, int Height) Downscale(
        byte[] strip, int w, int h, int stepX, int stepY)
    {
        if (stepX == 1 && stepY == 1)
            return (strip, w, h);

        var outW = Math.Max(1, w / stepX);
        var outH = Math.Max(1, h / stepY);
        var pixels = new byte[outW * outH * 4];
        for (var oy = 0; oy < outH; oy++)
        {
            var sy0 = oy * h / outH;
            var sy1 = Math.Max(sy0 + 1, (oy + 1) * h / outH);
            for (var ox = 0; ox < outW; ox++)
            {
                var sx0 = ox * w / outW;
                var sx1 = Math.Max(sx0 + 1, (ox + 1) * w / outW);
                long sb = 0, sg = 0, sr = 0;
                var count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var o = (sy * w + sx) * 4;
                        sb += strip[o]; sg += strip[o + 1]; sr += strip[o + 2];
                        count++;
                    }
                }
                var d = (oy * outW + ox) * 4;
                pixels[d] = (byte)(sb / count);
                pixels[d + 1] = (byte)(sg / count);
                pixels[d + 2] = (byte)(sr / count);
                pixels[d + 3] = 0xFF;
            }
        }
        return (pixels, outW, outH);
    }

    private static double Luma(uint argb) =>
        Luma((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static double Luma(byte r, byte g, byte b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    private static byte Mix(int from, int to, double k) =>
        (byte)Math.Clamp(Math.Round(from + (to - from) * k), 0, 255);

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
