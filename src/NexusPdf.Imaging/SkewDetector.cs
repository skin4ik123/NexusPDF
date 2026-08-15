namespace NexusPdf.Imaging;

/// <param name="AngleDegrees">Наклон страницы: положительный — против часовой стрелки.</param>
/// <param name="Confidence">
/// Насколько находка убедительна: 0 — «строк не видно, это не текст»,
/// 1 — строки читаются идеально. Ниже <see cref="SkewDetector.MinConfidence"/>
/// страницу лучше не трогать вовсе.
/// </param>
public readonly record struct SkewEstimate(double AngleDegrees, double Confidence)
{
    /// <summary>Стоит ли вообще поворачивать: и уверенность есть, и угол заметен.</summary>
    public bool IsWorthFixing => Confidence >= SkewDetector.MinConfidence &&
                                 Math.Abs(AngleDegrees) >= SkewDetector.MinAngleDegrees;
}

/// <summary>
/// Поиск наклона отсканированной страницы по профилю строк.
///
/// Идея простая и надёжнее «искать линии»: если страницу мысленно повернуть на
/// правильный угол, тёмные пиксели соберутся в узкие плотные полосы — строки
/// текста, — и разброс плотности по строкам станет максимальным. При неверном
/// угле строки размазываются друг по другу, и разброс падает. Перебираем углы,
/// берём максимум.
///
/// Поэтому метод работает и на таблицах, и на чертежах с рамкой, и на тексте
/// любого языка: ему не нужно распознавать буквы.
/// </summary>
public static class SkewDetector
{
    /// <summary>Дальше искать смысла нет: страницу с таким наклоном сканируют разве что боком.</summary>
    public const double MaxAngleDegrees = 8.0;

    /// <summary>Наклон меньше этого не виден глазу, а поворот стоит качества.</summary>
    public const double MinAngleDegrees = 0.12;

    /// <summary>Ниже этой уверенности считаем, что строк на странице нет.</summary>
    public const double MinConfidence = 0.05;

    /// <summary>Ширина уменьшенной копии, по которой идёт перебор.</summary>
    private const int WorkingSide = 900;

    public static SkewEstimate Detect(byte[] bgra, int width, int height) =>
        Detect(GrayImage.FromBgra(bgra, width, height));

    public static SkewEstimate Detect(GrayImage source)
    {
        var image = source.Downscale(WorkingSide);
        if (image.Width < 16 || image.Height < 16)
            return new SkewEstimate(0, 0);

        // Чёрно-белая маска: дальше считаются только тёмные пиксели.
        var threshold = image.OtsuThreshold();
        var dark = new byte[image.Pixels.Length];
        long darkCount = 0;
        for (var i = 0; i < dark.Length; i++)
        {
            if (image.Pixels[i] <= threshold)
            {
                dark[i] = 1;
                darkCount++;
            }
        }

        // Пустая или, наоборот, сплошь залитая страница: строк там нет.
        var fill = (double)darkCount / dark.Length;
        if (fill < 0.002 || fill > 0.6)
            return new SkewEstimate(0, 0);

        // Грубый проход с шагом 0.5°, затем точный вокруг найденного.
        var (coarse, _) = Search(dark, image.Width, image.Height, -MaxAngleDegrees, MaxAngleDegrees, 0.5);
        var (angle, score) = Search(dark, image.Width, image.Height,
            coarse - 0.5, coarse + 0.5, 0.05);

        // Уверенность — во сколько раз найденный угол лучше «как есть».
        var flat = Score(dark, image.Width, image.Height, 0);
        var confidence = flat <= 0 ? 0 : Math.Clamp((score - flat) / flat, 0, 1);
        return new SkewEstimate(angle, confidence);
    }

    private static (double Angle, double Score) Search(
        byte[] dark, int width, int height, double from, double to, double step)
    {
        var bestAngle = 0.0;
        var bestScore = double.NegativeInfinity;
        for (var angle = from; angle <= to + 1e-9; angle += step)
        {
            if (Math.Abs(angle) > MaxAngleDegrees + 1e-9) continue;
            var score = Score(dark, width, height, angle);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }
        return (bestAngle, bestScore);
    }

    /// <summary>
    /// Насколько «полосатой» становится страница при данном угле. Считается
    /// сдвигом столбцов (страницу не поворачиваем по-настоящему — это дорого
    /// и здесь не нужно), затем берётся сумма квадратов разностей соседних
    /// строк профиля: у собранных строк перепады резкие, у размазанных — нет.
    /// </summary>
    private static double Score(byte[] dark, int width, int height, double angleDegrees)
    {
        var tan = Math.Tan(angleDegrees * Math.PI / 180.0);
        var centerX = width / 2.0;
        var profile = new int[height];

        for (var x = 0; x < width; x++)
        {
            // Положительный угол = против часовой: чтобы «выпрямить», левый край
            // опускается, правый поднимается.
            var shift = (int)Math.Round((x - centerX) * tan);
            for (var y = 0; y < height; y++)
            {
                if (dark[(long)y * width + x] == 0) continue;
                var target = y + shift;
                if ((uint)target >= (uint)height) continue;
                profile[target]++;
            }
        }

        double score = 0;
        for (var y = 1; y < height; y++)
        {
            double d = profile[y] - profile[y - 1];
            score += d * d;
        }
        return score;
    }
}
