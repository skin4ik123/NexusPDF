using System.Buffers.Binary;
using System.Text;

namespace NexusPdf.TestSupport;

/// <summary>Пути к свободным шрифтам Liberation, скопированным рядом с тестами.</summary>
public static class TestFontPaths
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "TestFonts");

    public static string Sans(bool bold = false, bool italic = false) =>
        Path.Combine(Directory, "LiberationSans-" + (bold, italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        } + ".ttf");
}

/// <summary>
/// Параметры синтетического шрифта. Advances — ширины по кодовым точкам в
/// единицах шрифта; null — печатные ASCII шириной 500.
/// </summary>
public sealed record TinyFontSpec(
    string Family,
    string Subfamily = "Regular",
    int UnitsPerEm = 1000,
    short Ascender = 800,
    short Descender = -200,
    short LineGap = 0,
    ushort FsType = 0,
    ushort WeightClass = 400,
    bool Italic = false,
    IReadOnlyDictionary<int, ushort>? Advances = null);

/// <summary>
/// Минимальный, но корректный TrueType: таблицы head, hhea, maxp, OS/2, hmtx,
/// cmap (форматы 4 и 12), name, post, loca, glyf. Глифы пустые — для вёрстки
/// важны ширины, а не контуры.
/// </summary>
public static class TinyFontBuilder
{
    private const ushort NotdefAdvance = 500;

