using System.Text.RegularExpressions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Поле ввода без доступного имени экранный диктор называть не умеет: он
/// произносит тип элемента и молчит о том, что в него вводить. Подпись рядом
/// он при этом не читает — она отдельный элемент.
///
/// Проверяется разметка, а не запущенная программа: имя ставится в XAML, и
/// новое поле без него должно падать на сборке, а не всплывать у человека,
/// который работает с диктором.
/// </summary>
public sealed class AccessibilityMarkupTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NexusPdf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_Input_Field_Has_A_Name_For_The_Screen_Reader()
    {
        // Отрицательный просмотр отсекает теги-свойства вроде ComboBox.ItemTemplate.
        var input = new Regex(@"<(TextBox|ComboBox|PasswordBox|Slider)(?![.\w])[^>]*?/?>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var problems = new List<string>();
        var separator = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.xaml", options))
        {
            if (file.Contains($"{separator}obj{separator}") || file.Contains($"{separator}bin{separator}"))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match match in input.Matches(text))
            {
                var tag = match.Value;
                if (tag.Contains("AutomationProperties.Name") ||
                    tag.Contains("AutomationProperties.LabeledBy"))
                    continue;
                var line = text.Take(match.Index).Count(c => c == (char)10) + 1;
                problems.Add($"{Path.GetFileName(file)}:{line} — {match.Groups[1].Value}");
            }
        }

        Assert.True(problems.Count == 0,
            "Поля ввода без имени для диктора:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems));
    }
}
