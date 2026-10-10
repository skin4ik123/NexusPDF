using NexusPdf.Application;
using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Пакетная печать подгоняет профиль под принтер по тем же правилам, что и
/// центр печати: иначе один и тот же профиль печатал бы по-разному.
/// </summary>
public sealed class BatchPrintFitTests
{
    private static PrinterCapabilities Printer(bool color, bool duplex) => new()
    {
        PrinterName = "Тест",
        PaperSizes = new[] { new PaperSizeOption("A4", new SizePt(595.28, 841.89), "9") },
        SupportsColor = color,
        SupportsDuplexLongEdge = duplex,
        SupportsDuplexShortEdge = duplex,
    };

    [Fact]
    public void Booklet_Always_Flips_On_The_Short_Edge()
    {
        var settings = new LayoutSettings { Imposition = ImpositionMode.Booklet, Duplex = DuplexMode.LongEdge };
        var fitted = BatchPrintService.FitToPrinter(settings, Printer(color: true, duplex: true));
        Assert.Equal(DuplexMode.ShortEdge, fitted.Duplex);
    }

    [Fact]
    public void Colour_Becomes_Grey_Only_On_A_Known_Black_And_White_Printer()
    {
        var settings = new LayoutSettings { Color = ColorMode.Color };
        Assert.Equal(ColorMode.Grayscale,
            BatchPrintService.FitToPrinter(settings, Printer(color: false, duplex: true)).Color);
        Assert.Equal(ColorMode.Color,
            BatchPrintService.FitToPrinter(settings, Printer(color: true, duplex: true)).Color);

        // Возможности не прочитаны — «неизвестно» не значит «не умеет».
        Assert.Equal(ColorMode.Color,
            BatchPrintService.FitToPrinter(settings, PrinterCapabilities.Unknown("Ещё не опрошен")).Color);
    }

    [Fact]
    public void Two_Sided_On_A_Simplex_Printer_Prints_One_Sided_In_A_Batch()
    {
        // Ручной дуплекс в пакете невозможен: он требует человека у лотка
        // после каждого файла.
        var fitted = BatchPrintService.FitToPrinter(
            new LayoutSettings { Duplex = DuplexMode.LongEdge }, Printer(color: true, duplex: false));
        Assert.Equal(DuplexMode.Simplex, fitted.Duplex);

        var manual = BatchPrintService.FitToPrinter(
            new LayoutSettings { Duplex = DuplexMode.Manual }, Printer(color: true, duplex: true));
        Assert.Equal(DuplexMode.Simplex, manual.Duplex);
    }
}
