using System.IO.Compression;

namespace NexusPdf.Export;

/// <summary>Готовая к вставке картинка: байты и тип содержимого.</summary>
public sealed record EncodedImage(byte[] Bytes, string ContentType);

/// <summary>
/// Кодирование растра для вставки в документ. Приложение подставляет сюда
/// кодеки Windows (JPEG для фотографий), а без него работает встроенный PNG:
/// экспорт не должен зависеть от того, из какой оболочки его вызвали.
/// </summary>
public delegate EncodedImage? EncodeImage(byte[] bgra, int width, int height);

/// <summary>
/// Минимальный кодировщик PNG: без потерь и без внешних зависимостей.
///
/// Нужен потому, что библиотека экспорта не привязана к Windows — ей нельзя
/// звать кодеки WPF, а отдавать документ без картинок нечестно.
/// </summary>
public static class PortablePng
{
    public static EncodedImage Encode(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Пустой растр.");
        var stride = width * 4;
        if (bgra.Length < (long)stride * height)
            throw new ArgumentException("Растр короче своих размеров.", nameof(bgra));

        // Прозрачность в документе не нужна и лишь утяжеляет файл: пиксель
        // складывается с белым, как он и выглядел на странице PDF.
        var rawSize = (long)height * (width * 3 + 1);
        if (rawSize > int.MaxValue) throw new ArgumentException("Слишком большой растр.");
        var raw = new byte[rawSize];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0; // фильтр строки: без фильтра
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = bgra[i + 3];
                if (a == 255)
                {
                    raw[at++] = bgra[i + 2];
                    raw[at++] = bgra[i + 1];
                    raw[at++] = bgra[i];
                }
                else
                {
                    raw[at++] = (byte)((bgra[i + 2] * a + 255 * (255 - a)) / 255);
                    raw[at++] = (byte)((bgra[i + 1] * a + 255 * (255 - a)) / 255);
                    raw[at++] = (byte)((bgra[i] * a + 255 * (255 - a)) / 255);
                }
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;   // бит на канал
        header[9] = 2;   // цвет без альфы
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());

        return new EncodedImage(png.ToArray(), "image/png");
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        var name = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);

        var crc = Crc32(name, data);
        var tail = new byte[4];
        WriteBigEndian(tail, 0, crc);
        stream.Write(tail);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in first) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in second) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
