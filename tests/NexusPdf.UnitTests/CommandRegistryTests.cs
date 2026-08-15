using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Реестр команд и сборка контекстных меню. Проверяется то, ради чего реестр
/// и заводился: одно действие — одна запись, меню и панели берут её из одного
/// места, недоступность всегда объясняется.
/// </summary>
public sealed class CommandRegistryTests
{
    private static CommandDescriptor Cmd(
        string id, string title, MenuGroup group = MenuGroup.Editing,
        CommandDanger danger = CommandDanger.Undoable,
        bool multi = false, string[]? keywords = null,
        Func<SelectionContext, string?>? unavailable = null) => new()
    {
        Id = id,
        TitleKey = title,
        Group = group,
        Danger = danger,
        SupportsMultiSelection = multi,
        Keywords = keywords ?? Array.Empty<string>(),
        Unavailable = unavailable,
    };

    private static string Resolve(string key) => key;

    [Fact]
    public void Duplicate_Registration_Is_Rejected_Loudly()
    {
        // Две записи одной команды — это две разных реализации, которые
        // разойдутся в поведении. Лучше упасть на старте, чем разойтись тихо.
        var error = Assert.Throws<InvalidOperationException>(() => new CommandRegistry(new[]
        {
            Cmd("edit.copy", "Копировать"),
            Cmd("edit.copy", "Копировать ещё раз"),
        }));
        Assert.Contains("edit.copy", error.Message);
    }

    [Fact]
    public void Unknown_Command_Is_Reported_By_Id()
    {
        var registry = new CommandRegistry(new[] { Cmd("a", "A") });
        Assert.Null(registry.Find("нет-такой"));
        Assert.Throws<KeyNotFoundException>(() => registry.Require("нет-такой"));
    }

    // ----- Доступность -----

    [Fact]
    public void Read_Only_Document_Blocks_Changes_But_Not_Viewing()
    {
        var registry = new CommandRegistry(new[]
        {
            Cmd("edit.delete", "Удалить", danger: CommandDanger.Undoable),
            Cmd("view.zoomIn", "Крупнее", danger: CommandDanger.Safe),
        });
        var context = new SelectionContext { HasDocument = true, IsReadOnly = true };

        var delete = registry.Require("edit.delete").Evaluate(context);
        Assert.False(delete.IsAvailable);
        Assert.Equal("UxReadOnly", delete.ReasonKey);

        Assert.True(registry.Require("view.zoomIn").Evaluate(context).IsAvailable);
    }

    [Fact]
    public void Busy_Document_Blocks_Changes_With_Its_Own_Reason()
    {
        var command = Cmd("edit.delete", "Удалить");
        var result = command.Evaluate(new SelectionContext { HasDocument = true, IsBusy = true });
        Assert.Equal("UxBusy", result.ReasonKey);
    }

    [Fact]
    public void Unavailable_Command_Always_Explains_Itself()
    {
        var command = Cmd("ocr.recognize", "Распознать",
            unavailable: c => c.HasOcr ? null : "UxNoOcr");

        var withoutOcr = command.Evaluate(new SelectionContext { HasDocument = true, HasOcr = false });
        Assert.False(withoutOcr.IsAvailable);
        Assert.False(string.IsNullOrEmpty(withoutOcr.ReasonKey),
            "выключенная команда обязана называть причину");
    }

    // ----- Поиск -----

    private static CommandRegistry SearchRegistry() => new(new[]
    {
        Cmd("pages.rotateRight", "Повернуть вправо", keywords: new[] { "перевернуть", "поворот", "rotate" }),
        Cmd("pages.extract", "Извлечь страницы", keywords: new[] { "вытащить", "сохранить страницы" }),
        Cmd("content.editImageInPaint", "Редактировать изображение в Paint",
            keywords: new[] { "пейнт", "paint", "картинка" }),
        Cmd("print.open", "Печать", keywords: new[] { "распечатать", "print" }),
        Cmd("security.redact", "Удалить конфиденциальные данные",
            keywords: new[] { "вымарать", "redaction", "зачернить" }),
    });

