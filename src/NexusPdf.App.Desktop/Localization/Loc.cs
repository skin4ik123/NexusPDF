using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;

namespace NexusPdf.App.Desktop.Localization;

/// <summary>
/// Локализация на JSON-словарях (Resources/i18n/*.json, встроены в сборку).
/// Новый язык = один новый файл словаря; ключи, отсутствующие в выбранном
/// языке, берутся из русского словаря.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _current = new();
    private static Dictionary<string, string> _fallback = new();

    public static string CurrentLanguage { get; private set; } = "ru";

    public static IReadOnlyList<string> AvailableLanguages { get; } = new[] { "ru", "en" };

    public static void Load(string language)
    {
        _fallback = ReadDictionary("ru") ?? new Dictionary<string, string>();
        _current = ReadDictionary(language) ?? _fallback;
        CurrentLanguage = _current == _fallback ? "ru" : language;
    }

    public static string Get(string key)
    {
        if (_current.TryGetValue(key, out var value)) return value;
        if (_fallback.TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    public static string F(string key, params object?[] args) =>
        string.Format(Get(key), args);

    private static Dictionary<string, string>? ReadDictionary(string language)
    {
        var name = $"NexusPdf.App.Desktop.Resources.i18n.{language}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
    }
}

/// <summary>Расширение разметки: Text="{l:Loc Open}".</summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
