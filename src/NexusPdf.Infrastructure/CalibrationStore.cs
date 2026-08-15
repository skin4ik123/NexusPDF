using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPdf.Printing;

namespace NexusPdf.Infrastructure;

/// <summary>
/// Хранилище калибровок печати. Ключ — принтер, формат и лоток вместе: разные
/// форматы подаются по-разному, и одна поправка на весь принтер промахнётся.
/// </summary>
public sealed class CalibrationStore
{
    private readonly string _path;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public CalibrationStore(string path) => _path = path;

    public CalibrationStore() : this(Path.Combine(AppPaths.Root, "print-calibration.json")) { }

    public IReadOnlyList<PrintCalibration> LoadAll()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<PrintCalibration>();
            return JsonSerializer.Deserialize<List<PrintCalibration>>(File.ReadAllText(_path), Options)
                   ?? new List<PrintCalibration>();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать калибровки печати");
            return Array.Empty<PrintCalibration>();
        }
    }

    /// <summary>
    /// Калибровка для сочетания. Если драйвер сменился с момента калибровки,
    /// она возвращается вместе с признаком устаревания: молча применять
    /// поправку от прежнего драйвера нельзя.
    /// </summary>
    public (PrintCalibration? Calibration, bool DriverChanged) Find(
        string printer, string paper, string source, string currentDriver)
    {
        var key = PrintCalibration.MakeKey(printer, paper, source);
        var found = LoadAll().FirstOrDefault(c => c.Key == key);
        if (found == null) return (null, false);

        var changed = found.DriverName.Length > 0 &&
                      currentDriver.Length > 0 &&
                      !string.Equals(found.DriverName, currentDriver, StringComparison.Ordinal);
        return (found, changed);
    }

    public void Save(PrintCalibration calibration)
    {
        var all = LoadAll().Where(c => c.Key != calibration.Key).ToList();
        all.Add(calibration);
        Write(all);
    }

    public void Delete(string printer, string paper, string source)
    {
        var key = PrintCalibration.MakeKey(printer, paper, source);
        Write(LoadAll().Where(c => c.Key != key).ToList());
    }

    private void Write(IReadOnlyList<PrintCalibration> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(items, Options));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось сохранить калибровку печати");
        }
    }
}
