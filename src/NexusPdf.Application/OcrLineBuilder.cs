using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Сборка распознанных слов в строки и подбор цветов для режима
/// РЕДАКТИРУЕМОГО текста. Всё здесь — чистые функции над словами и растром,
/// поэтому качество раскладки проверяется тестами, а не на глаз.
/// </summary>
public static class OcrLineBuilder
{
    /// <summary>
    /// Слово попадает в текущую строку, если его вертикальный центр лежит в
    /// пределах этой доли высоты строки. Строки скана редко идеально ровные,
    /// поэтому допуск заметный.
    /// </summary>
    private const double LineCenterTolerance = 0.6;

    /// <summary>Разрыв больше этой доли высоты строки считается новой колонкой, а не пробелом.</summary>
    private const double ColumnGapFactor = 2.5;

    /// <summary>
    /// Во сколько раз рамка строки может превышать высоту её слов. Рамка
    /// настоящей строки примерно равна кеглю; заметно выше — это склейка
    /// нескольких строк, и заменять её опасно.
    /// </summary>
    private const double MaxBoxToWordHeight = 2.0;

    /// <summary>
    /// Слова в строки. Порядок слов внутри строки — слева направо, строки —
    /// сверху вниз, как их прочитает человек и как их удобно править.
    /// </summary>
    /// <param name="boxesAreWholeLines">
    /// true — движок уже отдал ЦЕЛЫЕ строки (так работает PaddleOCR), и каждая
    /// рамка становится строкой как есть. Собирать их повторно нельзя: соседние
    /// колонки документа склеиваются в одну строку через всю страницу.
    /// </param>
    public static IReadOnlyList<OcrTextLine> BuildLines(
        IReadOnlyList<OcrWordBox> words, bool boxesAreWholeLines = false)
    {
        if (words.Count == 0)
            return Array.Empty<OcrTextLine>();

        if (boxesAreWholeLines)
        {
            return words
                .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.WidthPt > 0 && w.HeightPt > 0)
                .Select(w => new OcrTextLine(w.Text.Trim(), w.XPt, w.YPt, w.WidthPt, w.HeightPt))
                .OrderBy(l => l.YPt)
                .ThenBy(l => l.XPt)
                .ToList();
        }

