using System.Runtime.InteropServices;
using System.Text;
using NexusPdf.Printing;

namespace NexusPdf.Printing.Windows;

/// <summary>Строка списка принтеров: то, что известно мгновенно, без опроса самого принтера.</summary>
public sealed record PrinterSummary(string Name, bool IsDefault, bool IsWorkOffline, bool IsNetwork);

/// <summary>
/// Обнаружение принтеров и чтение их РЕАЛЬНЫХ возможностей через winspool.
///
/// Устройство двухфазное, и это главное решение файла:
///  1) <see cref="DiscoverNames"/> — список имён из локального диспетчера
///     печати. Ни одного обращения к самим принтерам, миллисекунды даже при
///     мёртвых сетевых очередях.
///  2) <see cref="ReadAsync"/> — возможности ОДНОГО принтера, когда он выбран.
///     Этот вызов уже может уходить по сети, поэтому он асинхронный и живёт
///     под таймаутом вызывающего.
///
/// Прежняя версия на System.Printing читала возможности всех принтеров разом,
/// и для каждого — поля всех форматов бумаги отдельными запросами к драйверу.
/// На Windows 10 с сетевым принтером это держало окно печати закрытым до
/// минуты; поведение прожило три выпуска, потому что на машине разработчика
/// принтеры локальные. Всё, что драйвер не сообщил, по-прежнему остаётся
/// пустым: выдуманный список форматов хуже честного «принтер не ответил».
/// </summary>
public sealed class WindowsPrinterService
{
    /// <summary>Разумный предел ожидания драйвера: дольше — показываем «не отвечает».</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    // ----- Фаза 1: список имён -----

    public IReadOnlyList<PrinterSummary> DiscoverNames()
    {
        var result = new List<PrinterSummary>();
        var defaultName = ReadDefaultPrinterName();

        var flags = SpoolerInterop.PrinterEnumLocal | SpoolerInterop.PrinterEnumConnections;
        SpoolerInterop.EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return result;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!SpoolerInterop.EnumPrinters(flags, null, 4, buffer, needed, out _, out var count))
                return result;

            var size = Marshal.SizeOf<SpoolerInterop.PrinterInfo4>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SpoolerInterop.PrinterInfo4>(buffer + i * size);
                var name = Marshal.PtrToStringUni(info.PrinterName);
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(new PrinterSummary(
                    name,
                    IsDefault: string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    IsWorkOffline: (info.Attributes & SpoolerInterop.AttributeWorkOffline) != 0,
                    IsNetwork: (info.Attributes & SpoolerInterop.AttributeNetwork) != 0));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось получить список принтеров");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Строка списка до опроса возможностей: имя и то, что известно из
    /// диспетчера мгновенно. Форматы бумаги пустые — их дочитает ReadAsync.
    /// </summary>
    public static PrinterCapabilities Skeleton(PrinterSummary summary) =>
        PrinterCapabilities.Unknown(summary.Name) with
        {
            IsDefault = summary.IsDefault,
            State = summary.IsWorkOffline ? PrinterState.Offline : PrinterState.Unknown,
            Connection = summary.IsNetwork ? PrinterConnection.Network : PrinterConnection.Unknown,
        };

    private static string? ReadDefaultPrinterName()
    {
        try
        {
            uint size = 0;
            SpoolerInterop.GetDefaultPrinter(new StringBuilder(0), ref size);
            if (size == 0) return null;
            var sb = new StringBuilder((int)size);
            return SpoolerInterop.GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Принтер по умолчанию не определён");
            return null;
        }
    }

    // ----- Фаза 2: возможности одного принтера -----

