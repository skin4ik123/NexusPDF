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

    /// <summary>
    /// У списка страниц вертикальная полоса прокрутки ПОСТОЯННАЯ. С «Auto» она
    /// появлялась после первой подгонки по ширине, съедала 17 px вьюпорта, и
    /// первый же жест прокрутки пересчитывал масштаб — на 1366×768 документ
    /// заметно «дёргался», прежде чем поехать.
    /// </summary>
    [Fact]
    public void The_Pages_List_Reserves_Its_Scrollbar()
    {
        var path = Path.Combine(RepoRoot(), "src", "NexusPdf.App.Desktop", "Views", "DocumentView.xaml");
        var text = File.ReadAllText(path);
        var listStart = text.IndexOf("x:Name=\"PagesList\"", StringComparison.Ordinal);
        Assert.True(listStart >= 0, "список страниц переименован — поправьте тест");
        var tag = text.Substring(listStart, Math.Min(2000, text.Length - listStart));
        tag = tag[..tag.IndexOf('>')];
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Visible\"", tag);
    }

    /// <summary>
    /// Карточка страницы встаёт пиксель в пиксель: её размер задаёт элемент
    /// без рамки (рамка в 1 px отнимала у растра по пикселю с каждой стороны
    /// и мылила страницу), а тень нарисована фигурами, а не эффектом, который
    /// гнал весь лист через промежуточную текстуру.
    /// </summary>
    [Fact]
    public void The_Page_Card_Keeps_Its_Raster_Pixel_Exact()
    {
        var path = Path.Combine(RepoRoot(), "src", "NexusPdf.App.Desktop", "Views", "DocumentView.xaml");
        var text = File.ReadAllText(path);
        var listStart = text.IndexOf("x:Name=\"PagesList\"", StringComparison.Ordinal);
        var listEnd = text.IndexOf("x:Name=\"OrganizeList\"", StringComparison.Ordinal);
        Assert.True(listStart >= 0 && listEnd > listStart, "списки переименованы — поправьте тест");
        var reading = text[listStart..listEnd];

        Assert.DoesNotContain("<DropShadowEffect", reading);

        var card = reading.IndexOf("views:ViewBehaviors.RenderFullPage=\"True\"", StringComparison.Ordinal);
        Assert.True(card >= 0, "карточка страницы потеряла RenderFullPage");
        var tagStart = reading.LastIndexOf('<', card);
        var tag = reading[tagStart..reading.IndexOf('>', card)];
        Assert.StartsWith("<Grid", tag);
        Assert.DoesNotContain("BorderThickness", tag);
        Assert.Contains("Width=\"{Binding WidthDiu}\"", tag);
        Assert.Contains("Height=\"{Binding HeightDiu}\"", tag);
    }

    /// <summary>
    /// Файлы из Проводника в организаторе принимает библиотека перетаскивания
    /// (модель документа как IDropTarget). Собственные обработчики Drop на том
    /// же списке до событий не доходили: библиотека помечала их обработанными.
    /// </summary>
    [Fact]
    public void The_Organizer_Leaves_File_Drops_To_The_Drag_Library()
    {
        var path = Path.Combine(RepoRoot(), "src", "NexusPdf.App.Desktop", "Views", "DocumentView.xaml");
        var text = File.ReadAllText(path);
        var listStart = text.IndexOf("x:Name=\"OrganizeList\"", StringComparison.Ordinal);
        Assert.True(listStart >= 0, "организатор переименован — поправьте тест");
        var tag = text.Substring(listStart, Math.Min(2000, text.Length - listStart));
        tag = tag[..tag.IndexOf('>')];

        Assert.Contains("dd:DragDrop.IsDropTarget=\"True\"", tag);
        Assert.Contains("dd:DragDrop.DropHandler=\"{Binding}\"", tag);
        Assert.DoesNotContain(" Drop=\"", tag);
        Assert.DoesNotContain(" DragOver=\"", tag);
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