        var sorted = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.WidthPt > 0 && w.HeightPt > 0)
            .OrderBy(w => w.YPt + w.HeightPt / 2)
            .ThenBy(w => w.XPt)
            .ToList();
        if (sorted.Count == 0)
            return Array.Empty<OcrTextLine>();

        // Проход 1 — только по вертикали: собираем всё, что стоит на одной
        // строке. Разрывы здесь не смотрим: пока строка не собрана целиком,
        // расстояние между крайними словами ещё ничего не значит.
        //
        // Допуск берётся от высоты САМИХ СЛОВ, и слово может продолжить только
        // ПОСЛЕДНЮЮ строку. Раньше допуск считался от рамки уже набранной
        // строки — и это разносило страницу: стоило строке прихватить слово
        // чуть выше или ниже, её рамка вырастала, вместе с ней вырастал допуск,
        // и дальше она засасывала всё подряд. На фотографии паспорта половина
        // разворота становилась одной «строкой» с рамкой в пол-листа, а под неё
        // подкладывалась заплатка соответствующего размера.
        var rows = new List<List<OcrWordBox>>();
        var rowCenter = 0.0;
        foreach (var word in sorted)
        {
            var center = word.YPt + word.HeightPt / 2;
            if (rows.Count > 0)
            {
                var last = rows[^1];
                // Меньшая из двух высот: одно распознанное слово с завышенной
                // рамкой (печать, узор) не должно расширить допуск для всех.
                var reference = Math.Min(word.HeightPt, MedianHeight(last));
                if (Math.Abs(center - rowCenter) <= reference * LineCenterTolerance)
                {
                    last.Add(word);
                    // Скользящий центр вместо центра рамки: строки скана
                    // редко идеально горизонтальны, и накопленный наклон
                    // иначе отрывает конец строки от её начала.
                    rowCenter = last.Average(w => w.YPt + w.HeightPt / 2);
                    continue;
                }
            }
            rows.Add(new List<OcrWordBox> { word });
            rowCenter = center;
        }

        // Проход 2 — разрезаем строку там, где разрыв слишком велик: это
        // соседняя колонка таблицы, а не пробел между словами.
        var lines = new List<List<OcrWordBox>>();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(w => w.XPt).ToList();
            var height = Math.Max(1e-6, ordered.Max(w => w.YPt + w.HeightPt) - ordered.Min(w => w.YPt));
            var current = new List<OcrWordBox> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                var previousRight = current.Max(w => w.XPt + w.WidthPt);
                if (ordered[i].XPt - previousRight > height * ColumnGapFactor)
                {
                    lines.Add(current);
                    current = new List<OcrWordBox>();
                }
                current.Add(ordered[i]);
            }
            lines.Add(current);
        }

        var result = new List<OcrTextLine>(lines.Count);
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(w => w.XPt).ToList();
            var left = ordered.Min(w => w.XPt);
            var top = ordered.Min(w => w.YPt);
            var right = ordered.Max(w => w.XPt + w.WidthPt);
            var bottom = ordered.Max(w => w.YPt + w.HeightPt);

            // Страховка на случай, если группировка всё-таки ошиблась: рамка
            // настоящей строки примерно равна высоте её слов. Рамка сильно
            // выше — это склейка разных строк, и класть под неё заплатку
            // нельзя: она закроет кусок документа. Такую строку просто
            // пропускаем — скан под ней остаётся как был.
            if (bottom - top > MedianHeight(ordered) * MaxBoxToWordHeight)
                continue;

            result.Add(new OcrTextLine(
                string.Join(" ", ordered.Select(w => w.Text)),
                left, top, right - left, bottom - top));
        }

        return result
            .OrderBy(l => l.YPt)
            .ThenBy(l => l.XPt)
            .ToList();
    }

    /// <summary>Медианная высота слов — устойчивая мера «настоящего» кегля строки.</summary>
    private static double MedianHeight(List<OcrWordBox> words)
    {
        var heights = words.Select(w => w.HeightPt).OrderBy(h => h).ToList();
        return Math.Max(1e-6, heights[heights.Count / 2]);
    }

    /// <summary>
    /// Цвет бумаги вокруг строки: медиана пикселей рамки шириной в несколько
    /// точек. Медиана, а не среднее, чтобы буквы, задевающие рамку, не
    /// утягивали цвет в серый.
    /// </summary>
    public static uint SampleBackground(
        RenderedPageImage image, double pxPerPtX, double pxPerPtY, OcrTextLine line)
    {
        var x0 = (int)Math.Floor(line.XPt * pxPerPtX);
        var y0 = (int)Math.Floor(line.YPt * pxPerPtY);
        var x1 = (int)Math.Ceiling((line.XPt + line.WidthPt) * pxPerPtX);
        var y1 = (int)Math.Ceiling((line.YPt + line.HeightPt) * pxPerPtY);
        var margin = Math.Max(2, (int)Math.Round((y1 - y0) * 0.35));

        var samples = new List<(double Luma, byte B, byte G, byte R)>();
        void Sample(int x, int y)
        {
            if (x < 0 || y < 0 || x >= image.PixelWidth || y >= image.PixelHeight) return;
            var o = y * image.Stride + x * 4;
            var b = image.Bgra[o];
            var g = image.Bgra[o + 1];
            var r = image.Bgra[o + 2];
            samples.Add((Luma(r, g, b), b, g, r));
        }

        for (var x = x0 - margin; x <= x1 + margin; x += 2)
        {
            Sample(x, y0 - margin);
            Sample(x, y1 + margin);
        }
        for (var y = y0 - margin; y <= y1 + margin; y += 2)
        {
            Sample(x0 - margin, y);
            Sample(x1 + margin, y);
        }
        if (samples.Count == 0)
            return 0xFFFFFFFF;

        // Берём медиану СВЕТЛОЙ четверти, а не всей рамки: в плотном тексте
        // рамка задевает соседние строки, и обычная медиана давала серую
        // полосу вместо цвета бумаги.
        var bright = samples.OrderByDescending(s => s.Luma)
            .Take(Math.Max(1, samples.Count / 4))
            .ToList();
        var middle = bright[bright.Count / 2];
        return 0xFF000000u
               | ((uint)middle.R << 16)
               | ((uint)middle.G << 8)
               | middle.B;
    }

    /// <summary>
    /// Цвет самих букв: среднее по самым тёмным пикселям внутри строки. Если
    /// текст светлый на тёмном фоне, берётся самый светлый край — иначе
    /// заменённая строка стала бы невидимой.
    /// </summary>
    public static uint SampleInk(
        RenderedPageImage image, double pxPerPtX, double pxPerPtY, OcrTextLine line, uint background)
    {
        var x0 = Math.Max(0, (int)Math.Floor(line.XPt * pxPerPtX));
        var y0 = Math.Max(0, (int)Math.Floor(line.YPt * pxPerPtY));
        var x1 = Math.Min(image.PixelWidth - 1, (int)Math.Ceiling((line.XPt + line.WidthPt) * pxPerPtX));
        var y1 = Math.Min(image.PixelHeight - 1, (int)Math.Ceiling((line.YPt + line.HeightPt) * pxPerPtY));
        if (x1 <= x0 || y1 <= y0)
            return 0xFF000000;

        var backgroundLuma = Luma((byte)(background >> 16), (byte)(background >> 8), (byte)background);
        var darkBackground = backgroundLuma < 128;

        var pixels = new List<(double Luma, byte B, byte G, byte R)>();
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var o = y * image.Stride + x * 4;
                var b = image.Bgra[o];
                var g = image.Bgra[o + 1];
                var r = image.Bgra[o + 2];
                pixels.Add((Luma(r, g, b), b, g, r));
            }
        }
        if (pixels.Count == 0)
            return darkBackground ? 0xFFFFFFFF : 0xFF000000;

        // Берём только САМОЕ ядро штриха — два процента пикселей, дальше всего
        // ушедших от фона. Раньше бралась десятая часть, и в неё попадали
        // сглаженные края букв: усреднение с ними давало блёклый серый, и
        // заменённая строка на скане выходила еле видной.
        var ordered = darkBackground
            ? pixels.OrderByDescending(p => p.Luma).ToList()
            : pixels.OrderBy(p => p.Luma).ToList();
        var take = Math.Max(1, ordered.Count / 50);
        var slice = ordered.Take(take).ToList();
        return 0xFF000000u
               | ((uint)(byte)slice.Average(p => (double)p.R) << 16)
               | ((uint)(byte)slice.Average(p => (double)p.G) << 8)
               | (byte)slice.Average(p => (double)p.B);
    }

    private static double Luma(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    private static byte Median(List<byte> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