    public static byte[] Build(TinyFontSpec spec)
    {
        var advances = spec.Advances ?? Enumerable.Range(0x20, 0x7F - 0x20).ToDictionary(c => c, _ => (ushort)500);
        var codes = advances.Keys.OrderBy(c => c).ToArray();
        var glyphCount = codes.Length + 1;
        var bold = spec.WeightClass >= 600;

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = Os2(spec, bold, codes),
            ["cmap"] = Cmap(codes),
            ["glyf"] = new byte[4],
            ["head"] = Head(spec, bold),
            ["hhea"] = Hhea(spec, glyphCount),
            ["hmtx"] = Hmtx(codes.Select(c => advances[c]).Prepend(NotdefAdvance).ToArray()),
            ["loca"] = new byte[(glyphCount + 1) * 2],
            ["maxp"] = Maxp(glyphCount),
            ["name"] = Name(spec),
            ["post"] = Post(spec),
        };
        return Assemble(tables);
    }

    public static byte[] BuildCollection(params byte[][] faces)
    {
        var header = 12 + 4 * faces.Length;
        var output = new MemoryStream();
        var head = new byte[header];
        "ttcf"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), (uint)faces.Length);
        output.Write(head);
        for (var i = 0; i < faces.Length; i++)
        {
            var start = (uint)output.Length;
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12 + 4 * i), start);
            // Смещения таблиц в коллекции считаются от начала всего файла.
            var face = (byte[])faces[i].Clone();
            var count = BinaryPrimitives.ReadUInt16BigEndian(face.AsSpan(4));
            for (var t = 0; t < count; t++)
            {
                var at = face.AsSpan(12 + 16 * t + 8);
                BinaryPrimitives.WriteUInt32BigEndian(at, BinaryPrimitives.ReadUInt32BigEndian(at) + start);
            }
            output.Write(face);
        }
        var result = output.ToArray();
        head.CopyTo(result, 0);
        return result;
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var directory = 12 + 16 * count;
        var output = new byte[directory + tables.Values.Sum(t => Pad(t.Length))];
        BinaryPrimitives.WriteUInt32BigEndian(output, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)count);
        var power = 1;
        while (power * 2 <= count) power *= 2;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)Math.Log2(power));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)(count * 16 - power * 16));

        var offset = directory;
        var index = 0;
        foreach (var (tag, data) in tables)
        {
            var record = output.AsSpan(12 + 16 * index++);
            Encoding.ASCII.GetBytes(tag).CopyTo(record);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            data.CopyTo(output, offset);
            offset += Pad(data.Length);
        }
        return output;
    }

    private static int Pad(int length) => (length + 3) & ~3;

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        var padded = new byte[Pad(data.Length)];
        data.CopyTo(padded, 0);
        for (var i = 0; i < padded.Length; i += 4)
            sum += BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(i));
        return sum;
    }

    private static byte[] Head(TinyFontSpec spec, bool bold)
    {
        var b = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), (ushort)spec.UnitsPerEm);
        // Рамка шрифта: xMin 0, yMin = нижний вынос, xMax = кегль, yMax = верхний вынос.
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(38), spec.Descender);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(40), (short)spec.UnitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(42), spec.Ascender);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(44), (ushort)((bold ? 1 : 0) | (spec.Italic ? 2 : 0)));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(46), 8);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(48), 2);
        return b; // indexToLocFormat = 0 (short), glyphDataFormat = 0
    }

    private static byte[] Hhea(TinyFontSpec spec, int glyphs)
    {
        var b = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4), spec.Ascender);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6), spec.Descender);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(8), spec.LineGap);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), (ushort)spec.UnitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(34), (ushort)glyphs);
        return b;
    }

    private static byte[] Maxp(int glyphs)
    {
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)glyphs);
        return b;
    }

    private static byte[] Os2(TinyFontSpec spec, bool bold, int[] codes)
    {
        var b = new byte[96];
        BinaryPrimitives.WriteUInt16BigEndian(b, 4);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), 500);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), spec.WeightClass);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 5);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), spec.FsType);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(26), (short)(spec.UnitsPerEm / 20));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(28), (short)(spec.UnitsPerEm / 4));
        "TINY"u8.CopyTo(b.AsSpan(58));
        var selection = (spec.Italic ? 1 : 0) | (bold ? 1 << 5 : 0) | (!spec.Italic && !bold ? 1 << 6 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(62), (ushort)selection);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(64), (ushort)Math.Min(codes.FirstOrDefault(), 0xFFFF));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(66), (ushort)Math.Min(codes.LastOrDefault(), 0xFFFF));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(68), spec.Ascender);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(70), spec.Descender);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(72), spec.LineGap);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(74), (ushort)spec.Ascender);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(76), (ushort)(-spec.Descender));
        return b;
    }

    private static byte[] Hmtx(ushort[] advances)
    {
        var b = new byte[advances.Length * 4];
        for (var i = 0; i < advances.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(i * 4), advances[i]);
        return b;
    }

    /// <summary>cmap: (3,1) формат 4 — один сегмент на код, (3,10) формат 12 — одна группа на код.</summary>
    private static byte[] Cmap(int[] codes)
    {
        var bmp = codes.Where(c => c <= 0xFFFF).ToArray();
        var segments = bmp.Length + 1;
        var f4 = new byte[16 + segments * 8];
        BinaryPrimitives.WriteUInt16BigEndian(f4, 4);
        BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(2), (ushort)f4.Length);
        BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(6), (ushort)(segments * 2));
        var ends = 14;
        var starts = ends + segments * 2 + 2;
        var deltas = starts + segments * 2;
        for (var i = 0; i < segments; i++)
        {
            var code = i < bmp.Length ? bmp[i] : 0xFFFF;
            var glyph = i < bmp.Length ? Array.IndexOf(codes, code) + 1 : 0;
            BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(ends + 2 * i), (ushort)code);
            BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(starts + 2 * i), (ushort)code);
            BinaryPrimitives.WriteUInt16BigEndian(f4.AsSpan(deltas + 2 * i),
                i < bmp.Length ? (ushort)((glyph - code) & 0xFFFF) : (ushort)1);
        }

        var f12 = new byte[16 + codes.Length * 12];
        BinaryPrimitives.WriteUInt16BigEndian(f12, 12);
        BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(4), (uint)f12.Length);
        BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(12), (uint)codes.Length);
        for (var i = 0; i < codes.Length; i++)
        {
            var group = f12.AsSpan(16 + 12 * i);
            BinaryPrimitives.WriteUInt32BigEndian(group, (uint)codes[i]);
            BinaryPrimitives.WriteUInt32BigEndian(group[4..], (uint)codes[i]);
            BinaryPrimitives.WriteUInt32BigEndian(group[8..], (uint)(i + 1));
        }

        var header = 4 + 2 * 8;
        var cmap = new byte[header + f4.Length + f12.Length];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), (uint)header);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16), (uint)(header + f4.Length));
        f4.CopyTo(cmap, header);
        f12.CopyTo(cmap, header + f4.Length);
        return cmap;
    }

    private static byte[] Name(TinyFontSpec spec)
    {
        var full = spec.Family + " " + spec.Subfamily;
        var entries = new (ushort Id, string Text)[]
        {
            (1, spec.Family), (2, spec.Subfamily), (4, full),
            (6, (spec.Family + "-" + spec.Subfamily).Replace(" ", "")), (16, spec.Family), (17, spec.Subfamily),
        };
        var strings = entries.Select(e => Encoding.BigEndianUnicode.GetBytes(e.Text)).ToArray();
        var storage = 6 + 12 * entries.Length;
        var b = new byte[storage + strings.Sum(s => s.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)storage);
        var offset = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var r = b.AsSpan(6 + 12 * i);
            BinaryPrimitives.WriteUInt16BigEndian(r, 3);
            BinaryPrimitives.WriteUInt16BigEndian(r[2..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(r[4..], 0x409);
            BinaryPrimitives.WriteUInt16BigEndian(r[6..], entries[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(r[8..], (ushort)strings[i].Length);
            BinaryPrimitives.WriteUInt16BigEndian(r[10..], (ushort)offset);
            strings[i].CopyTo(b, storage + offset);
            offset += strings[i].Length;
        }
        return b;
    }

    private static byte[] Post(TinyFontSpec spec)
    {
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0x00030000);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(8), (short)(-spec.UnitsPerEm / 10));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(10), (short)(spec.UnitsPerEm / 20));
        return b;
    }
}
