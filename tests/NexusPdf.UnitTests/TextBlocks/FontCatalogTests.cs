using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Каталог установленных шрифтов: из него берётся список в окне выбора и
/// файлы для вёрстки и записи в PDF. Битый шрифт в системе не должен ломать
/// ни список, ни программу.
/// </summary>
public sealed class FontCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "fonts-" + Guid.NewGuid().ToString("N"));

    public FontCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Put(string name, byte[] data) => File.WriteAllBytes(Path.Combine(_dir, name), data);

    [Fact]
    public void Fallback_Families_Skip_Fonts_That_Cannot_Be_Embedded()
    {
        Put("locked.ttf", TinyFontBuilder.Build(new TinyFontSpec("Locked", FsType: 0x0002)));
        Put("open.ttf", TinyFontBuilder.Build(new TinyFontSpec("Open")));

        var catalog = FontCatalog.Scan([_dir]);

        Assert.Contains(catalog.Families, f => f.Name == "Locked");     // в списке есть, с пометкой
        Assert.DoesNotContain("Locked", catalog.FallbackFamilies);     // но запасным не берётся
        Assert.Contains("Open", catalog.FallbackFamilies);
    }

    [Fact]
    public void Scans_Families_And_Faces()
    {
        var catalog = FontCatalog.Scan([TestFontPaths.Directory]);

        var sans = Assert.Single(catalog.Families, f => f.Name == "Liberation Sans");
        Assert.Equal(4, sans.Faces.Count);
        Assert.Contains(catalog.Families, f => f.Name == "Liberation Mono");
        Assert.EndsWith("LiberationSans-BoldItalic.ttf", catalog.Resolve("Liberation Sans", true, true)!.Ref.Path);
        Assert.EndsWith("LiberationSans-Regular.ttf", catalog.Resolve("liberation sans", false, false)!.Ref.Path);
        Assert.True(sans.HasCyrillic);
        Assert.True(sans.Embeddable);
        Assert.True(sans.Supported);
        Assert.Null(catalog.Resolve("No Such Font", false, false));
    }

    [Fact]
    public void Missing_Style_Falls_Back_To_The_Nearest_Face()
    {
        var catalog = FontCatalog.Scan([TestFontPaths.Directory]);
        // У Liberation Mono в тестовых шрифтах только обычное начертание.
        Assert.EndsWith("LiberationMono-Regular.ttf", catalog.Resolve("Liberation Mono", true, true)!.Ref.Path);
    }

    [Fact]
    public void Marks_Fonts_Without_Cyrillic_Or_Embedding_Rights()
    {
        Put("latin.ttf", TinyFontBuilder.Build(new TinyFontSpec("Latin Only")));
        Put("locked.ttf", TinyFontBuilder.Build(new TinyFontSpec("Locked",
            FsType: 0x0002,
            Advances: Enumerable.Range(0x20, 0x5F).Concat(Enumerable.Range(0x400, 0x60))
                .ToDictionary(c => c, _ => (ushort)500))));

        var catalog = FontCatalog.Scan([_dir]);

        var latin = Assert.Single(catalog.Families, f => f.Name == "Latin Only");
        Assert.False(latin.HasCyrillic);
        Assert.True(latin.Embeddable);
        var locked = Assert.Single(catalog.Families, f => f.Name == "Locked");
        Assert.True(locked.HasCyrillic);
        Assert.False(locked.Embeddable);
    }

    [Fact]
    public void Skips_Broken_Font_Files()
    {
        Put("broken.ttf", new byte[10]);
        Put("truncated.ttf", File.ReadAllBytes(TestFontPaths.Sans())[..5000]);
        Put("fine.ttf", TinyFontBuilder.Build(new TinyFontSpec("Fine")));
        Put("notes.txt", "not a font"u8.ToArray());

        var catalog = FontCatalog.Scan([_dir, Path.Combine(_dir, "missing-dir")]);

        Assert.Equal("Fine", Assert.Single(catalog.Families).Name);
    }

    [Fact]
    public void Collection_Faces_Are_Listed()
    {
        Put("pair.ttc", TinyFontBuilder.BuildCollection(
            TinyFontBuilder.Build(new TinyFontSpec("First")),
            TinyFontBuilder.Build(new TinyFontSpec("Second"))));

        var catalog = FontCatalog.Scan([_dir]);

        Assert.Equal(0, catalog.Resolve("First", false, false)!.Ref.FaceIndex);
        var second = catalog.Resolve("Second", false, false)!;
        Assert.Equal(1, second.Ref.FaceIndex);
        Assert.Equal("Second", catalog.Load(second.Ref).FamilyName);
    }

    [Fact]
    public void Fallback_Families_Start_With_Preferred_And_Contain_All()
    {
        var catalog = FontCatalog.Scan([TestFontPaths.Directory]);
        Assert.Equal(catalog.Families.Count, catalog.FallbackFamilies.Count);
        Assert.Contains("Liberation Sans", catalog.FallbackFamilies);
    }
}
