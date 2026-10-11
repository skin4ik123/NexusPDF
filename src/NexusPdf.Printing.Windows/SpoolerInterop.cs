using System.Runtime.InteropServices;

namespace NexusPdf.Printing.Windows;

/// <summary>
/// Прямые вызовы диспетчера печати (winspool) и GDI.
///
/// System.Printing для ОПРОСА принтеров здесь больше не используется намеренно:
/// его GetPrintCapabilities строит XML-схему через COM-прослойку и на части
/// драйверов Windows 10 занимает секунды, а на недоступном сетевом принтере
/// висит до RPC-таймаута — окно печати «не открывалось» целыми минутами.
/// DeviceCapabilities и EnumPrinters — те самые вызовы, которыми пользуются
/// нативные приложения: списки приходят за миллисекунды, а размеры бумаги
/// драйвер отдаёт в десятых долях миллиметра, точнее любой нашей таблицы.
/// Отправка задания остаётся на System.Printing — там он работает хорошо.
/// </summary>
internal static class SpoolerInterop
{
    // ----- EnumPrinters -----

    public const uint PrinterEnumLocal = 0x2;
    public const uint PrinterEnumConnections = 0x4;

    /// <summary>PRINTER_ATTRIBUTE_WORK_OFFLINE: пользователь перевёл принтер «работать автономно».</summary>
    public const uint AttributeWorkOffline = 0x400;
    public const uint AttributeNetwork = 0x10;
    public const uint AttributeLocal = 0x40;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumPrinters(
        uint flags, string? name, uint level, IntPtr printers, uint bufferSize,
        out uint bytesNeeded, out uint count);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDefaultPrinter(
        System.Text.StringBuilder buffer, ref uint size);

    // ----- GetPrinter: состояние и свойства одной очереди -----

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetPrinter(
        IntPtr handle, uint level, IntPtr printer, uint bufferSize, out uint bytesNeeded);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PrinterInfo2
    {
        public IntPtr ServerName;
        public IntPtr PrinterName;
        public IntPtr ShareName;
        public IntPtr PortName;
        public IntPtr DriverName;
        public IntPtr Comment;
        public IntPtr Location;
        public IntPtr DevMode;
        public IntPtr SepFile;
        public IntPtr PrintProcessor;
        public IntPtr Datatype;
        public IntPtr Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint Jobs;
        public uint AveragePpm;
    }

    // PRINTER_STATUS_* — те, что программа умеет показывать словами.
    public const uint StatusPaused = 0x1;
    public const uint StatusError = 0x2;
    public const uint StatusPaperJam = 0x8;
    public const uint StatusPaperOut = 0x10;
    public const uint StatusOffline = 0x80;
    public const uint StatusBusy = 0x200;
    public const uint StatusPrinting = 0x400;
    public const uint StatusNotAvailable = 0x1000;
    public const uint StatusTonerLow = 0x20000;
    public const uint StatusDoorOpen = 0x400000;

    // ----- DeviceCapabilities -----

    public const ushort DcPapers = 2;
    public const ushort DcPaperSize = 3;
    public const ushort DcBins = 6;
    public const ushort DcDuplex = 7;
    public const ushort DcBinNames = 12;
    public const ushort DcEnumResolutions = 13;
    public const ushort DcPaperNames = 16;
    public const ushort DcCopies = 18;
    public const ushort DcCollate = 22;
    public const ushort DcColorDevice = 32;
    public const ushort DcMediaTypes = 34;
    public const ushort DcMediaTypeNames = 35;

    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DeviceCapabilities(
        string device, string? port, ushort capability, IntPtr output, IntPtr devMode);

    // ----- GDI: непечатаемые поля через информационный контекст принтера -----

    [DllImport("gdi32.dll", EntryPoint = "CreateICW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateIC(string driver, string device, string? port, IntPtr devMode);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(IntPtr hdc, int index);

    public const int HorzRes = 8;
    public const int VertRes = 10;
    public const int LogPixelsX = 88;
    public const int LogPixelsY = 90;
    public const int PhysicalWidth = 110;
    public const int PhysicalHeight = 111;
    public const int PhysicalOffsetX = 112;
    public const int PhysicalOffsetY = 113;

    /// <summary>DEVMODE ровно в объёме, который нужен выбору формата бумаги.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public short Orientation;
        public short PaperSize;
        public short PaperLength;
        public short PaperWidth;
        public short Scale;
        public short Copies;
        public short DefaultSource;
        public short PrintQuality;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    public const uint DmPaperSizeField = 0x2;
    public const uint DmPaperLengthField = 0x4;
    public const uint DmPaperWidthField = 0x8;
}
