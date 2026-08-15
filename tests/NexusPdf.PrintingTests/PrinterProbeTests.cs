using NexusPdf.Printing.Windows;
using NexusPdf.Printing;
using Xunit.Abstractions;

namespace NexusPdf.PrintingTests;

/// <summary>
/// Читаются ли возможности НАСТОЯЩИХ принтеров этой машины. Тест печатает
/// то, что сообщил драйвер: пустые списки здесь — не «сломано», а честный
/// ответ конкретного устройства, и это тоже надо видеть.
/// </summary>
public sealed class PrinterProbeTests
{
    private readonly ITestOutputHelper _output;
    public PrinterProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Probe()
    {
        using var service = new WindowsPrinterService();
        var printers = service.Discover();
        _output.WriteLine($"принтеров найдено: {printers.Count}");

        foreach (var p in printers)
        {
            _output.WriteLine("");
            _output.WriteLine($"=== {p.PrinterName}{(p.IsDefault ? "  [по умолчанию]" : "")}");
            _output.WriteLine($"    драйвер: {p.DriverName}");
            _output.WriteLine($"    порт: {p.PortName}, подключение: {p.Connection}, состояние: {p.State}");
            _output.WriteLine($"    виртуальный: {p.IsVirtual}");
            _output.WriteLine($"    цвет: {p.SupportsColor}, моно: {p.SupportsMonochrome}");
            _output.WriteLine($"    duplex длинный край: {p.SupportsDuplexLongEdge}, короткий: {p.SupportsDuplexShortEdge}");
            _output.WriteLine($"    сортировка: {p.SupportsCollation}, скрепление: {p.SupportsStapling}, макс. копий: {p.MaxCopies}");
            _output.WriteLine($"    разрешения: {(p.ResolutionsDpi.Count == 0 ? "не сообщены" : string.Join(", ", p.ResolutionsDpi))}");
            _output.WriteLine($"    лотки: {(p.PaperSources.Count == 0 ? "не сообщены" : string.Join(", ", p.PaperSources.Select(s => s.Name)))}");
            _output.WriteLine($"    типы носителя: {(p.MediaTypes.Count == 0 ? "не сообщены" : string.Join(", ", p.MediaTypes.Take(6).Select(m => m.Name)))}");
            _output.WriteLine($"    форматов бумаги: {p.PaperSizes.Count}");
            foreach (var paper in p.PaperSizes.Take(5))
            {
                var margins = p.HardMarginsFor(paper);
                _output.WriteLine($"      {paper.Name,-16} {paper.SizePt}  поля Л{margins.LeftPt:F1} В{margins.TopPt:F1} П{margins.RightPt:F1} Н{margins.BottomPt:F1} пт");
            }
        }

        Assert.NotEmpty(printers);
    }
}
