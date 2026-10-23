using NexusPdf.Ux;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Список шрифтов как в редакторах вёрстки: недавние, шрифты документа, все
/// установленные; поиск по вхождению; то, что в PDF не попадёт, видно, но
/// выбрать нельзя.
/// </summary>
public sealed class FontPickerModelTests
{
    private static readonly FontChoice[] All =
    [
        new("Arial", HasCyrillic: true, Embeddable: true, Supported: true),
        new("Bahnschrift", HasCyrillic: false, Embeddable: true, Supported: true),
        new("Locked Sans", HasCyrillic: true, Embeddable: false, Supported: true),
        new("Old Type1", HasCyrillic: true, Embeddable: true, Supported: false),
        new("Big CJK", HasCyrillic: true, Embeddable: true, Supported: true, IsLarge: true),
        new("Courier New", HasCyrillic: true, Embeddable: true, Supported: true),
    ];

    private static IReadOnlyList<FontPickerItem> Build(
        string query = "", string[]? recent = null, (string, string?)[]? inDocument = null) =>
        FontPickerModel.Build(All, recent ?? [], inDocument ?? [], query);

    [Fact]
    public void Groups_Come_In_Order_And_Empty_Ones_Are_Omitted()
    {
        var items = Build(recent: ["Courier New"], inDocument: [("ABCDEF+Arial-BoldMT", "Arial")]);
        Assert.Equal([FontGroup.Recent, FontGroup.InDocument, FontGroup.All],
            items.Select(i => i.Group).Distinct());

        Assert.DoesNotContain(Build(), i => i.Group is FontGroup.Recent or FontGroup.InDocument);
    }

    [Fact]
    public void All_Fonts_Are_Sorted_By_Name()
    {
        var names = Build().Where(i => i.Group == FontGroup.All).Select(i => i.Family).ToList();
        Assert.Equal(names.Order(StringComparer.CurrentCultureIgnoreCase), names);
        Assert.Equal(All.Length, names.Count);
    }

    [Fact]
    public void Fonts_That_Cannot_Go_Into_Pdf_Are_Shown_Disabled_With_The_Reason()
    {
        var items = Build().Where(i => i.Group == FontGroup.All).ToDictionary(i => i.Family);

        Assert.Equal((false, "FontNotEmbeddable"), (items["Locked Sans"].Enabled, items["Locked Sans"].NoteKey));
        Assert.Equal((false, "FontNotSupported"), (items["Old Type1"].Enabled, items["Old Type1"].NoteKey));
        Assert.Equal((true, "FontNoCyrillic"), (items["Bahnschrift"].Enabled, items["Bahnschrift"].NoteKey));
        Assert.Equal((true, "FontLarge"), (items["Big CJK"].Enabled, items["Big CJK"].NoteKey));
        Assert.Equal((true, (string?)null), (items["Arial"].Enabled, items["Arial"].NoteKey));
    }

    [Fact]
    public void Recent_Keeps_Its_Order_And_Drops_Missing_Fonts()
    {
        var recent = Build(recent: ["Courier New", "Gone Font", "Arial"])
            .Where(i => i.Group == FontGroup.Recent).Select(i => i.Family);
        Assert.Equal(["Courier New", "Arial"], recent);
    }

    [Fact]
    public void Document_Fonts_Show_What_They_Will_Be_Written_With()
    {
        var items = Build(inDocument: [("ABCDEF+Helvetica", "Arial"), ("Arial", "Arial"), ("QWERTY+Arial", "Arial"), ("Weird", null)])
            .Where(i => i.Group == FontGroup.InDocument).ToList();

        Assert.Equal(["Helvetica", "Arial"], items.Select(i => i.Family));
        Assert.Equal(("Arial", "FontWrittenWith"), (items[0].SubstituteFamily, items[0].NoteKey));
        Assert.Equal(("Arial", (string?)null), (items[1].SubstituteFamily, items[1].NoteKey));
        Assert.All(items, i => Assert.True(i.Enabled));
    }

    [Fact]
    public void Search_Is_A_Case_Insensitive_Substring_Across_Groups()
    {
        var items = Build("cour", recent: ["Courier New", "Arial"], inDocument: [("Helvetica", "Arial")]);

        Assert.All(items, i => Assert.Contains("cour", i.Family, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([FontGroup.Recent, FontGroup.All], items.Select(i => i.Group).Distinct());
        Assert.Empty(Build("no such font"));
    }
}
