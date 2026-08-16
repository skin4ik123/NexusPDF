namespace NexusPdf.Printing;

/// <summary>
/// Перевод готового листа в выбранный цветовой режим.
///
/// Делается ПРОГРАММОЙ, а не драйвером, и намеренно: «оттенки серого» у разных
/// драйверов означают разное, а у части монохромных принтеров цветной лист
/// уходит на устройство как есть и превращается в кашу уже там. Здесь результат
/// один и тот же на любом принтере, и его видно в предпросмотре до печати —
/// именно за этим предпросмотр и нужен.
///
/// Режим <see cref="ColorMode.PrinterDefault"/> сюда не доходит: он означает
/// «не трогать, пусть решает драйвер».
/// </summary>
public static class ColorConversion
{
    /// <summary>
    /// Порог чёрного для монохромного режима. Взят по светлой стороне: текст
    /// со сглаживанием состоит в основном из полутонов, и порог посередине
    /// съедал бы тонкие штрихи вместе с ними.
    /// </summary>
    private const int MonochromeThreshold = 186;

    /// <summary>Переводит BGRA-буфер на месте. Возвращает false, если делать нечего.</summary>
    /// <param name="width">Ширина листа в пикселях — нужна рассеиванию ошибки.</param>
    public static bool Apply(byte[] bgra, ColorMode mode, int width)
    {
        switch (mode)
        {
            case ColorMode.Grayscale:
                ToGrayscale(bgra);
                return true;
            case ColorMode.Monochrome:
                ToMonochrome(bgra, width);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Яркость по стандартным весам восприятия — та же формула, что у растров страниц.</summary>
    public static void ToGrayscale(byte[] bgra)
    {
        for (long i = 0; i + 3 < bgra.LongLength; i += 4)
        {
            var luma = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = luma;
        }
    }

    /// <summary>
    /// Один чёрный без полутонов, с рассеиванием ошибки по Флойду — Стайнбергу.
    ///
    /// Простой порог превращает фотографию в чёрное пятно, а сглаженный текст —
    /// в рваные буквы. Рассеивание сохраняет и то, и другое: полутон передаётся
    /// плотностью точек, а это ровно то, что умеет монохромный принтер.
    /// </summary>
    public static void ToMonochrome(byte[] bgra)
    {
        ToMonochrome(bgra, InferWidth(bgra));
    }

    /// <param name="width">Ширина в пикселях: без неё ошибку некуда переносить построчно.</param>
    public static void ToMonochrome(byte[] bgra, int width)
    {
        if (width <= 0) return;
        var height = (int)(bgra.LongLength / 4 / width);
        if (height <= 0) return;

        // Ошибка копится в отдельном буфере с запасом по краям, чтобы не
        // проверять границы в самом горячем цикле.
        var current = new double[width + 2];
        var next = new double[width + 2];

        for (var y = 0; y < height; y++)
        {
            Array.Clear(next);
            for (var x = 0; x < width; x++)
            {
                var o = ((long)y * width + x) * 4;
                var luma = (bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114) / 1000.0;
                var value = luma + current[x + 1];
                var black = value < MonochromeThreshold;
                var output = black ? 0.0 : 255.0;
                var error = value - output;

                var v = (byte)output;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = v;

                // 7/16 вправо, 3/16 влево-вниз, 5/16 вниз, 1/16 вправо-вниз.
                current[x + 2] += error * 7 / 16;
                next[x] += error * 3 / 16;
                next[x + 1] += error * 5 / 16;
                next[x + 2] += error * 1 / 16;
            }
            (current, next) = (next, current);
        }
    }

    /// <summary>
    /// Ширина неизвестна — считаем буфер одной строкой. Тогда рассеивание
    /// вырождается в порог по строке, но результат всё равно монохромный, а не
    /// молча цветной.
    /// </summary>
    private static int InferWidth(byte[] bgra) => (int)Math.Min(int.MaxValue, bgra.LongLength / 4);
}