    [Theory]
    [InlineData("повернуть", "pages.rotateRight")]
    [InlineData("перевернуть", "pages.rotateRight")]  // синоним
    [InlineData("rotate", "pages.rotateRight")]        // английский
    [InlineData("пейнт", "content.editImageInPaint")]  // русская транскрипция
    [InlineData("вымарать", "security.redact")]        // профессиональный жаргон
    [InlineData("распечатать", "print.open")]
    public void Search_Finds_By_Synonyms_And_Both_Languages(string query, string expectedId)
    {
        var results = SearchRegistry().Search(query, new SelectionContext { HasDocument = true }, Resolve);
        Assert.NotEmpty(results);
        Assert.Equal(expectedId, results[0].Command.Id);
    }

    [Fact]
    public void Search_Survives_The_Yo_Letter()
    {
        // «ё» и «е» путают постоянно, и команда не должна из-за этого теряться.
        var registry = new CommandRegistry(new[] { Cmd("a", "Чёткая линия") });
        var results = registry.Search("четкая", new SelectionContext(), Resolve);
        Assert.Single(results);
    }

    [Fact]
    public void Fuzzy_Search_Finds_By_Letters_In_Order()
    {
        var results = SearchRegistry().Search("извстр", new SelectionContext { HasDocument = true }, Resolve);
        Assert.Contains(results, r => r.Command.Id == "pages.extract");
    }

    [Fact]
    public void Available_Commands_Rank_Above_Unavailable_Ones()
    {
        var registry = new CommandRegistry(new[]
        {
            Cmd("a.print", "Печать", unavailable: _ => "UxNoPrinter"),
            Cmd("b.print", "Печать документа"),
        });
        var results = registry.Search("печать", new SelectionContext { HasDocument = true }, Resolve);

        // Показывать сверху то, что нельзя выполнить, — плохой совет.
        Assert.Equal("b.print", results[0].Command.Id);
        Assert.True(results[0].Availability.IsAvailable);
    }

    [Fact]
    public void Unavailable_Commands_Are_Still_Listed_With_A_Reason()
    {
        // Прятать их совсем нельзя: пользователь ищет команду именно чтобы
        // понять, почему она не работает.
        var registry = new CommandRegistry(new[]
        {
            Cmd("ocr.recognize", "Распознать", unavailable: _ => "UxNoOcr"),
        });
        var result = Assert.Single(registry.Search("распознать", new SelectionContext(), Resolve));
        Assert.False(result.Availability.IsAvailable);
        Assert.Equal("UxNoOcr", result.Availability.ReasonKey);
    }

    [Fact]
    public void Empty_Query_Returns_Everything_Available_First()
    {
        var registry = SearchRegistry();
        var results = registry.Search("", new SelectionContext { HasDocument = true }, Resolve);
        Assert.Equal(registry.All.Count, results.Count);
    }

    // ----- Контекстные меню -----

    private static CommandRegistry MenuRegistry() => new(new[]
    {
        Cmd(CommandIds.RotateRight, "Повернуть вправо", MenuGroup.Primary, multi: true),
        Cmd(CommandIds.RotateLeft, "Повернуть влево", MenuGroup.Primary, multi: true),
        Cmd(CommandIds.Rotate180, "Повернуть на 180°", MenuGroup.Primary, multi: true),
        Cmd(CommandIds.Copy, "Копировать", MenuGroup.Clipboard, multi: true),
        Cmd(CommandIds.Duplicate, "Дублировать", MenuGroup.Editing, multi: true),
        Cmd(CommandIds.ExtractPages, "Извлечь страницы", MenuGroup.Special, multi: true),
        Cmd(CommandIds.EditPageInPaint, "Редактировать страницу в Paint", MenuGroup.Special),
        Cmd(CommandIds.OcrPages, "Распознать текст", MenuGroup.Special, multi: true),
        Cmd(CommandIds.CompressPages, "Сжать страницы", MenuGroup.Special, multi: true),
        Cmd(CommandIds.PrintSelectedPages, "Печать страниц", MenuGroup.Special, multi: true),
        Cmd(CommandIds.DeletePages, "Удалить страницы", MenuGroup.Dangerous,
            CommandDanger.Undoable, multi: true),
        Cmd(CommandIds.PageProperties, "Свойства страницы", MenuGroup.Properties),
    });

