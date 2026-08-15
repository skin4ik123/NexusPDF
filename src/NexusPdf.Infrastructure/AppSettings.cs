using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPdf.Infrastructure;

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";          // "ru" | "en"
    public string Theme { get; set; } = "system";          // "light" | "dark" | "system"
    public List<string> RecentFiles { get; set; } = new();
    public List<string> LastSessionFiles { get; set; } = new();
    public int RenderCacheMegabytes { get; set; } = 256;
    public bool SingleInstance { get; set; } = true;

    /// <summary>Движок распознавания: paddle — точнее, tesseract — быстрее.</summary>
    public string OcrEngine { get; set; } = "paddle";

    /// <summary>Языковой пакет распознавания (идентификатор из ocrmodels.lock.json).</summary>
    public string OcrLanguagePack { get; set; } = "cyrillic";
    public bool KeepBackupOnSave { get; set; }
    public double LastZoom { get; set; } = 1.0;

    public const int MaxRecentFiles = 15;

    public void TouchRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }
}

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;

    public JsonSettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
        }
        catch
        {
            // Повреждённые настройки не должны мешать запуску.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(_path) + ".nexustmp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
