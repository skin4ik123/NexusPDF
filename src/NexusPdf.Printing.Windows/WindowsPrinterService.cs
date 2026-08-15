using System.Printing;
using NexusPdf.Printing;

namespace NexusPdf.Printing.Windows;

/// <summary>
/// Обнаружение принтеров и чтение их РЕАЛЬНЫХ возможностей через System.Printing.
/// Всё, что драйвер не сообщил, остаётся пустым: выдуманный список форматов
/// или несуществующий дуплекс хуже, чем честное «принтер об этом не сказал».
/// </summary>
public sealed class WindowsPrinterService : IDisposable
{
    private readonly PrintServer _server = new();
    private bool _disposed;

    /// <summary>Очереди печати вместе с состоянием. Недоступный сервер печати не должен ронять окно.</summary>
    public IReadOnlyList<PrinterCapabilities> Discover()
    {
        var result = new List<PrinterCapabilities>();
        string? defaultName = null;
        try
        {
            defaultName = LocalPrintServer.GetDefaultPrintQueue()?.FullName;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Принтер по умолчанию не определён");
        }

        try
        {
            foreach (var queue in _server.GetPrintQueues(new[]
                     {
                         EnumeratedPrintQueueTypes.Local,
                         EnumeratedPrintQueueTypes.Connections,
                     }))
            {
                using (queue)
                {
                    try
                    {
                        result.Add(ReadCapabilities(queue, queue.FullName == defaultName));
                    }
                    catch (Exception ex)
                    {
                        // Один сломанный драйвер не должен прятать остальные принтеры.
                        Serilog.Log.Warning(ex, "Не удалось прочитать возможности принтера {Printer}", queue.Name);
                        result.Add(new PrinterCapabilities
                        {
                            PrinterName = queue.FullName,
                            State = PrinterState.Unknown,
                            IsDefault = queue.FullName == defaultName,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось получить список принтеров");
        }

        return result
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.PrinterName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Свежие возможности одного принтера по имени: список мог устареть.</summary>
    public PrinterCapabilities? Read(string printerName)
    {
        try
        {
            using var queue = new PrintQueue(_server, printerName);
            var defaultName = LocalPrintServer.GetDefaultPrintQueue()?.FullName;
            return ReadCapabilities(queue, queue.FullName == defaultName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Принтер {Printer} недоступен", printerName);
            return null;
        }
    }

    private static PrinterCapabilities ReadCapabilities(PrintQueue queue, bool isDefault)
    {
        queue.Refresh();
        var caps = queue.GetPrintCapabilities();

        var papers = ReadPaperSizes(caps, queue);
        var margins = ReadHardMargins(queue, papers);

        return new PrinterCapabilities
        {
            PrinterName = queue.FullName,
            DriverName = SafeGet(() => queue.QueueDriver?.Name),
            Location = SafeGet(() => queue.Location),
            PortName = SafeGet(() => queue.QueuePort?.Name),
            Connection = DetectConnection(queue),
            State = DetectState(queue),
            IsDefault = isDefault,
            IsVirtual = IsVirtualQueue(queue),

            PaperSizes = papers,
            PaperSources = caps.InputBinCapability
                .Select(b => new PaperSourceOption(DescribeInputBin(b), b.ToString()))
                .ToList(),
            OutputBins = Array.Empty<OutputBinOption>(),
            MediaTypes = caps.PageMediaTypeCapability
                .Select(t => new MediaTypeOption(DescribeMediaType(t), t.ToString()))
                .ToList(),
            ResolutionsDpi = caps.PageResolutionCapability
                .Where(r => r.X.HasValue)
                .Select(r => r.X!.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToList(),

            SupportsColor = caps.OutputColorCapability.Contains(OutputColor.Color),
            SupportsMonochrome = caps.OutputColorCapability.Contains(OutputColor.Monochrome)
                                 || caps.OutputColorCapability.Contains(OutputColor.Grayscale)
                                 || caps.OutputColorCapability.Count == 0,
            SupportsDuplexLongEdge = caps.DuplexingCapability.Contains(Duplexing.TwoSidedLongEdge),
            SupportsDuplexShortEdge = caps.DuplexingCapability.Contains(Duplexing.TwoSidedShortEdge),
            SupportsCollation = caps.CollationCapability.Contains(Collation.Collated),
            SupportsStapling = caps.StaplingCapability.Any(s => s != Stapling.None),
            SupportsBooklet = false, // аппаратный буклет System.Printing не сообщает
            SupportsBorderless = false,
            MaxCopies = caps.MaxCopyCount ?? 1,

            HardMarginsByPaper = margins,
        };
    }

    /// <summary>
    /// Размеры бумаги. System.Printing перечисляет ИМЕНА форматов, а физические
    /// размеры даёт только для выбранного; поэтому размеры известных форматов
    /// берутся из таблицы ISO/ANSI, а неизвестные пропускаются, а не выдумываются.
    /// </summary>
    private static IReadOnlyList<PaperSizeOption> ReadPaperSizes(PrintCapabilities caps, PrintQueue queue)
    {
        var result = new List<PaperSizeOption>();
        foreach (var media in caps.PageMediaSizeCapability)
        {
            if (media.PageMediaSizeName is not { } name) continue;

            // Драйвер иногда сам сообщает размер в DIU — это точнее таблицы.
            if (media.Width is { } w && media.Height is { } h && w > 1 && h > 1)
            {
                result.Add(new PaperSizeOption(
                    DescribePaper(name),
                    new SizePt(Units.DiuToPoints(w), Units.DiuToPoints(h)),
                    name.ToString()));
                continue;
            }

            if (PaperSizeTable.TryGet(name, out var size))
                result.Add(new PaperSizeOption(DescribePaper(name), size, name.ToString()));
        }

        if (result.Count == 0)
        {
            // Драйвер не перечислил ничего — берём то, что стоит в очереди сейчас.
            var current = SafeGet(() => queue.DefaultPrintTicket?.PageMediaSize);
            if (current?.Width is { } cw && current.Height is { } ch)
                result.Add(new PaperSizeOption("Текущий формат",
                    new SizePt(Units.DiuToPoints(cw), Units.DiuToPoints(ch))));
        }

        return result
            .GroupBy(p => p.Name)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Непечатаемые поля. Драйвер отдаёт их через PageImageableArea выбранного
    /// PrintTicket, поэтому для каждого формата запрашиваем отдельно — общей
    /// величины «на принтер» не существует.
    /// </summary>
    private static IReadOnlyDictionary<string, MarginsPt> ReadHardMargins(
        PrintQueue queue, IReadOnlyList<PaperSizeOption> papers)
    {
        var result = new Dictionary<string, MarginsPt>();
        foreach (var paper in papers)
        {
            try
            {
                var ticket = queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
                if (paper.DriverValue != null &&
                    Enum.TryParse<PageMediaSizeName>(paper.DriverValue, out var name))
                    ticket.PageMediaSize = new PageMediaSize(name);

                var caps = queue.GetPrintCapabilities(ticket);
                var area = caps.PageImageableArea;
                if (area == null) continue;

                var left = Units.DiuToPoints(area.OriginWidth);
                var top = Units.DiuToPoints(area.OriginHeight);
                var right = Math.Max(0, paper.SizePt.WidthPt - left - Units.DiuToPoints(area.ExtentWidth));
                var bottom = Math.Max(0, paper.SizePt.HeightPt - top - Units.DiuToPoints(area.ExtentHeight));
                result[paper.Name] = new MarginsPt(left, top, right, bottom);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Поля для формата {Paper} не получены", paper.Name);
            }
        }
        return result;
    }

    private static PrinterState DetectState(PrintQueue queue)
    {
        try
        {
            // Порядок важен: сначала то, что требует вмешательства человека.
            var status = queue.QueueStatus;
            if (status.HasFlag(PrintQueueStatus.PaperJam)) return PrinterState.PaperJam;
            if (status.HasFlag(PrintQueueStatus.PaperOut)) return PrinterState.PaperOut;
            if (status.HasFlag(PrintQueueStatus.DoorOpen)) return PrinterState.DoorOpen;
            if (status.HasFlag(PrintQueueStatus.TonerLow)) return PrinterState.TonerLow;
            if (status.HasFlag(PrintQueueStatus.Error)) return PrinterState.Error;
            if (status.HasFlag(PrintQueueStatus.Offline)) return PrinterState.Offline;
            if (status.HasFlag(PrintQueueStatus.Paused)) return PrinterState.Paused;
            if (status.HasFlag(PrintQueueStatus.Printing)) return PrinterState.Printing;
            if (status.HasFlag(PrintQueueStatus.Busy)) return PrinterState.Busy;
            return status == PrintQueueStatus.None ? PrinterState.Ready : PrinterState.Unknown;
        }
        catch
        {
            return PrinterState.Unknown;
        }
    }

    private static PrinterConnection DetectConnection(PrintQueue queue)
    {
        try
        {
            if (IsVirtualQueue(queue)) return PrinterConnection.Virtual;
            var port = queue.QueuePort?.Name ?? "";
            if (port.StartsWith("TS", StringComparison.OrdinalIgnoreCase) ||
                queue.Name.Contains("redirected", StringComparison.OrdinalIgnoreCase))
                return PrinterConnection.RemoteDesktop;
            if (queue.IsShared || port.StartsWith(@"\\", StringComparison.Ordinal) ||
                port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
                port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
                return PrinterConnection.Network;
            return PrinterConnection.Local;
        }
        catch
        {
            return PrinterConnection.Unknown;
        }
    }

    /// <summary>Виртуальный принтер определяется по порту вывода в файл, а не по названию.</summary>
    private static bool IsVirtualQueue(PrintQueue queue)
    {
        try
        {
            var port = queue.QueuePort?.Name ?? "";
            return port.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase)
                || port.StartsWith("SHRFAX", StringComparison.OrdinalIgnoreCase)
                || port.Equals("FILE:", StringComparison.OrdinalIgnoreCase)
                || port.StartsWith("XPSPort", StringComparison.OrdinalIgnoreCase)
                || port.StartsWith("nul", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static T? SafeGet<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }

    private static string DescribePaper(PageMediaSizeName name) => name switch
    {
        PageMediaSizeName.ISOA3 => "A3",
        PageMediaSizeName.ISOA4 => "A4",
        PageMediaSizeName.ISOA5 => "A5",
        PageMediaSizeName.ISOA6 => "A6",
        PageMediaSizeName.ISOB5Envelope => "B5 конверт",
        PageMediaSizeName.NorthAmericaLetter => "Letter",
        PageMediaSizeName.NorthAmericaLegal => "Legal",
        PageMediaSizeName.NorthAmericaTabloid => "Tabloid",
        PageMediaSizeName.NorthAmericaExecutive => "Executive",
        _ => name.ToString(),
    };

    private static string DescribeInputBin(InputBin bin) => bin switch
    {
        InputBin.AutoSelect => "Автовыбор",
        InputBin.Cassette => "Лоток",
        InputBin.Manual => "Ручная подача",
        InputBin.Tractor => "Тракторная подача",
        InputBin.AutoSheetFeeder => "Автоподатчик",
        _ => bin.ToString(),
    };

    /// <summary>
    /// Названия типов носителя на русском. Перечисление WPF беднее списка IPP:
    /// «плотная», «тонкая» и «переработанная» в нём отсутствуют, поэтому
    /// выдуманных пунктов здесь нет — что драйвер прислал, то и показываем.
    /// </summary>
    private static string DescribeMediaType(PageMediaType type) => type switch
    {
        PageMediaType.Plain => "Обычная бумага",
        PageMediaType.Stationery => "Писчая бумага",
        PageMediaType.Bond => "Документная бумага",
        PageMediaType.Archival => "Архивная бумага",
        PageMediaType.Photographic => "Фотобумага",
        PageMediaType.PhotographicGlossy => "Фотобумага глянцевая",
        PageMediaType.PhotographicHighGloss => "Фотобумага сверхглянцевая",
        PageMediaType.PhotographicMatte => "Фотобумага матовая",
        PageMediaType.PhotographicSatin => "Фотобумага сатиновая",
        PageMediaType.PhotographicSemiGloss => "Фотобумага полуглянцевая",
        PageMediaType.PhotographicFilm => "Фотоплёнка",
        PageMediaType.Transparency => "Плёнка",
        PageMediaType.EnvelopePlain => "Конверт",
        PageMediaType.EnvelopeWindow => "Конверт с окном",
        PageMediaType.Label => "Этикетки",
        PageMediaType.CardStock => "Картон",
        PageMediaType.Continuous => "Рулонная подача",
        PageMediaType.HighResolution => "Бумага высокого разрешения",
        PageMediaType.TShirtTransfer => "Термоперенос",
        PageMediaType.AutoSelect => "Автовыбор",
        _ => type.ToString(),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.Dispose();
    }
}
