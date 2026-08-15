using System.IO.Compression;

namespace NexusPdf.Pdf.Abstractions;

/// <summary>Чем кодировать конкретное изображение.</summary>
public enum ImageCodec
{
    /// <summary>JPEG в цвете: фотографии и цветные сканы.</summary>
    ColorJpeg,

    /// <summary>JPEG в оттенках серого: чёрно-белый скан весит втрое меньше цветного.</summary>
    GrayJpeg,

    /// <summary>Без потерь (Flate): графика, схемы, снимки экрана — JPEG портит их и часто РАЗДУВАЕТ.</summary>
    Lossless,
}

/// <summary>Что за картинка перед нами — от этого зависит и кодек, и качество.</summary>
public enum ImageContent
{
    /// <summary>Фотография или цветной скан: плавные переходы, шум сенсора.</summary>
    Photo,

    /// <summary>Скан в оттенках серого: цветовых плоскостей нет, а платим мы за них.</summary>
    GrayScan,

    /// <summary>Схема, снимок экрана, логотип: плоские заливки и резкие границы.</summary>
    Graphics,
}

/// <summary>Решение о кодировании: чем сжимать и с каким качеством.</summary>
public readonly record struct ImageEncodingChoice(ImageCodec Codec, int Quality)
{
    public bool IsGray => Codec == ImageCodec.GrayJpeg;
    public bool IsLossless => Codec == ImageCodec.Lossless;
}

/// <summary>Разбор картинки: что это и как её кодировать с потерями.</summary>
/// <param name="Content">К какому из трёх случаев относится изображение.</param>
/// <param name="Jpeg">Способ кодирования с потерями, подобранный под содержимое.</param>
public readonly record struct ImageAnalysis(ImageContent Content, ImageEncodingChoice Jpeg)
{
    /// <summary>Стоит ли вообще пробовать вариант без потерь.</summary>
    public bool TryLossless => Content == ImageContent.Graphics;
}

/// <summary>
/// Выбор кодека под конкретное изображение — то, чем современный оптимизатор
/// отличается от «пережать всё в JPEG 75».
///
/// Три наблюдения, ради которых это существует:
/// 1. Скан документа почти всегда серый. Цветной JPEG хранит для него две
///    лишние цветовые плоскости — это примерно троекратная переплата.
/// 2. Схема, снимок экрана и логотип состоят из плоских заливок и резких
///    границ. JPEG даёт на них ореолы И нередко файл БОЛЬШЕ исходного:
///    ровные заливки сжимаются без потерь лучше.
/// 3. Фотография — единственный случай, где цветной JPEG действительно нужен.
///
/// Главный признак «графики» здесь — не количество цветов, а СОВПАДЕНИЕ
/// соседних пикселей. Именно на нём живёт Flate: у схемы соседи в заливке
/// побайтово равны, а у скана каждый пиксель отличается от соседа шумом
/// сенсора, даже когда глазу оба места кажутся белыми. Счёт цветов на этом
/// путается (в сером скане их все 256), а совпадения — нет.
/// </summary>
public static class ImageCodecChooser
{
    /// <summary>Отклонение канала от серого, которое ещё считается серым (шум сканера).</summary>
    private const int GrayTolerance = 12;

    /// <summary>
    /// Доля точно совпадающих соседей, с которой картинка считается графикой.
    /// У схем и снимков экрана она за 0.7, у сканов и фотографий — единицы
    /// процентов, так что порог посередине берёт запас в обе стороны.
    /// </summary>
    private const double FlatNeighbourRatio = 0.45;

    /// <summary>Каждая N-я пара пикселей: полный проход по 40 Мп ничего не уточнит.</summary>
    private const int SampleStep = 7;

    /// <summary>
    /// Насколько вариант без потерь может быть тяжелее JPEG и всё-таки победить.
    /// Для линий и текста JPEG экономит байты ценой ореолов — небольшая
    /// переплата за чистую картинку здесь оправданна, кратная — уже нет.
    /// </summary>
    private const double LosslessSizeAllowance = 3.0;

    /// <summary>
    /// Разбор изображения (BGRA, построчно): что это и как кодировать с потерями.
    /// </summary>
    /// <param name="quality">Базовое качество JPEG из настроек пользователя.</param>
    public static ImageAnalysis Analyze(byte[] bgra, int width, int height, int quality)
    {
        var color = new ImageEncodingChoice(ImageCodec.ColorJpeg, quality);
        if (width < 2 || height < 1 || bgra.Length < 8)
            return new ImageAnalysis(ImageContent.Photo, color);

        var gray = true;
        long flat = 0;
        long pairs = 0;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = (long)y * width * 4;
            for (var x = 0; x + 1 < width; x += SampleStep)
            {
                var o = rowOffset + (long)x * 4;
                if (o + 6 >= bgra.Length) break;
                int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];

                if (gray && (Math.Abs(r - g) > GrayTolerance || Math.Abs(g - b) > GrayTolerance))
                    gray = false;

                // Сосед СПРАВА, а не следующая проба: совпадение имеет смысл
                // только для пикселей, стоящих рядом в потоке.
                if (b == bgra[o + 4] && g == bgra[o + 5] && r == bgra[o + 6])
                    flat++;
                pairs++;
            }
        }

        if (pairs == 0)
            return new ImageAnalysis(ImageContent.Photo, color);

        if ((double)flat / pairs >= FlatNeighbourRatio)
            return new ImageAnalysis(ImageContent.Graphics, color);

        return gray
            // Серому скану качество можно дать выше при том же весе: цветовых
            // плоскостей у него нет, и они не тратят биты.
            ? new ImageAnalysis(ImageContent.GrayScan,
                new ImageEncodingChoice(ImageCodec.GrayJpeg, Math.Min(95, quality + 10)))
            : new ImageAnalysis(ImageContent.Photo, color);
    }

    /// <summary>
    /// Сколько займёт вариант без потерь. Считается честно — тем же Flate и по
    /// тем же 24-битным RGB, которые запишет pdfium, — чтобы решение принималось
    /// по измерению, а не по вере в эвристику.
    /// </summary>
    public static long EstimateLosslessBytes(byte[] bgra, int width, int height)
    {
        var rgb = new byte[(long)width * height * 3];
        for (long i = 0, o = 0; o + 2 < rgb.Length && i + 2 < bgra.Length; i += 4, o += 3)
        {
            rgb[o] = bgra[i + 2];
            rgb[o + 1] = bgra[i + 1];
            rgb[o + 2] = bgra[i];
        }
        var counter = new CountingStream();
        using (var deflate = new ZLibStream(counter, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(rgb, 0, rgb.Length);
        return counter.Length;
    }

    /// <summary>
    /// Побеждает ли вариант без потерь. Не «он меньше», а «он не дороже втрое»:
    /// на линиях и тексте разница в байтах окупается отсутствием ореолов.
    /// </summary>
    public static bool LosslessWins(long losslessBytes, long jpegBytes) =>
        jpegBytes <= 0 || losslessBytes <= jpegBytes * LosslessSizeAllowance;

    /// <summary>
    /// Итог применяется, только если он ДЕЙСТВИТЕЛЬНО меньше исходного.
    /// Пересжатие, увеличивающее файл, — не оптимизация, а порча.
    /// </summary>
    public static bool IsWorthReplacing(long originalBytes, long encodedBytes) =>
        originalBytes <= 0 || encodedBytes < originalBytes * 0.95;

    /// <summary>Считает байты, не храня их: нужен только размер результата.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
