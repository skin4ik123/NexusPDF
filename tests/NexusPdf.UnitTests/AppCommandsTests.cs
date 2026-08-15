using System.Text.Json;
using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Каталог команд — источник названий и доступности для всего интерфейса.
/// Ошибка здесь видна пользователю как пустой пункт меню, латинский ключ
/// вместо названия или кнопка, которая молча ничего не делает.
/// </summary>
public sealed class AppCommandsTests
{
    private static readonly CommandRegistry Registry = AppCommands.Build();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NexusPdf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Dictionary<string, string> Dictionary(string language)
    {
        var path = Path.Combine(RepoRoot(), "src", "NexusPdf.App.Desktop",
            "Resources", "i18n", language + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    [Fact]
    public void Catalogue_Builds_Without_Duplicate_Identifiers()
    {
        // Реестр падает на повторе сам; тест фиксирует, что каталог собран.
        Assert.True(Registry.All.Count > 50, $"команд в каталоге: {Registry.All.Count}");
    }

    [Fact]
    public void Every_Command_Has_A_Title_In_Both_Languages()
    {
        var ru = Dictionary("ru");
        var en = Dictionary("en");

        var missing = Registry.All
            .Where(c => !ru.ContainsKey(c.TitleKey) || !en.ContainsKey(c.TitleKey))
            .Select(c => $"{c.Id} → {c.TitleKey}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "У команд нет названия в словаре (в меню будет виден ключ):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_Description_Key_Exists_When_It_Is_Set()
    {
        var ru = Dictionary("ru");
        var missing = Registry.All
            .Where(c => c.DescriptionKey.Length > 0 && !ru.ContainsKey(c.DescriptionKey))
            .Select(c => $"{c.Id} → {c.DescriptionKey}")
            .ToList();
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// Причина недоступности обязана быть строкой словаря: иначе выключенный
    /// пункт покажет пользователю «UxNoDocument» вместо объяснения.
    /// </summary>
    [Fact]
    public void Every_Unavailability_Reason_Is_A_Real_Localized_String()
    {
        var ru = Dictionary("ru");
        var en = Dictionary("en");

        // Контексты подобраны так, чтобы сработали все ветки проверок.
        var contexts = new[]
        {
            new SelectionContext(),
            new SelectionContext { HasDocument = true },
            new SelectionContext { HasDocument = true, IsBusy = true },
            new SelectionContext { HasDocument = true, IsReadOnly = true },
            new SelectionContext { HasDocument = true, AllowsPrinting = false },
            new SelectionContext { HasDocument = true, AllowsEditing = false },
            new SelectionContext { HasDocument = true, HasQpdf = false },
            new SelectionContext { HasDocument = true, HasOcr = false },
            new SelectionContext { HasDocument = true, HasImageEditor = false },
            new SelectionContext { HasDocument = true, PageCount = 3, SelectedCount = 3, Kind = SelectionKind.Page },
            new SelectionContext { HasDocument = true, Kind = SelectionKind.Text, HasTextSelection = true },
        };

        var bad = new List<string>();
        foreach (var command in Registry.All)
        {
            foreach (var context in contexts)
            {
                var result = command.Evaluate(context);
                if (result.IsAvailable) continue;
                Assert.False(string.IsNullOrEmpty(result.ReasonKey),
                    $"{command.Id} выключается молча");
                if (!ru.ContainsKey(result.ReasonKey!) || !en.ContainsKey(result.ReasonKey!))
                    bad.Add($"{command.Id} → {result.ReasonKey}");
            }
        }
        Assert.True(bad.Count == 0, "Причины без перевода:\n" + string.Join("\n", bad.Distinct()));
    }

    [Fact]
    public void Every_Context_Menu_Entry_Exists_In_The_Catalogue()
    {
        // Пункт, которого нет в каталоге, просто молча исчезает из меню —
        // и разница между «убрали намеренно» и «опечатались» пропадает.
        var composer = new ContextMenuComposer(Registry);
        var missing = new List<string>();

        foreach (var kind in Enum.GetValues<SelectionKind>())
        {
            var context = new SelectionContext
            {
                HasDocument = true, Kind = kind, SelectedCount = 1, PageCount = 10,
                HasTextSelection = kind == SelectionKind.Text, IsSigned = true,
            };
            foreach (var item in composer.Compose(context))
            {
                if (Registry.Find(item.Command.Id) == null)
                    missing.Add($"{kind}: {item.Command.Id}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void Page_Menu_Offers_The_Basics_And_Keeps_Deletion_Last()
    {
        var composer = new ContextMenuComposer(Registry);
        var items = composer.Compose(new SelectionContext
        {
            HasDocument = true, Kind = SelectionKind.Page, SelectedCount = 1, PageCount = 10,
        });

        Assert.Contains(items, i => i.Command.Id == CommandIds.RotateRight);
        Assert.Contains(items, i => i.Command.Id == CommandIds.ExtractPages);
        Assert.Equal(CommandIds.DeletePages, items[^1].Command.Id);
    }

    [Fact]
    public void Text_Menu_Appears_Only_For_A_Real_Text_Selection()
    {
        var composer = new ContextMenuComposer(Registry);
        var withSelection = composer.Compose(new SelectionContext
        {
            HasDocument = true, Kind = SelectionKind.Text, HasTextSelection = true, PageCount = 1,
        });

        // Копирование и разметка — то, ради чего меню и открывают.
        Assert.All(new[] { CommandIds.Copy, CommandIds.Highlight, CommandIds.Underline, CommandIds.Strikeout },
            id => Assert.True(withSelection.Single(i => i.Command.Id == id).Availability.IsAvailable, id));

        // То же меню без выделения: пункты остаются, но объясняют, что нужно.
        var without = composer.Compose(new SelectionContext
        {
            HasDocument = true, Kind = SelectionKind.Text, HasTextSelection = false, PageCount = 1,
        });
        Assert.Equal("UxNoTextSelection",
            without.Single(i => i.Command.Id == CommandIds.Underline).Availability.ReasonKey);
    }

    [Fact]
    public void Printing_Is_Blocked_When_The_Document_Forbids_It()
    {
        var context = new SelectionContext { HasDocument = true, AllowsPrinting = false, PageCount = 2 };
        foreach (var id in new[] { CommandIds.Print, CommandIds.PrintCurrentPage, CommandIds.PrintSelectedPages })
        {
            var result = Registry.Require(id).Evaluate(context);
            Assert.False(result.IsAvailable, id);
            Assert.Equal("UxPrintForbidden", result.ReasonKey);
        }
    }

    [Fact]
    public void Deleting_The_Last_Page_Is_Refused_With_An_Explanation()
    {
        var result = Registry.Require(CommandIds.DeletePages).Evaluate(new SelectionContext
        {
            HasDocument = true, Kind = SelectionKind.Page, PageCount = 1, SelectedCount = 1,
        });
        Assert.Equal("UxCannotDeleteAllPages", result.ReasonKey);
    }

    [Fact]
    public void Dialog_Commands_Are_Marked_So_The_Title_Gets_Its_Ellipsis()
    {
        // Многоточие ставится по этому признаку, а не руками в словаре.
        Assert.True(Registry.Require(CommandIds.ExtractPages).OpensDialog);
        Assert.True(Registry.Require(CommandIds.Print).OpensDialog);
        Assert.False(Registry.Require(CommandIds.RotateRight).OpensDialog);
    }

    [Fact]
    public void Search_Finds_Real_Commands_By_Everyday_Words()
    {
        var context = new SelectionContext { HasDocument = true, PageCount = 5 };
        var cases = new (string Query, string Id)[]
        {
            ("перевернуть", CommandIds.RotateRight),
            ("вымарать", CommandIds.Redact),
            ("пейнт", CommandIds.EditImageInPaint),
            ("пароль", CommandIds.ProtectWithPassword),
            ("склеить", CommandIds.MergePdfs),
            ("распознать", CommandIds.Ocr),
            ("сжать", CommandIds.CompressPages),
        };

        foreach (var (query, id) in cases)
        {
            var results = Registry.Search(query, context, Loc);
            Assert.True(results.Any(r => r.Command.Id == id),
                $"по запросу «{query}» ожидалась команда {id}");
        }
    }

    /// <summary>Перевод названий для поиска — из настоящего русского словаря.</summary>
    private static string Loc(string key) => Dictionary("ru").GetValueOrDefault(key, key);
}
