using System.IO;
using System.Text.Json;
using NexusPdf.Printing;

namespace NexusPdf.Printing.Windows;

/// <summary>
/// Дисковый кэш возможностей принтеров.
///
/// Возможности меняются при смене драйвера — то есть почти никогда, а стоят
/// дорого: это поход к драйверу, на сетевых очередях — по сети. Кэш делает
/// второе открытие окна печати мгновенным даже там, где первый опрос занял
/// секунды: окно сразу работает на сохранённых данных, а свежие приходят
/// фоном и молча заменяют устаревшие.
///
/// Состояние принтера (готов/нет бумаги/офлайн) НЕ кэшируется осознанно —
/// оно живое и читается при каждом открытии.
///
/// На диск пишется собственная плоская форма, а не доменные типы: у тех есть
/// вычисляемые свойства (например, SizePt.Swapped типа SizePt), на которых
/// сериализатор зацикливается, да и формат кэша не должен ломаться от любой
/// правки домена.
/// </summary>
public sealed class PrinterCapsCache
{
    private sealed class PaperDto
    {
        public string Name { get; set; } = "";
        public double W { get; set; }
        public double H { get; set; }
        public string? Driver { get; set; }
        public double[]? Margins { get; set; } // Л, В, П, Н — если известны
    }

    private sealed class CapsDto
    {
        public DateTimeOffset Stored { get; set; }
        public string? Driver { get; set; }
        public string? Port { get; set; }
        public string? Location { get; set; }
        public int Connection { get; set; }
        public bool IsVirtual { get; set; }
        public bool Color { get; set; }
        public bool DuplexLong { get; set; }
        public bool DuplexShort { get; set; }
        public bool Collation { get; set; }
        public int MaxCopies { get; set; } = 1;
        public string? DefaultPaper { get; set; }
        public List<PaperDto> Papers { get; set; } = new();
        public List<string> Bins { get; set; } = new();
        public List<string> Media { get; set; } = new();
        public List<int> Resolutions { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;
    private readonly TimeSpan _maxAge;
    private Dictionary<string, CapsDto>? _entries;

    public PrinterCapsCache(string path, TimeSpan? maxAge = null)
    {
        _path = path;
        _maxAge = maxAge ?? TimeSpan.FromDays(14);
    }

    private Dictionary<string, CapsDto> Load()
    {
        if (_entries != null) return _entries;
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, CapsDto>>(File.ReadAllText(_path), Options)
                  ?? new Dictionary<string, CapsDto>()
                : new Dictionary<string, CapsDto>();
        }
        catch (Exception ex)
        {
            // Повреждённый кэш не должен мешать печати: просто начнём заново.
            Serilog.Log.Debug(ex, "Кэш принтеров не прочитался");
            _entries = new Dictionary<string, CapsDto>();
        }
        return _entries;
    }

    public PrinterCapabilities? TryGet(string printerName)
    {
        var entries = Load();
        if (!entries.TryGetValue(printerName, out var dto)) return null;
        if (DateTimeOffset.UtcNow - dto.Stored > _maxAge) return null;
        // Запись без форматов — след неудачного опроса, а не возможности
        // принтера: прежние версии её сохраняли, и окно печати на такой записи
        // уходило в бесконечный повторный опрос и падало.
        if (dto.Papers.Count == 0) return null;

        var margins = new Dictionary<string, MarginsPt>();
        var papers = new List<PaperSizeOption>(dto.Papers.Count);
        foreach (var paper in dto.Papers)
        {
            papers.Add(new PaperSizeOption(paper.Name, new SizePt(paper.W, paper.H), paper.Driver));
            if (paper.Margins is { Length: 4 } m)
                margins[paper.Name] = new MarginsPt(m[0], m[1], m[2], m[3]);
        }

        return PrinterCapabilities.Unknown(printerName) with
        {
            DriverName = dto.Driver,
            PortName = dto.Port,
            Location = dto.Location,
            Connection = (PrinterConnection)dto.Connection,
            IsVirtual = dto.IsVirtual,
            SupportsColor = dto.Color,
            SupportsMonochrome = true,
            SupportsDuplexLongEdge = dto.DuplexLong,
            SupportsDuplexShortEdge = dto.DuplexShort,
            SupportsCollation = dto.Collation,
            MaxCopies = Math.Max(1, dto.MaxCopies),
            PaperSizes = papers,
            DefaultPaperDriverValue = dto.DefaultPaper,
            PaperSources = dto.Bins.Select(b => new PaperSourceOption(b)).ToList(),
            MediaTypes = dto.Media.Select(m => new MediaTypeOption(m)).ToList(),
            ResolutionsDpi = dto.Resolutions,
            HardMarginsByPaper = margins,
        };
    }

    public void Store(PrinterCapabilities capabilities)
    {
        // Неполный ответ драйвера не кэшируется: он не описывает принтер.
        if (capabilities.PaperSizes.Count == 0) return;
        var entries = Load();
        entries[capabilities.PrinterName] = new CapsDto
        {
            Stored = DateTimeOffset.UtcNow,
            Driver = capabilities.DriverName,
            Port = capabilities.PortName,
            Location = capabilities.Location,
            Connection = (int)capabilities.Connection,
            IsVirtual = capabilities.IsVirtual,
            Color = capabilities.SupportsColor,
            DuplexLong = capabilities.SupportsDuplexLongEdge,
            DuplexShort = capabilities.SupportsDuplexShortEdge,
            Collation = capabilities.SupportsCollation,
            MaxCopies = capabilities.MaxCopies,
            DefaultPaper = capabilities.DefaultPaperDriverValue,
            Papers = capabilities.PaperSizes.Select(p => new PaperDto
            {
                Name = p.Name,
                W = p.SizePt.WidthPt,
                H = p.SizePt.HeightPt,
                Driver = p.DriverValue,
                Margins = capabilities.HardMarginsByPaper.TryGetValue(p.Name, out var m)
                    ? new[] { m.LeftPt, m.TopPt, m.RightPt, m.BottomPt }
                    : null,
            }).ToList(),
            Bins = capabilities.PaperSources.Select(b => b.Name).ToList(),
            Media = capabilities.MediaTypes.Select(m => m.Name).ToList(),
            Resolutions = capabilities.ResolutionsDpi.ToList(),
        };

        // Очереди, удалённые из системы, не должны жить в кэше вечно.
        foreach (var stale in entries.Where(e => DateTimeOffset.UtcNow - e.Value.Stored > _maxAge)
                     .Select(e => e.Key).ToList())
            entries.Remove(stale);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Кэш принтеров не записался");
        }
    }
}
