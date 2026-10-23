using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Отпечаток объектов блока замечает чужую правку (место, размер, цвет,
/// шрифт, наклон), но не содержит текста: иначе по нему подбирался бы
/// короткий текст, вымаранный другой программой.
/// </summary>
public sealed class TextBlockHashTests
{
    private static TextBlockHash.Piece Piece(string reference = "0:0", string font = "LiberationSans", double size = 12,
        uint color = 0xFF000000, double a = 1, double b = 0, double c = 0, double d = 1, double x = 10, double y = 20) =>
        new(reference, font, size, color, a, b, c, d, x, y);

    [Fact]
    public void Hash_Has_No_Text_In_It() =>
        Assert.DoesNotContain(typeof(TextBlockHash.Piece).GetProperties(), p => p.Name.Contains("Text", StringComparison.Ordinal));

    [Fact]
    public void Hash_Covers_Font_Orientation_Place_And_Order()
    {
        var baseline = TextBlockHash.Compute([Piece(), Piece("0:5", x: 40)]);

        Assert.Equal(baseline, TextBlockHash.Compute([Piece("0:5", x: 40), Piece()]));   // порядок объектов не важен
        Assert.NotEqual(baseline, TextBlockHash.Compute([Piece(font: "Arial"), Piece("0:5", x: 40)]));
        Assert.NotEqual(baseline, TextBlockHash.Compute([Piece(b: 0.2), Piece("0:5", x: 40)]));
        Assert.NotEqual(baseline, TextBlockHash.Compute([Piece(x: 10.5), Piece("0:5", x: 40)]));
        Assert.NotEqual(baseline, TextBlockHash.Compute([Piece("0:5"), Piece("0:0", x: 40)]));   // куски поменяли местами
        Assert.Equal(baseline, TextBlockHash.Compute([Piece(x: 10.0000004, a: 0.9999998), Piece("0:5", x: 40)]));
    }
}