    /// <summary>
    /// Возможности принтера с ограничением по времени. null — принтер не
    /// ответил за отведённое время или недоступен; сам опрос при этом не
    /// прерывается (winspool не умеет отмену), просто его результат уже
    /// никому не нужен.
    /// </summary>
    public async Task<PrinterCapabilities?> ReadAsync(
        string printerName, TimeSpan timeout, CancellationToken ct)
    {
        var work = Task.Run(() => ReadCore(printerName), CancellationToken.None);
        var finished = await Task.WhenAny(work, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (finished != work)
        {
            Serilog.Log.Warning("Принтер {Printer} не ответил за {Timeout} — окно работает без него",
                printerName, timeout);
            return null;
        }
        return await work.ConfigureAwait(false);
    }

    /// <summary>
    /// Опрос без ограничения по времени — для окна, которое само решает, когда
    /// сказать «принтер не отвечает», но готово принять и поздний ответ:
    /// медленная сетевая очередь, ответившая через десять секунд, всё равно
    /// получит свои форматы, а не останется на стандартных до закрытия окна.
    /// </summary>
    public Task<PrinterCapabilities?> ReadInBackground(string printerName) =>
        Task.Run(() => ReadCore(printerName));

    /// <summary>Синхронное чтение: только для вызова с фонового потока.</summary>
    private static PrinterCapabilities? ReadCore(string printerName)
    {
        try
        {
            var (status, attributes, port, driver, location, defaultPaper) = ReadQueueInfo(printerName);

            var papers = ReadPaperSizes(printerName, port);
            var duplex = DeviceCapability(printerName, port, SpoolerInterop.DcDuplex);
            var color = DeviceCapability(printerName, port, SpoolerInterop.DcColorDevice);
            var collate = DeviceCapability(printerName, port, SpoolerInterop.DcCollate);
            var copies = DeviceCapability(printerName, port, SpoolerInterop.DcCopies);

            return new PrinterCapabilities
            {
                PrinterName = printerName,
                DriverName = driver,
                Location = location,
                PortName = port,
                Connection = DetectConnection(attributes, port),
                State = MapState(status, attributes),
                IsDefault = string.Equals(printerName, ReadDefaultPrinterName(), StringComparison.OrdinalIgnoreCase),
                IsVirtual = IsVirtualPort(port),

                PaperSizes = papers,
                DefaultPaperDriverValue = defaultPaper,
                PaperSources = ReadBins(printerName, port),
                MediaTypes = ReadMediaTypes(printerName, port),
                ResolutionsDpi = ReadResolutions(printerName, port),

                SupportsColor = color == 1,
                SupportsMonochrome = true,
                SupportsDuplexLongEdge = duplex == 1,
                SupportsDuplexShortEdge = duplex == 1,
                SupportsCollation = collate == 1,
                SupportsStapling = false, // winspool про степлер не сообщает; выдумывать нельзя
                SupportsBooklet = false,
                SupportsBorderless = false,
                MaxCopies = Math.Max(1, copies),

                // Поля дочитываются лениво для выбранного формата: один формат —
                // одно обращение к драйверу, а не все сразу при открытии окна.
                HardMarginsByPaper = new Dictionary<string, MarginsPt>(),
            };
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Принтер {Printer} недоступен", printerName);
            return null;
        }
    }

    // ----- Непечатаемые поля: лениво, для одного формата -----

    /// <summary>
    /// Поля выбранного формата через информационный контекст GDI. null —
    /// драйвер не ответил; план тогда считает печатаемой всю страницу, что
    /// честнее выдуманных «стандартных 5 мм».
    /// </summary>
    public async Task<MarginsPt?> ReadHardMarginsAsync(
        string printerName, PaperSizeOption paper, TimeSpan timeout, CancellationToken ct)
    {
        var work = Task.Run(() => ReadHardMarginsCore(printerName, paper), CancellationToken.None);
        var finished = await Task.WhenAny(work, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return finished == work ? await work.ConfigureAwait(false) : null;
    }

    private static MarginsPt? ReadHardMarginsCore(string printerName, PaperSizeOption paper)
    {
        var devMode = new SpoolerInterop.DevMode
        {
            DeviceName = printerName.Length > 31 ? printerName[..31] : printerName,
            Size = (ushort)Marshal.SizeOf<SpoolerInterop.DevMode>(),
            SpecVersion = 0x0401,
        };

        if (paper.DriverValue != null && short.TryParse(paper.DriverValue, out var dmPaper))
        {
            devMode.Fields = SpoolerInterop.DmPaperSizeField;
            devMode.PaperSize = dmPaper;
        }
        else
        {
            // Формат без кода драйвера задаётся размерами в 0,1 мм.
            devMode.Fields = SpoolerInterop.DmPaperLengthField | SpoolerInterop.DmPaperWidthField;
            devMode.PaperWidth = (short)Math.Round(paper.SizePt.WidthPt / 72.0 * 254.0);
            devMode.PaperLength = (short)Math.Round(paper.SizePt.HeightPt / 72.0 * 254.0);
        }

        var devModePtr = Marshal.AllocHGlobal(devMode.Size);
        var hdc = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(devMode, devModePtr, false);
            hdc = SpoolerInterop.CreateIC("WINSPOOL", printerName, null, devModePtr);
            if (hdc == IntPtr.Zero) return null;

            double dpiX = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.LogPixelsX);
            double dpiY = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.LogPixelsY);
            if (dpiX <= 0 || dpiY <= 0) return null;

            double physW = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.PhysicalWidth);
            double physH = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.PhysicalHeight);
            double offX = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.PhysicalOffsetX);
            double offY = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.PhysicalOffsetY);
            double resW = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.HorzRes);
            double resH = SpoolerInterop.GetDeviceCaps(hdc, SpoolerInterop.VertRes);
            if (physW <= 0 || physH <= 0 || resW <= 0 || resH <= 0) return null;

            var left = offX / dpiX * 72.0;
            var top = offY / dpiY * 72.0;
            var right = Math.Max(0, (physW - offX - resW) / dpiX * 72.0);
            var bottom = Math.Max(0, (physH - offY - resH) / dpiY * 72.0);
            return new MarginsPt(left, top, right, bottom);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Поля для формата {Paper} не получены", paper.Name);
            return null;
        }
        finally
        {
            if (hdc != IntPtr.Zero) SpoolerInterop.DeleteDC(hdc);
            Marshal.FreeHGlobal(devModePtr);
        }
    }

    // ----- Отправка: лёгкая проверка существования очереди -----

    /// <summary>Существует ли очередь. Открытие локального диспетчера, без похода к принтеру.</summary>
    public static bool QueueExists(string printerName)
    {
        if (!SpoolerInterop.OpenPrinter(printerName, out var handle, IntPtr.Zero))
            return false;
        SpoolerInterop.ClosePrinter(handle);
        return true;
    }

    // ----- Чтение словарей DeviceCapabilities -----

    private static IReadOnlyList<PaperSizeOption> ReadPaperSizes(string printer, string? port)
    {
        var codes = ReadUShorts(printer, port, SpoolerInterop.DcPapers);
        var names = ReadStrings(printer, port, SpoolerInterop.DcPaperNames, 64);
        var sizes = ReadPoints(printer, port, SpoolerInterop.DcPaperSize);

        var count = Math.Min(codes.Count, Math.Min(names.Count, sizes.Count));
        var result = new List<PaperSizeOption>(count);
        for (var i = 0; i < count; i++)
        {
            // Размер приходит в 0,1 мм; нулевые и отрицательные — мусор драйвера.
            var wPt = sizes[i].X / 254.0 * 72.0;
            var hPt = sizes[i].Y / 254.0 * 72.0;
            if (wPt < 36 || hPt < 36) continue;
            var name = string.IsNullOrWhiteSpace(names[i]) ? $"{codes[i]}" : names[i];
            result.Add(new PaperSizeOption(name, new SizePt(wPt, hPt), codes[i].ToString()));
        }

        return result.GroupBy(p => p.Name).Select(g => g.First()).ToList();
    }

    private static IReadOnlyList<PaperSourceOption> ReadBins(string printer, string? port)
    {
        var names = ReadStrings(printer, port, SpoolerInterop.DcBinNames, 24);
        var codes = ReadUShorts(printer, port, SpoolerInterop.DcBins);
        var count = Math.Min(names.Count, codes.Count);
        var result = new List<PaperSourceOption>(count);
        for (var i = 0; i < count; i++)
            if (!string.IsNullOrWhiteSpace(names[i]))
                result.Add(new PaperSourceOption(names[i], codes[i].ToString()));
        return result;
    }

    private static IReadOnlyList<MediaTypeOption> ReadMediaTypes(string printer, string? port)
    {
        var names = ReadStrings(printer, port, SpoolerInterop.DcMediaTypeNames, 64);
        return names.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => new MediaTypeOption(n)).ToList();
    }

    private static IReadOnlyList<int> ReadResolutions(string printer, string? port)
    {
        var pairs = ReadInts(printer, port, SpoolerInterop.DcEnumResolutions);
        var result = new List<int>();
        for (var i = 0; i + 1 < pairs.Count; i += 2)
            if (pairs[i] > 0)
                result.Add(pairs[i]);
        return result.Distinct().OrderBy(x => x).ToList();
    }

    private static int DeviceCapability(string printer, string? port, ushort capability) =>
        SpoolerInterop.DeviceCapabilities(printer, port, capability, IntPtr.Zero, IntPtr.Zero);

    private static List<ushort> ReadUShorts(string printer, string? port, ushort capability)
    {
        var count = SpoolerInterop.DeviceCapabilities(printer, port, capability, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return new List<ushort>();
        var buffer = Marshal.AllocHGlobal(count * sizeof(ushort));
        try
        {
            SpoolerInterop.DeviceCapabilities(printer, port, capability, buffer, IntPtr.Zero);
            var result = new List<ushort>(count);
            for (var i = 0; i < count; i++)
                result.Add((ushort)Marshal.ReadInt16(buffer, i * sizeof(ushort)));
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<int> ReadInts(string printer, string? port, ushort capability)
    {
        var count = SpoolerInterop.DeviceCapabilities(printer, port, capability, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return new List<int>();
        // DC_ENUMRESOLUTIONS возвращает ПАРЫ LONG на каждую запись.
        var buffer = Marshal.AllocHGlobal(count * 2 * sizeof(int));
        try
        {
            SpoolerInterop.DeviceCapabilities(printer, port, capability, buffer, IntPtr.Zero);
            var result = new List<int>(count * 2);
            for (var i = 0; i < count * 2; i++)
                result.Add(Marshal.ReadInt32(buffer, i * sizeof(int)));
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<(int X, int Y)> ReadPoints(string printer, string? port, ushort capability)
    {
        var count = SpoolerInterop.DeviceCapabilities(printer, port, capability, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return new List<(int, int)>();
        var buffer = Marshal.AllocHGlobal(count * 2 * sizeof(int));
        try
        {
            SpoolerInterop.DeviceCapabilities(printer, port, capability, buffer, IntPtr.Zero);
            var result = new List<(int, int)>(count);
            for (var i = 0; i < count; i++)
                result.Add((Marshal.ReadInt32(buffer, i * 8), Marshal.ReadInt32(buffer, i * 8 + 4)));
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<string> ReadStrings(string printer, string? port, ushort capability, int itemLength)
    {
        var count = SpoolerInterop.DeviceCapabilities(printer, port, capability, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return new List<string>();
        var buffer = Marshal.AllocHGlobal(count * itemLength * sizeof(char));
        try
        {
            SpoolerInterop.DeviceCapabilities(printer, port, capability, buffer, IntPtr.Zero);
            var result = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var chars = new char[itemLength];
                Marshal.Copy(buffer + i * itemLength * sizeof(char), chars, 0, itemLength);
                var zero = Array.IndexOf(chars, '\0');
                result.Add(new string(chars, 0, zero < 0 ? itemLength : zero).Trim());
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ----- Свойства очереди -----

    private static (uint Status, uint Attributes, string? Port, string? Driver, string? Location, string? DefaultPaper)
        ReadQueueInfo(string printerName)
    {
        if (!SpoolerInterop.OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new InvalidOperationException($"Queue '{printerName}' did not open.");
        try
        {
            SpoolerInterop.GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return (0, 0, null, null, null, null);
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!SpoolerInterop.GetPrinter(handle, 2, buffer, needed, out _))
                    return (0, 0, null, null, null, null);
                var info = Marshal.PtrToStructure<SpoolerInterop.PrinterInfo2>(buffer);
                return (info.Status, info.Attributes,
                    Marshal.PtrToStringUni(info.PortName),
                    Marshal.PtrToStringUni(info.DriverName),
                    Marshal.PtrToStringUni(info.Location),
                    ReadDefaultPaper(info.DevMode));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            SpoolerInterop.ClosePrinter(handle);
        }
    }

    /// <summary>
    /// Бумага по умолчанию из настроек очереди (DEVMODE.dmPaperSize). Читаются
    /// только поля фиксированной части структуры по их смещениям: её полный
    /// размер у драйверов разный, и раскладывать чужой буфер целиком в нашу
    /// структуру значило бы читать за его концом.
    /// </summary>
    private static string? ReadDefaultPaper(IntPtr devMode)
    {
        if (devMode == IntPtr.Zero) return null;
        try
        {
            const int sizeOffset = 68;        // dmSize: после dmDeviceName[32] и двух версий
            const int fieldsOffset = 72;      // dmFields
            const int paperSizeOffset = 78;   // dmPaperSize (после dmOrientation)
            var size = (ushort)Marshal.ReadInt16(devMode, sizeOffset);
            if (size < paperSizeOffset + 2) return null;
            var fields = (uint)Marshal.ReadInt32(devMode, fieldsOffset);
            if ((fields & SpoolerInterop.DmPaperSizeField) == 0) return null;
            var paper = Marshal.ReadInt16(devMode, paperSizeOffset);
            return paper > 0 ? paper.ToString() : null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Бумага по умолчанию не прочиталась");
            return null;
        }
    }

    private static PrinterState MapState(uint status, uint attributes)
    {
        // Порядок важен: сначала то, что требует вмешательства человека.
        if ((status & SpoolerInterop.StatusPaperJam) != 0) return PrinterState.PaperJam;
        if ((status & SpoolerInterop.StatusPaperOut) != 0) return PrinterState.PaperOut;
        if ((status & SpoolerInterop.StatusDoorOpen) != 0) return PrinterState.DoorOpen;
        if ((status & SpoolerInterop.StatusTonerLow) != 0) return PrinterState.TonerLow;
        if ((status & SpoolerInterop.StatusError) != 0) return PrinterState.Error;
        if ((status & SpoolerInterop.StatusNotAvailable) != 0) return PrinterState.Offline;
        if ((status & SpoolerInterop.StatusOffline) != 0) return PrinterState.Offline;
        if ((attributes & SpoolerInterop.AttributeWorkOffline) != 0) return PrinterState.Offline;
        if ((status & SpoolerInterop.StatusPaused) != 0) return PrinterState.Paused;
        if ((status & SpoolerInterop.StatusPrinting) != 0) return PrinterState.Printing;
        if ((status & SpoolerInterop.StatusBusy) != 0) return PrinterState.Busy;
        return status == 0 ? PrinterState.Ready : PrinterState.Unknown;
    }

    private static PrinterConnection DetectConnection(uint attributes, string? port)
    {
        if (IsVirtualPort(port)) return PrinterConnection.Virtual;
        port ??= "";
        if (port.StartsWith("TS", StringComparison.OrdinalIgnoreCase))
            return PrinterConnection.RemoteDesktop;
        if ((attributes & SpoolerInterop.AttributeNetwork) != 0 ||
            port.StartsWith(@"\\", StringComparison.Ordinal) ||
            port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
            port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
            return PrinterConnection.Network;
        return PrinterConnection.Local;
    }

    /// <summary>Виртуальный принтер определяется по порту вывода в файл, а не по названию.</summary>
    private static bool IsVirtualPort(string? port)
    {
        port ??= "";
        return port.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase)
            || port.StartsWith("SHRFAX", StringComparison.OrdinalIgnoreCase)
            || port.Equals("FILE:", StringComparison.OrdinalIgnoreCase)
            || port.StartsWith("XPSPort", StringComparison.OrdinalIgnoreCase)
            || port.StartsWith("nul", StringComparison.OrdinalIgnoreCase);
    }
}
