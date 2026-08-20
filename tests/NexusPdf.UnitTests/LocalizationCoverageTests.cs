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

    /// <summary>
    /// Все словари обязаны содержать ОДНИ И ТЕ ЖЕ ключи.
    ///
    /// Проверяется каждый файл каталога, а не пара ru/en: неполный словарь не
    /// падает, а молча подставляет русскую строку, и найти такую дыру можно
    /// только глазами по всему интерфейсу. Добавили язык — он сразу под
    /// проверкой, без правки этого теста.
    /// </summary>
    [Fact]
    public void All_Language_Packs_Have_The_Same_Keys()
    {
        var dir = Path.Combine(RepoRoot(), "src", "NexusPdf.App.Desktop", "Resources", "i18n");
        var packs = Directory.GetFiles(dir, "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, ReadKeys, StringComparer.Ordinal);

        Assert.True(packs.Count >= 2, "Словарей меньше двух — проверять нечего.");
        var reference = packs["ru"];

        var problems = new List<string>();
        foreach (var (language, keys) in packs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (language == "ru") continue;
            var missing = reference.Except(keys, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            var extra = keys.Except(reference, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                problems.Add($"{language}: не хватает {missing.Count} — {string.Join(", ", missing.Take(15))}");
            if (extra.Count > 0)
                problems.Add($"{language}: лишние — {string.Join(", ", extra.Take(15))}");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Подстановки {0}, {1} обязаны совпадать во всех языках: строка с лишней
    /// подстановкой роняет форматирование прямо в лицо пользователю.
    /// </summary>
    [Fact]
    public void All_Language_Packs_Use_The_Same_Placeholders()
    {
        var dir = Path.Combine(RepoRoot(), "src", "NexusPdf.App.Desktop", "Resources", "i18n");
        var reference = ReadValues(Path.Combine(dir, "ru.json"));
        var problems = new List<string>();

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var language = Path.GetFileNameWithoutExtension(file);
            if (language == "ru") continue;
            foreach (var (key, value) in ReadValues(file))
            {
                if (!reference.TryGetValue(key, out var original)) continue;
                var wanted = Placeholders(original);
                var actual = Placeholders(value);
                if (!wanted.SetEquals(actual))
                    problems.Add($"{language}/{key}: ожидались {{{string.Join(",", wanted.Order())}}}, " +
                                 $"а есть {{{string.Join(",", actual.Order())}}}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Ключи, которые собираются из имени значения перечисления:
    /// Loc.Get("PrinterState_" + state) и подобные.
    ///
    /// Поиск по строковым литералам такие обращения не видит, поэтому новое
    /// значение перечисления тихо превращается в латинское имя ключа посреди
    /// русского окна — ровно там, где пользователь ждёт состояние принтера.
    /// </summary>
    [Fact]
    public void Composed_Enum_Keys_Exist_In_Every_Language()
    {
        var dir = Path.Combine(RepoRoot(), "src", "NexusPdf.App.Desktop", "Resources", "i18n");
        var packs = Directory.GetFiles(dir, "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, ReadKeys, StringComparer.Ordinal);

        var wanted = Enum.GetValues<NexusPdf.Printing.PrinterState>()
            .Select(state => "PrinterState_" + state)
            .Concat(Enum.GetValues<NexusPdf.Printing.PrintJobState>()
                .Select(NexusPdf.Printing.PrintJobStateMapper.TitleKey))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        var problems = new List<string>();
        foreach (var (language, keys) in packs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var missing = wanted.Where(key => !keys.Contains(key)).ToList();
            if (missing.Count > 0)
                problems.Add($"{language}: нет строк — {string.Join(", ", missing)}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static Dictionary<string, string> ReadValues(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    private static HashSet<int> Placeholders(string value)
    {
        var found = new HashSet<int>();
        foreach (Match match in Regex.Matches(value, @"\{(\d+)[^}]*\}"))
            found.Add(int.Parse(match.Groups[1].Value));
        return found;
    }
}
