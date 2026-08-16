using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Ключи перевода. Пропущенный ключ не роняет программу — он молча показывает
/// пользователю само имя ключа: кнопка «Copy» вместо «Копировать». Такое видно
/// только глазами и только если дойти до нужного окна, поэтому проверяется
/// разом по всем исходникам.
/// </summary>
public sealed class LocalizationKeysTests
{
    private static readonly Regex XamlKey = new(@"\{l:Loc\s+([A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);
    /// <summary>
    /// Ключ, СКЛЕЕННЫЙ из частей («PrinterState_» + состояние), проверить
    /// статически нельзя — за такими следят отдельные проверки самих списков.
    /// Поэтому берутся только целые литералы: за строкой сразу «,» или «)».
    /// </summary>
    private static readonly Regex CodeKey = new(@"Loc\.(?:Get|F)\(\s*""([A-Za-z0-9_]+)""\s*[,)]", RegexOptions.Compiled);

    /// <summary>Корень репозитория ищется по файлу решения — тесты не знают своего пути в CI.</summary>
    private static DirectoryInfo RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NexusPdf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static Dictionary<string, string> Pack(DirectoryInfo root, string language)
    {
        var path = Path.Combine(root.FullName, "src", "NexusPdf.App.Desktop", "Resources", "i18n", $"{language}.json");
        Assert.True(File.Exists(path), $"Нет словаря {language}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    /// <summary>Все ключи, которые интерфейс просит у словаря.</summary>
    private static IEnumerable<(string Key, string File)> UsedKeys(DirectoryInfo root)
    {
        var app = Path.Combine(root.FullName, "src", "NexusPdf.App.Desktop");
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in XamlKey.Matches(File.ReadAllText(file)))
                yield return (m.Groups[1].Value, Path.GetFileName(file));
        }
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in CodeKey.Matches(File.ReadAllText(file)))
                yield return (m.Groups[1].Value, Path.GetFileName(file));
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Every_Key_Asked_For_By_The_Interface_Exists(string language)
    {
        var root = RepositoryRoot();
        var pack = Pack(root, language);
        var missing = UsedKeys(root)
            .Where(u => !pack.ContainsKey(u.Key))
            .Select(u => $"{u.Key} ({u.File})")
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        Assert.True(missing.Count == 0,
            $"В {language}.json нет ключей, которые просит интерфейс: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Russian_And_English_Packs_Describe_The_Same_Things()
    {
        var root = RepositoryRoot();
        var ru = Pack(root, "ru");
        var en = Pack(root, "en");

        var onlyRu = ru.Keys.Except(en.Keys).OrderBy(x => x).ToList();
        var onlyEn = en.Keys.Except(ru.Keys).OrderBy(x => x).ToList();

        Assert.True(onlyRu.Count == 0, "Есть только по-русски: " + string.Join(", ", onlyRu));
        Assert.True(onlyEn.Count == 0, "Есть только по-английски: " + string.Join(", ", onlyEn));
    }

    /// <summary>
    /// Подстановки должны совпадать: если по-русски «{0} из {1}», а по-английски
    /// только «{0}», английский текст потеряет число молча.
    /// </summary>
    [Fact]
    public void Placeholders_Match_Between_Languages()
    {
        var root = RepositoryRoot();
        var ru = Pack(root, "ru");
        var en = Pack(root, "en");
        var placeholder = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);

        var broken = new List<string>();
        foreach (var (key, russian) in ru)
        {
            if (!en.TryGetValue(key, out var english)) continue;
            var a = placeholder.Matches(russian).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            var b = placeholder.Matches(english).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            if (!a.SequenceEqual(b)) broken.Add(key);
        }
        Assert.True(broken.Count == 0, "Разные подстановки: " + string.Join(", ", broken));
    }
}
