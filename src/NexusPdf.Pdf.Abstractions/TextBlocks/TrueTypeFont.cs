using System.Buffers.Binary;
using System.Text;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Шрифт TrueType/OpenType глазами вёрстки: ширины букв, вертикальные
/// размеры, есть ли буква в шрифте, разрешено ли встраивание.
///
/// Файлы шрифтов на чужом компьютере — недоверенные данные: битый шрифт в
/// папке Fonts не должен ронять программу. Каждое чтение проверяет границы,
/// и любой дефект превращается в <see cref="FormatException"/>.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly uint _sfntVersion;
    private readonly int _glyphCount;
    private readonly int _hMetrics;
    private readonly int _hmtx;
    private readonly CharacterMap _cmap;

    private TrueTypeFont(byte[] data, uint sfntVersion, Dictionary<string, (int, int)> tables)
    {
        _data = data;
        _sfntVersion = sfntVersion;
        _tables = tables;

        var head = Table("head", 54);
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm is < 16 or > 16384)
            throw new FormatException("Недопустимый размер em в шрифте.");
        var macStyle = U16(head + 44);

        var hhea = Table("hhea", 36);
        Ascender = I16(hhea + 4);
        Descender = I16(hhea + 6);
        LineGap = I16(hhea + 8);
        _hMetrics = U16(hhea + 34);

        _glyphCount = U16(Table("maxp", 6) + 4);
        if (_hMetrics < 1 || _hMetrics > _glyphCount)
            throw new FormatException("Таблица ширин шрифта не согласована с числом глифов.");
        _hmtx = Table("hmtx", _hMetrics * 4);

        if (TryTable("OS/2", 64, out var os2))
        {
            WeightClass = U16(os2 + 4);
            FsType = U16(os2 + 8);
            StrikeoutSize = I16(os2 + 26);
            StrikeoutPosition = I16(os2 + 28);
            var selection = U16(os2 + 62);
            IsItalic = (selection & 0x01) != 0;
            IsBold = (selection & 0x20) != 0;
        }
        else
        {
            WeightClass = (macStyle & 1) != 0 ? 700 : 400;
            IsBold = (macStyle & 1) != 0;
            IsItalic = (macStyle & 2) != 0;
            StrikeoutSize = (short)(UnitsPerEm / 20);
            StrikeoutPosition = (short)(UnitsPerEm / 4);
        }

        if (TryTable("post", 12, out var post))
        {
            UnderlinePosition = I16(post + 8);
            UnderlineThickness = I16(post + 10);
        }
        else
        {
            UnderlinePosition = (short)(-UnitsPerEm / 10);
            UnderlineThickness = (short)(UnitsPerEm / 20);
        }

        FamilyName = NameString(16) ?? NameString(1) ?? "";
        SubfamilyName = NameString(17) ?? NameString(2) ?? "";
        PostScriptName = NameString(6) ?? "";
        _cmap = CharacterMap.Read(this);
    }

    public string FamilyName { get; }
    public string SubfamilyName { get; }
    public string PostScriptName { get; }
    public int UnitsPerEm { get; }
    public bool IsBold { get; }
    public bool IsItalic { get; }
    public int WeightClass { get; }
    public short Ascender { get; }
    public short Descender { get; }
    public short LineGap { get; }
    public short UnderlinePosition { get; }
    public short UnderlineThickness { get; }
    public short StrikeoutPosition { get; }
    public short StrikeoutSize { get; }
    public ushort FsType { get; }

    /// <summary>
    /// Можно ли встраивать в документ. Запрещено при «Restricted License»
    /// (бит 1 без разрешающих битов 2 и 3) и для шрифтов «только растр».
    /// </summary>
    public bool IsEmbeddable => (FsType & 0x000F) != 0x0002 && (FsType & 0x0200) == 0;

    /// <summary>Контуры TrueType (таблица glyf), а не CFF.</summary>
    public bool HasTrueTypeOutlines => _tables.ContainsKey("glyf");

    public bool HasGlyph(int codePoint) => GlyphOf(codePoint) != 0;

    /// <summary>
    /// Диапазоны кодов из таблицы кодов (с запасом: внутри диапазона глиф
    /// может оказаться пустым). Для быстрого отсева при поиске запасного шрифта.
    /// </summary>
    public IReadOnlyList<CodeRange> CoveredRanges()
    {
        try
        {
            return _cmap.Ranges();
        }
        catch (FormatException)
        {
            return [];
        }
    }

    /// <summary>Ширина буквы в единицах шрифта; нет буквы — ширина .notdef.</summary>
    public int AdvanceWidth(int codePoint)
    {
        var glyph = Math.Min(GlyphOf(codePoint), _hMetrics - 1);
        return U16(_hmtx + glyph * 4);
    }

    private int GlyphOf(int codePoint)
    {
        int glyph;
        try
        {
            glyph = _cmap.Lookup(codePoint);
        }
        catch (FormatException)
        {
            // Испорченный участок таблицы кодов: буквы «нет», а не падение вёрстки.
            return 0;
        }
        return glyph >= 0 && glyph < _glyphCount ? glyph : 0;
    }

    /// <summary>Число шрифтов в данных: 1 у обычного файла, n у коллекции, 0 — это не шрифт.</summary>
    public static int FaceCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return 0;
        var tag = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (tag == TtcTag)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            return count is > 0 and < 10_000 && data.Length >= 12 + 4L * count ? (int)count : 0;
        }
        return IsSfnt(tag) ? 1 : 0;
    }

    public static TrueTypeFont Parse(byte[] data, int faceIndex = 0)
    {
        try
        {
            var faces = FaceCount(data);
            if (faces == 0)
                throw new FormatException("Это не файл шрифта TrueType/OpenType.");
            if (faceIndex < 0 || faceIndex >= faces)
                throw new FormatException("В файле шрифта нет лица с таким номером.");

            var directory = BinaryPrimitives.ReadUInt32BigEndian(data) == TtcTag
                ? checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + 4 * faceIndex)))
                : 0;
            if (directory < 0 || directory + 12 > data.Length)
                throw new FormatException("Каталог таблиц шрифта за пределами файла.");
            var version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(directory));
            if (!IsSfnt(version))
                throw new FormatException("Неизвестный формат шрифта.");

            var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(directory + 4));
            if (directory + 12 + 16L * count > data.Length)
                throw new FormatException("Каталог таблиц шрифта обрезан.");
            var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var record = data.AsSpan(directory + 12 + 16 * i, 16);
                var tag = Encoding.ASCII.GetString(record[..4]);
                var offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                var length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
                if ((long)offset + length > data.Length)
                    throw new FormatException($"Таблица {tag} шрифта выходит за пределы файла.");
                tables[tag] = ((int)offset, (int)length);
            }
            return new TrueTypeFont(data, version, tables);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new FormatException("Файл шрифта повреждён.", ex);
        }
    }

    /// <summary>
    /// Отдельный файл шрифта с таблицами этого лица — для коллекций (.ttc),
    /// которые PDFium целиком не встраивает. У обычного файла — копия данных.
    /// </summary>
    public byte[] ExtractFace()
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(_data) != TtcTag)
            return (byte[])_data.Clone();

        var tags = _tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var header = 12 + 16 * tags.Length;
        var output = new byte[header + tags.Sum(t => (_tables[t].Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32BigEndian(output, _sfntVersion);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)tags.Length);
        var power = 1;
        while (power * 2 <= tags.Length) power *= 2;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)Math.Log2(power));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)((tags.Length - power) * 16));

        var offset = header;
        for (var i = 0; i < tags.Length; i++)
        {
            var (from, length) = _tables[tags[i]];
            var record = output.AsSpan(12 + 16 * i);
            Encoding.ASCII.GetBytes(tags[i]).CopyTo(record);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(_data.AsSpan(from, length)));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)length);
            _data.AsSpan(from, length).CopyTo(output.AsSpan(offset));
            offset += (length + 3) & ~3;
        }
        return output;
    }

    // ----- Чтение с проверкой границ -----

    private const uint TtcTag = 0x74746366; // 'ttcf'

    private static bool IsSfnt(uint tag) =>
        tag is 0x00010000 or 0x74727565 /* 'true' */ or 0x4F54544F /* 'OTTO' */;

    private static uint Checksum(ReadOnlySpan<byte> table)
    {
        uint sum = 0;
        for (var i = 0; i < table.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++)
                word = (word << 8) | (i + k < table.Length ? table[i + k] : 0u);
            sum += word;
        }
        return sum;
    }

    private int Table(string tag, int minimum)
    {
        if (!TryTable(tag, minimum, out var offset))
            throw new FormatException($"В шрифте нет таблицы {tag}.");
        return offset;
    }

    private bool TryTable(string tag, int minimum, out int offset)
    {
        offset = 0;
        if (!_tables.TryGetValue(tag, out var table) || table.Length < minimum)
            return false;
        offset = table.Offset;
        return true;
    }

    private int TableEnd(string tag) => _tables[tag].Offset + _tables[tag].Length;

    private ushort U16(int at) =>
        at >= 0 && at + 2 <= _data.Length
            ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at))
            : throw new FormatException("Чтение за пределами файла шрифта.");

    private short I16(int at) => unchecked((short)U16(at));

    private uint U32(int at) =>
        at >= 0 && at + 4 <= _data.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at))
            : throw new FormatException("Чтение за пределами файла шрифта.");

    /// <summary>
    /// Строка таблицы name: Windows (английский, затем любой язык), затем
    /// Mac Roman, затем Unicode-платформа.
    /// </summary>
    private string? NameString(int nameId)
    {
        if (!TryTable("name", 6, out var name)) return null;
        var count = U16(name + 2);
        var storage = name + U16(name + 4);
        string? windows = null, windowsAny = null, mac = null, unicode = null;
        for (var i = 0; i < count; i++)
        {
            var record = name + 6 + 12 * i;
            if (U16(record + 6) != nameId) continue;
            var platform = U16(record);
            var encoding = U16(record + 2);
            var language = U16(record + 4);
            var length = U16(record + 8);
            var start = storage + U16(record + 10);
            if (start + length > TableEnd("name")) continue;
            var bytes = _data.AsSpan(start, length);
            switch (platform)
            {
                case 3 when encoding is 0 or 1 or 10:
                    var text = Encoding.BigEndianUnicode.GetString(bytes);
                    if (language == 0x409) windows ??= text;
                    else windowsAny ??= text;
                    break;
                case 1 when encoding == 0:
                    mac ??= Encoding.Latin1.GetString(bytes);
                    break;
                case 0:
                    unicode ??= Encoding.BigEndianUnicode.GetString(bytes);
                    break;
            }
        }
        var result = windows ?? windowsAny ?? mac ?? unicode;
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>Отображение «кодовая точка → глиф» из таблицы cmap (форматы 4 и 12).</summary>
    private sealed class CharacterMap
    {
        private readonly TrueTypeFont _font;
        private readonly int _format4 = -1;
        private readonly int _format12 = -1;
        private readonly bool _symbol;

        private CharacterMap(TrueTypeFont font, int format4, int format12, bool symbol)
        {
            _font = font;
            _format4 = format4;
            _format12 = format12;
            _symbol = symbol;
        }

        public static CharacterMap Read(TrueTypeFont font)
        {
            var cmap = font.Table("cmap", 4);
            var end = font.TableEnd("cmap");
            var count = font.U16(cmap + 2);
            int unicode4 = -1, unicode12 = -1, symbol4 = -1;
            for (var i = 0; i < count; i++)
            {
                var record = cmap + 4 + 8 * i;
                var platform = font.U16(record);
                var encoding = font.U16(record + 2);
                var offset = font.U32(record + 4);
                if (offset >= end - cmap) continue;
                var sub = cmap + (int)offset;
                var format = font.U16(sub);
                var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
                if (format == 12 && unicode && unicode12 < 0) unicode12 = sub;
                else if (format == 4 && unicode && unicode4 < 0) unicode4 = sub;
                else if (format == 4 && platform == 3 && encoding == 0 && symbol4 < 0) symbol4 = sub;
            }
            if (unicode4 < 0 && unicode12 < 0 && symbol4 < 0)
                throw new FormatException("В шрифте нет таблицы Unicode-кодов.");
            return new CharacterMap(font, unicode4 >= 0 ? unicode4 : symbol4, unicode12, unicode4 < 0 && symbol4 >= 0);
        }

        public IReadOnlyList<CodeRange> Ranges()
        {
            var f = _font;
            var ranges = new List<CodeRange>();
            if (_format12 >= 0)
            {
                var groups = f.U32(_format12 + 12);
                if (groups > (uint)(f._data.Length / 12)) throw new FormatException("Таблица кодов шрифта обрезана.");
                for (var g = 0; g < groups; g++)
                {
                    var at = _format12 + 16 + 12 * g;
                    var start = f.U32(at);
                    var end = f.U32(at + 4);
                    if (start <= end && end <= 0x10FFFF) ranges.Add(new CodeRange((int)start, (int)end));
                }
            }
            if (_format4 >= 0)
            {
                var segments = f.U16(_format4 + 6) / 2;
                var ends = _format4 + 14;
                var starts = ends + segments * 2 + 2;
                for (var i = 0; i < segments; i++)
                {
                    var start = f.U16(starts + 2 * i);
                    var end = f.U16(ends + 2 * i);
                    if (start > end || start == 0xFFFF) continue;
                    if (_symbol && start >= 0xF000 && end <= 0xF0FF)
                        ranges.Add(new CodeRange(start - 0xF000, end - 0xF000));
                    ranges.Add(new CodeRange(start, end));
                }
            }
            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<CodeRange>(ranges.Count);
            foreach (var range in ranges)
            {
                if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
                    merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
                else
                    merged.Add(range);
            }
            return merged;
        }

        public int Lookup(int codePoint)
        {
            if (codePoint < 0) return 0;
            if (_format12 >= 0)
            {
                var glyph = Lookup12(codePoint);
                if (glyph != 0 || _format4 < 0) return glyph;
            }
            if (_format4 < 0 || codePoint > 0xFFFF) return 0;
            var found = Lookup4(codePoint);
            // Символьные шрифты кладут буквы в область F000–F0FF.
            return found == 0 && _symbol && codePoint <= 0xFF ? Lookup4(0xF000 + codePoint) : found;
        }

        private int Lookup4(int code)
        {
            var f = _font;
            var segments = f.U16(_format4 + 6) / 2;
            var ends = _format4 + 14;
            var starts = ends + segments * 2 + 2;
            var deltas = starts + segments * 2;
            var ranges = deltas + segments * 2;
            int lo = 0, hi = segments - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (f.U16(ends + 2 * mid) < code) lo = mid + 1;
                else hi = mid - 1;
            }
            if (lo >= segments) return 0;
            var start = f.U16(starts + 2 * lo);
            if (code < start) return 0;
            var delta = f.U16(deltas + 2 * lo);
            var rangeAt = ranges + 2 * lo;
            var rangeOffset = f.U16(rangeAt);
            if (rangeOffset == 0) return (code + delta) & 0xFFFF;
            var glyph = f.U16(rangeAt + rangeOffset + 2 * (code - start));
            return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
        }

        private int Lookup12(int code)
        {
            var f = _font;
            var groups = f.U32(_format12 + 12);
            if (groups > (uint)(f._data.Length / 12)) throw new FormatException("Таблица кодов шрифта обрезана.");
            long lo = 0, hi = groups - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var group = _format12 + 16 + (int)mid * 12;
                var first = f.U32(group);
                var last = f.U32(group + 4);
                if (code < first) hi = mid - 1;
                else if (code > last) lo = mid + 1;
                else return (int)(f.U32(group + 8) + (uint)(code - first));
            }
            return 0;
        }
    }
}
