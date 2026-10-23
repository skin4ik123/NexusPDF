using System.Collections.Concurrent;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>Лицо шрифта: файл и номер лица в нём (у коллекций .ttc их несколько).</summary>
public sealed record FontFaceRef(string Path, int FaceIndex);

/// <summary>Одно начертание установленного шрифта и всё, что о нём нужно знать списку шрифтов.</summary>
/// <param name="HasCyrillic">Есть все буквы русского алфавита.</param>
/// <param name="Embeddable">Автор шрифта разрешает встраивать его в документы.</param>
/// <param name="Supported">Контуры TrueType: такой шрифт PDFium встраивает.</param>
public sealed record FontFaceInfo(
    FontFaceRef Ref, string Family, string Subfamily, bool Bold, bool Italic,
    int Weight, bool HasCyrillic, bool Embeddable, bool Supported, long FileSize);

/// <summary>Семейство шрифтов с его начертаниями.</summary>
public sealed record FontFamilyInfo(string Name, IReadOnlyList<FontFaceInfo> Faces)
{
    public bool HasCyrillic => Faces.Any(f => f.HasCyrillic);
    public bool Embeddable => Faces.Any(f => f.Embeddable);
    public bool Supported => Faces.Any(f => f.Supported);
}

/// <summary>Шрифты для вёрстки и записи текстовых блоков.</summary>
public interface IFontProvider
{
    /// <summary>Начертание семейства; нет нужного — ближайшее; нет семейства — null.</summary>
    FontFaceInfo? Resolve(string family, bool bold, bool italic);

    /// <summary>Разобранный шрифт лица (кэшируется).</summary>
    TrueTypeFont Load(FontFaceRef face);

    /// <summary>
    /// Семейства для букв, которых нет в выбранном шрифте: сначала шрифт по
    /// умолчанию, Arial и Segoe UI, затем все остальные.
    /// </summary>
    IReadOnlyList<string> FallbackFamilies { get; }
}

/// <summary>
/// Все шрифты, установленные в Windows (системные и пользовательские), а не
/// закрытый список: человек выбирает любой, а то, что в PDF не попадёт или
/// напишет квадраты, помечается, а не прячется.
/// </summary>
public sealed class FontCatalog : IFontProvider
{
    /// <summary>Гарнитура по умолчанию — ею пишется текст, если выбора не сделали.</summary>
    public const string DefaultFamily = "Segoe UI";

    private static readonly Lazy<FontCatalog> SystemCatalog = new(() => Scan(SystemFontDirectories()));

    private readonly Dictionary<string, FontFamilyInfo> _byName;
    private readonly ConcurrentDictionary<FontFaceRef, TrueTypeFont> _loaded = new();

    private FontCatalog(IReadOnlyList<FontFamilyInfo> families)
    {
        Families = families;
        _byName = families.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var preferred = new[] { DefaultFamily, "Arial", "Segoe UI" }
            .Where(_byName.ContainsKey)
            .Select(name => _byName[name].Name)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        // Запасными берутся только шрифты, которые можно вписать в PDF:
        // недостающая буква не должна уйти в файл шрифтом с запретом
        // встраивания или без контуров TrueType.
        var usable = families
            .Where(f => f.Faces.Any(face => face.Embeddable && face.Supported))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        FallbackFamilies = preferred
            .Concat(families.Select(f => f.Name))
            .Where(usable.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Шрифты этого компьютера (сканируются один раз при первом обращении).</summary>
    public static FontCatalog System => SystemCatalog.Value;

    public IReadOnlyList<FontFamilyInfo> Families { get; }

    public IReadOnlyList<string> FallbackFamilies { get; }

    /// <summary>Шрифт крупнее этого раздувает PDF заметно — список его помечает.</summary>
    public long LargeFontWarningBytes { get; } = 10 * 1024 * 1024;

    /// <summary>Папки шрифтов Windows: общая и пользовательская.</summary>
    public static IReadOnlyList<string> SystemFontDirectories()
    {
        var result = new List<string>();
        var shared = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(shared)) result.Add(shared);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) result.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        return result;
    }

    /// <summary>
    /// Каталог по папкам. Нечитаемые и испорченные файлы пропускаются молча —
    /// один битый шрифт не должен лишать человека всего списка.
    /// </summary>
    public static FontCatalog Scan(IEnumerable<string> directories)
    {
        var files = directories
            .Where(Directory.Exists)
            .SelectMany(dir =>
            {
                try
                {
                    return Directory.EnumerateFiles(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Enumerable.Empty<string>();
                }
            })
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var faces = new ConcurrentBag<FontFaceInfo>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 2 }, path =>
        {
            foreach (var face in Inspect(path))
                faces.Add(face);
        });

        var families = faces
            .GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FontFamilyInfo(g.First().Family, g
                .OrderBy(f => f.Bold).ThenBy(f => f.Italic).ThenBy(f => Math.Abs(f.Weight - 400))
                .ThenBy(f => f.Ref.Path, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Ref.FaceIndex)
                .ToList()))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FontCatalog(families);
    }

    private static IEnumerable<FontFaceInfo> Inspect(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        var count = TrueTypeFont.FaceCount(data);
        for (var index = 0; index < count; index++)
        {
            TrueTypeFont font;
            try
            {
                font = TrueTypeFont.Parse(data, index);
            }
            catch (FormatException)
            {
                continue;
            }
            if (font.FamilyName.Length == 0) continue;
            yield return new FontFaceInfo(
                new FontFaceRef(path, index), font.FamilyName, font.SubfamilyName, font.IsBold, font.IsItalic,
                font.WeightClass, HasCyrillic(font), font.IsEmbeddable, font.HasTrueTypeOutlines, data.LongLength);
        }
    }

    private static bool HasCyrillic(TrueTypeFont font)
    {
        for (var c = 0x410; c <= 0x44F; c++)
        {
            if (!font.HasGlyph(c)) return false;
        }
        return font.HasGlyph(0x401) && font.HasGlyph(0x451);
    }

    public FontFaceInfo? Resolve(string family, bool bold, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family) || !_byName.TryGetValue(family.Trim(), out var info))
            return null;
        // Как и прежде: нет нужного начертания — ближайшее, синтетическое
        // утолщение или наклон не делаются.
        return info.Faces.FirstOrDefault(f => f.Bold == bold && f.Italic == italic)
               ?? info.Faces.FirstOrDefault(f => f.Bold == bold && !f.Italic)
               ?? info.Faces.FirstOrDefault(f => !f.Bold && !f.Italic)
               ?? info.Faces[0];
    }

    public TrueTypeFont Load(FontFaceRef face) =>
        _loaded.GetOrAdd(face, f => TrueTypeFont.Parse(File.ReadAllBytes(f.Path), f.FaceIndex));
}
