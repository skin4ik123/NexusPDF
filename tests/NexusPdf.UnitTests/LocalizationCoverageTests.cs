using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Каждая строка, которую интерфейс просит у словаря, обязана в нём быть — и
/// в русском, и в английском. Пропущенный ключ выглядит как пустая надпись
/// или как латинское имя ключа посреди русского окна; глазами это ловится
/// только случайно, поэтому проверяется тестом.
/// </summary>
public sealed class LocalizationCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NexusPdf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static HashSet<string> ReadKeys(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Ключи, запрошенные из кода и разметки: Loc.Get("X"), Loc.F("X", …), {l:Loc X}.</summary>
    private static Dictionary<string, string> UsedKeys(string root)
    {
        var patterns = new[]
        {
            new Regex(@"Loc\.Get\(""([A-Za-z0-9_]+)""\)", RegexOptions.Compiled),
            new Regex(@"Loc\.F\(""([A-Za-z0-9_]+)""", RegexOptions.Compiled),
            new Regex(@"\{l:Loc\s+([A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled),
        };

        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", options))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                continue;
            // Сама реализация словаря содержит примеры вызовов — её пропускаем.
            if (Path.GetFileName(file).Equals("Loc.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            foreach (var pattern in patterns)
            {
                foreach (Match match in pattern.Matches(text))
                    used[match.Groups[1].Value] = Path.GetRelativePath(root, file);
            }
        }
        return used;
    }

    [Fact]
    public void Every_Requested_String_Exists_In_Both_Languages()
    {
        var root = RepoRoot();
        var ru = ReadKeys(Path.Combine(root, "src", "NexusPdf.App.Desktop", "Resources", "i18n", "ru.json"));
        var en = ReadKeys(Path.Combine(root, "src", "NexusPdf.App.Desktop", "Resources", "i18n", "en.json"));

        var missing = UsedKeys(root)
            .Where(pair => !ru.Contains(pair.Key) || !en.Contains(pair.Key))
            .Select(pair => $"{pair.Key} ({pair.Value})" +
                (ru.Contains(pair.Key) ? " — нет в en" : en.Contains(pair.Key) ? " — нет в ru" : " — нет в обоих"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Интерфейс просит строки, которых нет в словаре:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Russian_And_English_Dictionaries_Have_The_Same_Keys()
    {
        var root = RepoRoot();
        var ru = ReadKeys(Path.Combine(root, "src", "NexusPdf.App.Desktop", "Resources", "i18n", "ru.json"));
        var en = ReadKeys(Path.Combine(root, "src", "NexusPdf.App.Desktop", "Resources", "i18n", "en.json"));

        var onlyRu = ru.Except(en, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var onlyEn = en.Except(ru, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.True(onlyRu.Count == 0 && onlyEn.Count == 0,
            $"Только в ru: {string.Join(", ", onlyRu)}\nТолько в en: {string.Join(", ", onlyEn)}");
    }
}
