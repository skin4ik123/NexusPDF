using System.Text.Json;
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

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

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
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<PrintProfile>>(json, Options)
                   ?? new List<PrintProfile>();
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

    private void WriteCustom(IReadOnlyList<PrintProfile> profiles)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(profiles, Options));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось сохранить профили печати");
        }
    }
}
