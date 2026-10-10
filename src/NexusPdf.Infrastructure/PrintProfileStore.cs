using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPdf.Printing;

namespace NexusPdf.Infrastructure;

/// <summary>
/// Хранилище профилей печати в JSON рядом с настройками.
///
/// Встроенные профили в файл не пишутся: иначе исправление встроенного набора
/// в новой версии не доехало бы до тех, кто уже запускал программу.
/// </summary>
public sealed class PrintProfileStore
{
    private readonly string _path;

    /// <summary>
    /// Пишутся ВСЕ поля. Прежняя запись опускала значения по умолчанию своего
    /// типа, а при чтении на их место вставали инициализаторы записи — так
    /// «Фактический размер» (нулевое значение перечисления) превращался в
    /// «Уменьшать крупные», а «Только документ» — в «С печатными аннотациями».
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Метка формата в каждой записи. Её нет у файлов прежних версий — у них
    /// отсутствующее поле значит «значение по умолчанию своего типа».
    /// </summary>
    private const string FormatProperty = "FormatVersion";
    private const int FormatVersion = 2;

    public PrintProfileStore(string path) => _path = path;

    public PrintProfileStore() : this(Path.Combine(AppPaths.Root, "print-profiles.json")) { }

    /// <summary>Встроенные плюс пользовательские. Пользовательский с тем же именем побеждает.</summary>
    public IReadOnlyList<PrintProfile> LoadAll()
    {
        var custom = LoadCustom();
        var result = new List<PrintProfile>();

        foreach (var builtIn in BuiltInPrintProfiles.All)
        {
            var overridden = custom.FirstOrDefault(p =>
                string.Equals(p.Name, builtIn.Name, StringComparison.CurrentCultureIgnoreCase));
            result.Add(overridden ?? builtIn);
        }

        foreach (var profile in custom)
        {
            if (!result.Any(p => string.Equals(p.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase)))
                result.Add(profile);
        }
        return result;
    }

    public IReadOnlyList<PrintProfile> LoadCustom()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<PrintProfile>();
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonArray items)
                return Array.Empty<PrintProfile>();

            var result = new List<PrintProfile>();
            foreach (var item in items)
            {
                if (item is not JsonObject record) continue;
                if (!record.ContainsKey(FormatProperty))
                    RestoreOmittedDefaults(record);
                record.Remove(FormatProperty);
                if (record.Deserialize<PrintProfile>(Options) is { } profile)
                    result.Add(profile);
            }
            return result;
        }
        catch (Exception ex)
        {
            // Испорченный файл профилей не должен мешать печатать вообще.
            Serilog.Log.Warning(ex, "Не удалось прочитать профили печати, используются встроенные");
            return Array.Empty<PrintProfile>();
        }
    }

    /// <summary>Сохраняет или заменяет профиль по имени.</summary>
    public void Save(PrintProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("У профиля должно быть имя.", nameof(profile));

        var custom = LoadCustom()
            .Where(p => !string.Equals(p.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        custom.Add(profile with { IsBuiltIn = false });
        WriteCustom(custom);
    }

    /// <summary>
    /// Удаляет пользовательский профиль. Встроенный после удаления
    /// переопределения возвращается к заводскому виду, а не исчезает.
    /// </summary>
    public void Delete(string name)
    {
        var custom = LoadCustom()
            .Where(p => !string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        WriteCustom(custom);
    }

    /// <summary>
    /// Файл прежней версии: поля, равные значению по умолчанию СВОЕГО ТИПА, в
    /// нём просто не записаны. Возвращаем их явно, иначе вместо нуля встанет
    /// инициализатор записи и профиль молча поменяет смысл.
    /// </summary>
    private static void RestoreOmittedDefaults(JsonObject record)
    {
        foreach (var property in typeof(PrintProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || record.ContainsKey(property.Name)) continue;
            var type = property.PropertyType;
            if (type == typeof(bool))
                record[property.Name] = false;
            else if (type.IsEnum || type == typeof(int) || type == typeof(double))
                record[property.Name] = 0;
        }
    }

    private void WriteCustom(IReadOnlyList<PrintProfile> profiles)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var items = new JsonArray();
            foreach (var profile in profiles)
            {
                var record = JsonSerializer.SerializeToNode(profile, Options)!.AsObject();
                record[FormatProperty] = FormatVersion;
                items.Add(record);
            }
            var tmp = _path + ".nexustmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, items.ToJsonString(Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось сохранить профили печати");
        }
    }
}
