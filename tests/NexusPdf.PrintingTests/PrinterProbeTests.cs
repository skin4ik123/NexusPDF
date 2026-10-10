using System.Diagnostics;
using NexusPdf.Printing.Windows;
using NexusPdf.Printing;
using Xunit.Abstractions;

namespace NexusPdf.PrintingTests;

/// <summary>
/// Читаются ли НАСТОЯЩИЕ принтеры этой машины через winspool. Тест печатает
/// то, что сообщил драйвер: пустые списки здесь — не «сломано», а честный
/// ответ конкретного устройства, и это тоже надо видеть.
///
/// Отдельно проверяется ВРЕМЯ: список имён обязан приходить мгновенно — его
/// медленность и была жалобой «окно печати не открывается» на Windows 10.
/// </summary>
public sealed class PrinterProbeTests
{
    private readonly ITestOutputHelper _output;
    public PrinterProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Names_Arrive_Instantly()
    {
        var service = new WindowsPrinterService();
        var watch = Stopwatch.StartNew();
        var names = service.DiscoverNames();
        watch.Stop();

        _output.WriteLine($"принтеров: {names.Count} за {watch.ElapsedMilliseconds} мс");
        foreach (var p in names)
            _output.WriteLine($"  {p.Name}{(p.IsDefault ? "  [по умолчанию]" : "")}" +
                              $"{(p.IsWorkOffline ? "  [автономно]" : "")}{(p.IsNetwork ? "  [сеть]" : "")}");

        Assert.NotEmpty(names);
        // Имена идут из локального диспетчера без похода к принтерам: даже на
        // медленной машине это десятки миллисекунд. Секунда — уже регресс к
        // старому поведению.
        Assert.True(watch.ElapsedMilliseconds < 1000,
            $"Список имён занял {watch.ElapsedMilliseconds} мс — это снова блокирующий опрос.");
    }

    [Fact]
    public async Task Capabilities_Of_A_Local_Queue_Are_Read()
    {
        var service = new WindowsPrinterService();
        var names = service.DiscoverNames();
        Assert.NotEmpty(names);

        foreach (var summary in names)
        {
            var caps = await service.ReadAsync(
                summary.Name, WindowsPrinterService.DefaultTimeout, CancellationToken.None);
            if (caps == null)
            {
                _output.WriteLine($"=== {summary.Name}: не ответил за таймаут");
                continue;
            }

            _output.WriteLine("");
            _output.WriteLine($"=== {caps.PrinterName}{(caps.IsDefault ? "  [по умолчанию]" : "")}");
            _output.WriteLine($"    драйвер: {caps.DriverName}");
            _output.WriteLine($"    порт: {caps.PortName}, подключение: {caps.Connection}, состояние: {caps.State}");
            _output.WriteLine($"    виртуальный: {caps.IsVirtual}");
            _output.WriteLine($"    цвет: {caps.SupportsColor}, дуплекс: {caps.SupportsAnyDuplex}, " +
                              $"сортировка: {caps.SupportsCollation}, макс. копий: {caps.MaxCopies}");
            _output.WriteLine($"    разрешения: {(caps.ResolutionsDpi.Count == 0 ? "не сообщены" : string.Join(", ", caps.ResolutionsDpi))}");
            _output.WriteLine($"    лотки: {(caps.PaperSources.Count == 0 ? "не сообщены" : string.Join(", ", caps.PaperSources.Select(s => s.Name)))}");
            _output.WriteLine($"    форматов бумаги: {caps.PaperSizes.Count}");
            foreach (var paper in caps.PaperSizes.Take(5))
                _output.WriteLine($"      {paper.Name,-20} {paper.SizePt.WidthPt:F1}×{paper.SizePt.HeightPt:F1} пт (код {paper.DriverValue})");

            // Хоть один формат у настоящей очереди быть обязан, и размер A4,
            // если он есть, обязан быть настоящим размером A4 от драйвера.
            Assert.NotEmpty(caps.PaperSizes);
            var a4 = caps.PaperSizes.FirstOrDefault(p => p.DriverValue == "9");
            if (a4 != null)
            {
                Assert.InRange(a4.SizePt.WidthPt, 590, 600);   // 595.3 пт
                Assert.InRange(a4.SizePt.HeightPt, 836, 847);  // 841.9 пт
            }
        }
    }

    [Fact]
    public async Task Hard_Margins_Come_From_The_Driver_For_One_Format()
    {
        var service = new WindowsPrinterService();
        var names = service.DiscoverNames();
        var target = names.FirstOrDefault(n =>
            n.Name.Contains("PDF", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        if (target == null) return;

        var caps = await service.ReadAsync(
            target.Name, WindowsPrinterService.DefaultTimeout, CancellationToken.None);
        if (caps == null || caps.PaperSizes.Count == 0) return;

        var paper = caps.PaperSizes.FirstOrDefault(p => p.DriverValue == "9") ?? caps.PaperSizes[0];
        var margins = await service.ReadHardMarginsAsync(
            target.Name, paper, WindowsPrinterService.DefaultTimeout, CancellationToken.None);

        _output.WriteLine($"{target.Name}, {paper.Name}: поля {margins?.LeftPt:F1}/{margins?.TopPt:F1}/{margins?.RightPt:F1}/{margins?.BottomPt:F1} пт");
        Assert.NotNull(margins);
        // Поля не могут съедать больше трети листа — иначе это мусор чтения.
        Assert.InRange(margins!.Value.LeftPt, 0, paper.SizePt.WidthPt / 3);
        Assert.InRange(margins.Value.TopPt, 0, paper.SizePt.HeightPt / 3);
    }

    [Fact]
    public void Queue_Existence_Is_A_Cheap_Check()
    {
        var names = new WindowsPrinterService().DiscoverNames();
        if (names.Count > 0)
            Assert.True(WindowsPrinterService.QueueExists(names[0].Name));
        Assert.False(WindowsPrinterService.QueueExists("Несуществующий принтер NexusPDF"));
    }
}
