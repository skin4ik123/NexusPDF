using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.TestSupport;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Шрифт документа → шрифт Windows, которым будет написан текст. Список
/// «In this document» показывает, чем заменится, например, Helvetica бланка.
/// </summary>
public sealed class FontSubstitutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", "subst-" + Guid.NewGuid().ToString("N"));
    private readonly FontCatalog _fonts;

    public FontSubstitutionTests()
    {
        Directory.CreateDirectory(_dir);
        foreach (var family in new[] { "Arial", "Times New Roman", "Courier New" })
            File.WriteAllBytes(Path.Combine(_dir, family.Replace(" ", "") + ".ttf"),
                TinyFontBuilder.Build(new TinyFontSpec(family)));
        _fonts = FontCatalog.Scan([_dir, TestFontPaths.Directory]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("Helvetica", "Arial")]
    [InlineData("ABCDEF+Helvetica-Bold", "Arial")]
    [InlineData("Arial-BoldMT", "Arial")]
    [InlineData("TimesNewRomanPSMT", "Times New Roman")]
    [InlineData("TimesNewRomanPS-BoldMT", "Times New Roman")]
    [InlineData("Times-Italic", "Times New Roman")]
    [InlineData("Courier", "Courier New")]
    [InlineData("ABCDEF+LiberationSans,Bold", "Liberation Sans")]
    [InlineData("LiberationMono", "Liberation Mono")]
    [InlineData("UnknownFont", null)]
    [InlineData("", null)]
    public void Maps_Pdf_Font_Names_To_Installed_Families(string pdfName, string? expected)
    {
        Assert.Equal(expected, FontSubstitution.FamilyFor(pdfName, _fonts));
    }
}
