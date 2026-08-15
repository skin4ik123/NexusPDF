using NexusPdf.Infrastructure;
using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Профили печати. Главное здесь не «сохранилось», а ЧТО именно сохраняется:
/// профиль не должен уносить с собой пароль, PIN или диапазон страниц.
/// </summary>
public sealed class PrintProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));

    private PrintProfileStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        return new PrintProfileStore(Path.Combine(_dir, "profiles.json"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Built_In_Profiles_Are_Available_Without_A_File()
    {
        var store = NewStore();
        var all = store.LoadAll();

        Assert.Equal(BuiltInPrintProfiles.All.Count, all.Count);
        Assert.All(all, p => Assert.True(p.IsBuiltIn));
        Assert.Contains(all, p => p.Name == "Буклет" && p.Imposition == ImpositionMode.Booklet);
        Assert.Contains(all, p => p.Name == "4 страницы на лист" && p.NUpColumns == 2 && p.NUpRows == 2);
    }

    [Fact]
    public void Saved_Profile_Survives_A_Reload()
    {
        var store = NewStore();
        var settings = new LayoutSettings
        {
            Imposition = ImpositionMode.NUp,
            NUp = new NUpSettings { Rows = 3, Columns = 2 },
            Size = SizeMode.CustomScale,
            CustomScale = 0.75,
            Duplex = DuplexMode.ShortEdge,
            Color = ColorMode.Grayscale,
            Marks = new MarkSettings { Marks = PrinterMarks.CropMarks, BleedPt = 9 },
        };
        store.Save(PrintProfile.FromSettings("Мой профиль", settings, "HP", "A4"));

        var loaded = NewStore().LoadAll().Single(p => p.Name == "Мой профиль");
        Assert.False(loaded.IsBuiltIn);
        Assert.Equal(ImpositionMode.NUp, loaded.Imposition);
        Assert.Equal(3, loaded.NUpRows);
        Assert.Equal(0.75, loaded.CustomScale, 3);
        Assert.Equal(DuplexMode.ShortEdge, loaded.Duplex);
        Assert.Equal(ColorMode.Grayscale, loaded.Color);
        Assert.Equal(PrinterMarks.CropMarks, loaded.Marks);
        Assert.Equal(9, loaded.BleedPt, 3);
        Assert.Equal("HP", loaded.PrinterName);
        Assert.Equal("A4", loaded.PaperName);
    }

    [Fact]
    public void Round_Trip_Through_Settings_Keeps_The_Layout()
    {
        var original = new LayoutSettings
        {
            Imposition = ImpositionMode.Booklet,
            Booklet = new BookletSettings { SignatureSize = 8, CompensateCreep = true },
            Orientation = OrientationMode.Landscape,
            Annotations = AnnotationPolicy.DocumentOnly,
            PrintAsImage = true,
        };
        var restored = PrintProfile.FromSettings("x", original).ToSettings();

        Assert.Equal(original.Imposition, restored.Imposition);
        Assert.Equal(original.Booklet.SignatureSize, restored.Booklet.SignatureSize);
        Assert.Equal(original.Booklet.CompensateCreep, restored.Booklet.CompensateCreep);
        Assert.Equal(original.Orientation, restored.Orientation);
        Assert.Equal(original.Annotations, restored.Annotations);
        Assert.Equal(original.PrintAsImage, restored.PrintAsImage);
    }

    [Fact]
    public void Profile_Cannot_Carry_A_Page_Range()
    {
        // Профиль «Черновик», молча печатающий вчерашний диапазон вместо
        // всего документа, — ловушка. Поля для диапазона в записи нет вовсе.
        var properties = typeof(PrintProfile).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("RangeText", properties);
        Assert.DoesNotContain("Scope", properties);
        Assert.DoesNotContain("Password", properties);
        Assert.DoesNotContain("Pin", properties);
    }

    [Fact]
    public void User_Profile_Overrides_A_Built_In_One_By_Name()
    {
        var store = NewStore();
        store.Save(PrintProfile.FromSettings("Буклет",
            new LayoutSettings { Imposition = ImpositionMode.Single }));

        var all = NewStore().LoadAll();
        var booklet = all.Single(p => p.Name == "Буклет");
        Assert.False(booklet.IsBuiltIn);
        Assert.Equal(ImpositionMode.Single, booklet.Imposition);

        // Количество не растёт: переопределение занимает место встроенного.
        Assert.Equal(BuiltInPrintProfiles.All.Count, all.Count);
    }

    [Fact]
    public void Deleting_An_Override_Restores_The_Built_In_Profile()
    {
        var store = NewStore();
        store.Save(PrintProfile.FromSettings("Буклет",
            new LayoutSettings { Imposition = ImpositionMode.Single }));
        store.Delete("Буклет");

        var booklet = NewStore().LoadAll().Single(p => p.Name == "Буклет");
        Assert.True(booklet.IsBuiltIn);
        Assert.Equal(ImpositionMode.Booklet, booklet.Imposition);
    }

    [Fact]
    public void Corrupted_File_Does_Not_Break_Printing()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "profiles.json");
        File.WriteAllText(path, "{ это не JSON");

        var all = new PrintProfileStore(path).LoadAll();
        Assert.Equal(BuiltInPrintProfiles.All.Count, all.Count);
    }

    [Fact]
    public void Profile_Without_A_Name_Is_Rejected()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() =>
            store.Save(PrintProfile.FromSettings("   ", new LayoutSettings())));
    }
}