    [Fact]
    public void Page_Menu_Puts_Delete_At_The_Very_Bottom()
    {
        var composer = new ContextMenuComposer(MenuRegistry());
        var items = composer.Compose(new SelectionContext
        {
            HasDocument = true, Kind = SelectionKind.Page, SelectedCount = 1,
        });

        Assert.NotEmpty(items);
        // «Удалить» не должно оказаться между «Копировать» и «Вставить».
        Assert.Equal(CommandIds.DeletePages, items[^1].Command.Id);
    }

    [Fact]
    public void Menu_Groups_Are_Separated()
    {
        var composer = new ContextMenuComposer(MenuRegistry());
        var items = composer.Compose(new SelectionContext
        {
            HasDocument = true, Kind = SelectionKind.Page, SelectedCount = 1,
        });

        Assert.True(items.Count(i => i.IsSeparatorBefore) >= 3,
            "логические группы обязаны разделяться");
        Assert.False(items[0].IsSeparatorBefore, "разделитель не ставится перед первым пунктом");
    }

    [Fact]
    public void Multi_Selection_Hides_Commands_That_Make_No_Sense_For_It()
    {
        var composer = new ContextMenuComposer(MenuRegistry());
        var many = composer.Compose(new SelectionContext
        {
            HasDocument = true, Kind = SelectionKind.Page, SelectedCount = 12,
        });

        // Правка одной страницы в редакторе растра для двенадцати бессмысленна.
        Assert.DoesNotContain(many, i => i.Command.Id == CommandIds.EditPageInPaint);
        Assert.Contains(many, i => i.Command.Id == CommandIds.RotateRight);
    }

    [Fact]
    public void Multi_Selection_Title_Names_The_Count()
    {
        var command = Cmd(CommandIds.RotateRight, "PageRotateRight", multi: true);
        var context = new SelectionContext { Kind = SelectionKind.Page, SelectedCount = 12 };

        var title = ContextMenuComposer.Title(command, context,
            key => key == "PageRotateRightMany" ? "Повернуть {0} страниц вправо" : key,
            (key, args) => string.Format("Повернуть {0} страниц вправо", args));

        Assert.Equal("Повернуть 12 страниц вправо", title);
    }

    [Fact]
    public void Missing_Plural_Key_Falls_Back_Instead_Of_Inventing_Text()
    {
        var command = Cmd("x", "Повернуть", multi: true);
        var title = ContextMenuComposer.Title(
            command, new SelectionContext { SelectedCount = 5 },
            Resolve, (key, args) => key);
        Assert.Equal("Повернуть", title);
    }

    [Fact]
    public void Unknown_Selection_Kind_Gives_An_Empty_Menu_Not_A_Crash()
    {
        var composer = new ContextMenuComposer(new CommandRegistry(Array.Empty<CommandDescriptor>()));
        Assert.Empty(composer.Compose(new SelectionContext { Kind = SelectionKind.Page }));
    }

    [Fact]
    public void Every_Menu_Entry_References_A_Real_Command_Id()
    {
        // Опечатка в списке меню иначе просто молча уберёт пункт.
        var known = typeof(CommandIds)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var registry = new CommandRegistry(known.Select(id => Cmd(id, id, multi: true)));
        var composer = new ContextMenuComposer(registry);

        foreach (var kind in Enum.GetValues<SelectionKind>())
        {
            var items = composer.Compose(new SelectionContext
            {
                HasDocument = true, Kind = kind, SelectedCount = 1,
            });
            Assert.All(items, i => Assert.Contains(i.Command.Id, known));
        }
    }
}
