namespace NexusPdf.Imaging;

/// <summary>
/// Полутоновая копия страницы: один байт на пиксель. Все разборы скана
/// (наклон, шум, фон) смотрят только на яркость, а держать ради этого четыре
/// байта на пиксель — впустую тратить и память, и время обхода.
/// </summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[(long)width * height];
    }

    public GrayImage(byte[] pixels, int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public byte this[int x, int y]
    {
        get => Pixels[(long)y * Width + x];
        set => Pixels[(long)y * Width + x] = value;
    }

    /// <summary>BGRA-растр → яркость по стандартным весам восприятия.</summary>
    public static GrayImage FromBgra(byte[] bgra, int width, int height)
    {
        var gray = new GrayImage(width, height);
        var count = (long)width * height;
        for (long i = 0; i < count; i++)
        {
            var o = i * 4;
            if (o + 2 >= bgra.Length) break;
            gray.Pixels[i] = (byte)((bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114) / 1000);
        }
        return gray;
    }

    /// <summary>
    /// Уменьшенная копия усреднением. Наклон ищется именно по ней: на полной
    /// странице 300 DPI перебор углов стоил бы секунды, а угол от уменьшения
    /// не меняется.
    /// </summary>
    public GrayImage Downscale(int maxSide)
    {
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(Width, Height) / (double)maxSide));
        if (step == 1) return this;

        var w = Math.Max(1, Width / step);
        var h = Math.Max(1, Height / step);
        var small = new GrayImage(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sum = 0;
            var n = 0;
            for (var dy = 0; dy < step; dy++)
            {
                var sy = y * step + dy;
                if (sy >= Height) break;
                for (var dx = 0; dx < step; dx++)
                {
                    var sx = x * step + dx;
                    if (sx >= Width) break;
                    sum += this[sx, sy];
                    n++;
                }
            }
            small[x, y] = (byte)(n > 0 ? sum / n : 255);
        }
        return small;
    }

    /// <summary>
    /// Порог Оцу: делит гистограмму так, чтобы «бумага» и «краска» разошлись
    /// как можно дальше. Фиксированный порог 128 на жёлтой бумаге или бледном
    /// скане ошибается целиком.
    /// </summary>
    public int OtsuThreshold()
    {
        Span<int> histogram = stackalloc int[256];
        foreach (var p in Pixels)
            histogram[p]++;

        long total = Pixels.LongLength;
        if (total == 0) return 128;

        long sumAll = 0;
        for (var i = 0; i < 256; i++)
            sumAll += (long)i * histogram[i];

        long sumBackground = 0;
        long countBackground = 0;
        double best = -1;
        var threshold = 128;

        for (var t = 0; t < 256; t++)
        {
            countBackground += histogram[t];
            if (countBackground == 0) continue;
            var countForeground = total - countBackground;
            if (countForeground == 0) break;

            sumBackground += (long)t * histogram[t];
            var meanBackground = (double)sumBackground / countBackground;
            var meanForeground = (double)(sumAll - sumBackground) / countForeground;
            var between = (double)countBackground * countForeground *
                          (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }
}
