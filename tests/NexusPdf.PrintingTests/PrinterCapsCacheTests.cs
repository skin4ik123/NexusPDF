using System.IO;
using NexusPdf.Printing;
using NexusPdf.Printing.Windows;

namespace NexusPdf.PrintingTests;

/// <summary>
/// Дисковый кэш возможностей: именно он делает ВТОРОЕ открытие окна печати
/// мгновенным на машинах, где драйвер отвечает секунды.
/// </summary>
public sealed class PrinterCapsCacheTests
{
    private static string TempPath() => Path.Combine(
        Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"), "caps.json");

    private static PrinterCapabilities Sample(string name) => PrinterCapabilities.Unknown(name) with
    {
        DriverName = "Тестовый драйвер",
        PaperSizes = new[] { new PaperSizeOption("A4", new SizePt(595.3, 841.9), "9") },
        SupportsColor = true,
        MaxCopies = 42,
        HardMarginsByPaper = new Dictionary<string, MarginsPt> { ["A4"] = new(10, 11, 12, 13) },
    };

    [Fact]
    public void Survives_A_Round_Trip_With_Margins()
    {
        var path = TempPath();
        new PrinterCapsCache(path).Store(Sample("HP"));

        var reloaded = new PrinterCapsCache(path).TryGet("HP");
        Assert.NotNull(reloaded);
        Assert.Equal(42, reloaded!.MaxCopies);
        Assert.True(reloaded.SupportsColor);
        var paper = Assert.Single(reloaded.PaperSizes);
        Assert.Equal("9", paper.DriverValue);
        Assert.Equal(11, reloaded.HardMarginsFor(paper).TopPt, 3);
    }

    [Fact]
    public void Default_Paper_Survives_A_Round_Trip()
    {
        var path = TempPath();
        new PrinterCapsCache(path).Store(Sample("HP") with { DefaultPaperDriverValue = "1" });
        Assert.Equal("1", new PrinterCapsCache(path).TryGet("HP")!.DefaultPaperDriverValue);
    }

    [Fact]
    public void Paperless_Answer_Is_Neither_Stored_Nor_Returned()
    {
        // Ответ драйвера без форматов — сбой чтения. Прежние версии его
        // кэшировали на две недели, и окно печати на такой записи уходило в
        // бесконечный повторный опрос и падало.
        var path = TempPath();
        var cache = new PrinterCapsCache(path);
        cache.Store(Sample("Broken") with { PaperSizes = Array.Empty<PaperSizeOption>() });
        Assert.Null(new PrinterCapsCache(path).TryGet("Broken"));

        // Запись прежней версии без форматов тоже не отдаётся.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"Old\":{\"Stored\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\",\"Papers\":[]}}");
        Assert.Null(new PrinterCapsCache(path).TryGet("Old"));
    }

    [Fact]
    public void Expired_Entries_Are_Not_Returned()
    {
        var path = TempPath();
        new PrinterCapsCache(path, maxAge: TimeSpan.FromDays(14)).Store(Sample("Old"));
        // Нулевой срок жизни: всё записанное уже устарело.
        Assert.Null(new PrinterCapsCache(path, maxAge: TimeSpan.Zero).TryGet("Old"));
    }

    [Fact]
    public void A_Broken_File_Means_An_Empty_Cache_Not_A_Crash()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ это не json");
        Assert.Null(new PrinterCapsCache(path).TryGet("Any"));
    }

    [Fact]
    public void Unknown_Printer_Is_A_Miss()
    {
        Assert.Null(new PrinterCapsCache(TempPath()).TryGet("Нет такого"));
    }
}
