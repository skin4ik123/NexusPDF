using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPdf.Infrastructure;

public sealed class AppSettings
{
    /// <summary>
    /// Язык интерфейса: "ru" | "en" | "uk", либо пусто — «как в системе».
    ///
    /// Пусто по умолчанию, потому что программа переведена на три языка, и на
    /// русской Windows открываться по-английски ей незачем: пользователь видит
    /// чужой язык там, где перевод есть. Если языка системы среди переводов
    /// нет, берётся английский — он остаётся общим знаменателем.
    ///
    /// Выбранный вручную язык пишется сюда же и переживает перезапуск, так что
    /// системное умолчание больше не вмешивается.
    /// </summary>
    public string Language { get; set; } = "";
    public string Theme { get; set; } = "system";          // "light" | "dark" | "system"
    public List<string> RecentFiles { get; set; } = new();
    public List<string> LastSessionFiles { get; set; } = new();
    public int RenderCacheMegabytes { get; set; } = 256;
    public bool SingleInstance { get; set; } = true;

    /// <summary>Движок распознавания: paddle — точнее, tesseract — быстрее.</summary>
    public string OcrEngine { get; set; } = "paddle";

    /// <summary>Языковой пакет распознавания (идентификатор из ocrmodels.lock.json).</summary>
    public string OcrLanguagePack { get; set; } = "cyrillic";
    /// <summary>
    /// Плотность интерфейса: "auto" | "compact" | "comfortable" | "touch".
    /// «auto» — размеры идут за способом ввода: коснулись пальцем, значит цели
    /// становятся крупнее сразу.
    /// </summary>
    public string UiDensity { get; set; } = "auto";

    /// <summary>
    /// Состав быстрой панели — идентификаторы команд, «|» означает разделитель.
    /// Пустой список означает «набор по умолчанию».
    /// </summary>
    public List<string> QuickCommands { get; set; } = new();

    /// <summary>
    /// Какое поколение набора по умолчанию уже влито в <see cref="QuickCommands"/>.
    ///
    /// Нужно, чтобы новые кнопки доходили до тех, кто панель уже настраивал:
    /// без этого сохранённый список навсегда остаётся таким, каким был, и
    /// добавленная в программу команда просто не появляется. Влитое поколение
    /// запоминается, поэтому убранную вручную кнопку обновление не вернёт.
    /// </summary>
    public int QuickCommandsGeneration { get; set; }

    /// <summary>Недавно использованные инструменты: верхний раздел панели.</summary>
    public List<string> RecentCommands { get; set; } = new();

    /// <summary>Показывать подписи рядом со значками быстрой панели.</summary>
    public bool QuickPanelLabels { get; set; } = true;

    /// <summary>Рабочее пространство: reading | editing | reviewing | pages.</summary>
    public string Workspace { get; set; } = "reading";

    /// <summary>
    /// Видимые панели списком имён: QuickPanel, ToolRail, SidePanel, Comments,
    /// Properties, StatusBar. Пустая строка означает «спрятано всё».
    /// </summary>
    public string? Panels { get; set; }

    /// <summary>Ширины панелей: боковая, инструменты, комментарии, свойства.</summary>
    public string? PanelWidths { get; set; }

    /// <summary>Расположение инструментов в правой панели, заданное пользователем.</summary>
    public string? ToolsLayout { get; set; }

    /// <summary>
    /// Разделы правой панели, раскрытые пользователем, через «;». Пусто —
    /// свёрнуто всё: восемь раскрытых списков подряд не помещаются на экран, и
    /// панель превращается в длинную прокрутку вместо оглавления.
    /// </summary>
    public string? ToolsExpandedGroups { get; set; }

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
