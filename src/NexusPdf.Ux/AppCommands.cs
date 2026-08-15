namespace NexusPdf.Ux;

/// <summary>
/// Каталог команд программы. Единственное место, где записано, как команда
/// называется, каким значком обозначается, когда доступна и почему нет.
///
/// Панели, контекстные меню, палитра и горячие клавиши берут сведения ОТСЮДА.
/// Описывать команду где-то ещё нельзя: расхождение между кнопкой и пунктом
/// меню начинается ровно с такого второго описания.
///
/// В каталоге только РАБОТАЮЩИЕ команды. Красивый пункт меню, за которым нет
/// действия, — обман: команда попадает сюда вместе со своей реализацией, а не
/// заранее. Проверяется это на запуске: у каждой записи обязан быть обработчик.
/// </summary>
public static class AppCommands
{
    public static CommandRegistry Build() => new(All());

    public static IEnumerable<CommandDescriptor> All()
    {
        // ---------- Файл ----------
        yield return new CommandDescriptor
        {
            Id = CommandIds.Open, TitleKey = "Open", DescriptionKey = "OpenTooltip", Glyph = "",
            Category = CommandCategory.File, Group = MenuGroup.Primary,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+O", OpensDialog = true,
            Keywords = new[] { "открыть", "файл", "open", "загрузить" },
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.Save, TitleKey = "Save", DescriptionKey = "SaveTooltip", Glyph = "",
            Category = CommandCategory.File, Group = MenuGroup.Primary, Shortcut = "Ctrl+S",
            Keywords = new[] { "сохранить", "save", "записать" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.SaveAs, TitleKey = "SaveAs", DescriptionKey = "SaveAsTooltip", Glyph = "",
            Category = CommandCategory.File, Group = MenuGroup.Primary,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+Shift+S", OpensDialog = true,
            Keywords = new[] { "сохранить как", "копия", "save as" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.CloseTab, TitleKey = "CloseTab", Glyph = "",
            Category = CommandCategory.File, Group = MenuGroup.Dangerous,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+W",
            Keywords = new[] { "закрыть", "вкладка", "close" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.DocumentProperties, TitleKey = "PropsMenu", Glyph = "",
            Category = CommandCategory.File, Group = MenuGroup.Properties,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "свойства", "сведения", "метаданные", "properties", "автор" },
            Unavailable = NeedsDocument,
        };

        // ---------- Правка ----------
        yield return new CommandDescriptor
        {
            Id = CommandIds.Undo, TitleKey = "Undo", DescriptionKey = "UndoTooltip", Glyph = "",
            Category = CommandCategory.Edit, Group = MenuGroup.Quick, Shortcut = "Ctrl+Z",
            Keywords = new[] { "отменить", "назад", "undo" },
            Unavailable = c => !c.CanUndo ? "UxNothingToUndo" : null,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.Redo, TitleKey = "Redo", DescriptionKey = "RedoTooltip", Glyph = "",
            Category = CommandCategory.Edit, Group = MenuGroup.Quick, Shortcut = "Ctrl+Y",
            Keywords = new[] { "повторить", "вперёд", "redo" },
            Unavailable = c => !c.CanRedo ? "UxNothingToRedo" : null,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.Copy, TitleKey = "UxCopy", Glyph = "",
            Category = CommandCategory.Edit, Group = MenuGroup.Clipboard,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+C",
            Keywords = new[] { "копировать", "copy", "буфер обмена" },
            Unavailable = c => !c.HasTextSelection ? "UxNoTextSelection" : null,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.SelectAllOnPage, TitleKey = "SelectAll",
            Category = CommandCategory.Edit, Group = MenuGroup.Quick,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+A",
            Keywords = new[] { "выделить всё", "select all", "весь текст страницы" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.Duplicate, TitleKey = "DuplicatePages", Glyph = "",
            Category = CommandCategory.Pages, Group = MenuGroup.Editing, SupportsMultiSelection = true,
            Keywords = new[] { "дублировать", "копия страницы", "duplicate", "размножить" },
            Unavailable = NeedsEditableDocument,
        };

        // ---------- Просмотр ----------
        yield return Safe(CommandIds.ZoomIn, "ZoomIn", "", CommandCategory.View,
            "Ctrl++", new[] { "крупнее", "увеличить", "zoom in", "приблизить", "масштаб" });
        yield return Safe(CommandIds.ZoomOut, "ZoomOut", "", CommandCategory.View,
            "Ctrl+-", new[] { "мельче", "уменьшить", "zoom out", "отдалить", "масштаб" });
        yield return Safe(CommandIds.ZoomActual, "ZoomActual", "", CommandCategory.View,
            "Ctrl+1", new[] { "фактический размер", "100", "actual size" });
        yield return Safe(CommandIds.FitWidth, "ZoomFitWidth", "", CommandCategory.View,
            "", new[] { "по ширине", "fit width", "вписать по ширине" });
        yield return Safe(CommandIds.FitPage, "ZoomFitPage", "", CommandCategory.View,
            "Ctrl+0", new[] { "страница целиком", "fit page", "вписать страницу" });
        yield return Safe(CommandIds.Find, "Find", "", CommandCategory.View,
            "Ctrl+F", new[] { "поиск", "найти", "search", "find" });
        yield return new CommandDescriptor
        {
            Id = CommandIds.FindSelection, TitleKey = "UxFindSelection", Glyph = "",
            Category = CommandCategory.View, Group = MenuGroup.Quick, Danger = CommandDanger.Safe,
            Keywords = new[] { "найти это", "искать выделенное", "найти такой же текст" },
            Unavailable = c => !c.HasTextSelection ? "UxNoTextSelection" : null,
        };
        yield return Safe(CommandIds.ToggleOrganize, "OrganizeTooltip", "", CommandCategory.View,
            "", new[] { "систематизация", "порядок страниц", "миниатюры", "organize" });
        yield return Safe(CommandIds.ToggleOutline, "PanelTabOutline", "", CommandCategory.View,
            "", new[] { "оглавление", "закладки", "outline", "содержание" });
        yield return Safe(CommandIds.ToggleCommentsPanel, "CommentsPanel", "", CommandCategory.Comments,
            "", new[] { "панель комментариев", "список замечаний", "comments" });

        // ---------- Страницы ----------
        yield return Page(CommandIds.RotateRight, "RotateRight", "",
            new[] { "повернуть вправо", "перевернуть", "rotate right", "поворот" });
        yield return Page(CommandIds.RotateLeft, "RotateLeft", "",
            new[] { "повернуть влево", "перевернуть", "rotate left", "поворот" });
        yield return Page(CommandIds.Rotate180, "Rotate180", "",
            new[] { "перевернуть", "180", "вверх ногами", "rotate 180" });
        yield return new CommandDescriptor
        {
            Id = CommandIds.DeletePages, TitleKey = "DeletePages", Glyph = "",
            Category = CommandCategory.Pages, Group = MenuGroup.Dangerous,
            SupportsMultiSelection = true,
            Keywords = new[] { "удалить страницы", "убрать страницы", "delete pages" },
            Unavailable = c => NeedsEditableDocument(c)
                ?? (c.PageCount > 0 && c.PageCount <= Math.Max(c.SelectedCount, 1)
                    ? "UxCannotDeleteAllPages" : null),
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.ExtractPages, TitleKey = "ExtractPages", Glyph = "",
            Category = CommandCategory.Pages, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true, SupportsMultiSelection = true,
            Keywords = new[] { "извлечь", "вытащить", "сохранить страницы", "extract pages" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.PageProperties, TitleKey = "UxPageProperties",
            Category = CommandCategory.Pages, Group = MenuGroup.Properties,
            Danger = CommandDanger.Safe, OpensDialog = true, SupportsMultiSelection = true,
            Keywords = new[] { "свойства страницы", "размер страницы", "page size" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.PrintSelectedPages, TitleKey = "UxPrintSelected", Glyph = "",
            Category = CommandCategory.Print, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true, SupportsMultiSelection = true,
            Keywords = new[] { "печать выбранных", "распечатать страницы" },
            Unavailable = NeedsPrintableDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.PrintCurrentPage, TitleKey = "UxPrintCurrent", Glyph = "",
            Category = CommandCategory.Print, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "печать страницы", "распечатать эту страницу" },
            Unavailable = NeedsPrintableDocument,
        };

        // ---------- Содержимое ----------
        yield return Content(CommandIds.AddText, "AddTextMenu", "", true,
            new[] { "добавить текст", "надпись", "add text" });
        yield return Content(CommandIds.InsertImage, "InsertImageMenu", "", true,
            new[] { "вставить изображение", "картинка", "фото", "insert image" });
        yield return Content(CommandIds.InsertSignatureImage, "InsertSignatureMenu", "", true,
            new[] { "подпись картинкой", "факсимиле", "вставить подпись" });
        yield return new CommandDescriptor
        {
            Id = CommandIds.EditText, TitleKey = "UxEditText", DescriptionKey = "TextEditTool",
            Glyph = "", Category = CommandCategory.Content, Group = MenuGroup.Primary,
            Keywords = new[] { "редактировать текст", "изменить текст", "правка текста", "edit text" },
            Unavailable = NeedsEditableDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.EditImageInPaint, TitleKey = "PaintEditImageMenu", Glyph = "",
            Category = CommandCategory.Content, Group = MenuGroup.Special, OpensDialog = true,
            Touch = TouchSuitability.NeedsPrecision,
            Keywords = new[] { "paint", "пейнт", "редактировать изображение", "картинка" },
            Unavailable = c => NeedsEditableDocument(c) ?? (!c.HasImageEditor ? "UxNoImageEditor" : null),
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.EditRegionInPaint, TitleKey = "PaintEditRegionMenu", Glyph = "",
            Category = CommandCategory.Content, Group = MenuGroup.Special, OpensDialog = true,
            Touch = TouchSuitability.NeedsPrecision,
            Keywords = new[] { "paint", "пейнт", "редактировать область", "кусок страницы" },
            Unavailable = c => NeedsEditableDocument(c) ?? (!c.HasImageEditor ? "UxNoImageEditor" : null),
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.EditPageInPaint, TitleKey = "PaintEditMenu", Glyph = "",
            Category = CommandCategory.Content, Group = MenuGroup.Special, OpensDialog = true,
            Touch = TouchSuitability.NeedsPrecision,
            Keywords = new[] { "paint", "пейнт", "редактировать страницу", "внешний редактор" },
            Unavailable = c => NeedsEditableDocument(c) ?? (!c.HasImageEditor ? "UxNoImageEditor" : null),
        };
        yield return Content(CommandIds.HeaderFooter, "HeaderFooterMenu", "", true,
            new[] { "колонтитулы", "номера страниц", "header", "footer" });
        yield return Content(CommandIds.Watermark, "WatermarkMenu", "", true,
            new[] { "водяной знак", "watermark", "черновик", "копия" });

        // ---------- Комментарии ----------
        yield return Comment(CommandIds.AddNote, "UxNote", "NoteTool", "",
            new[] { "заметка", "комментарий", "note", "примечание" });
        yield return new CommandDescriptor
        {
            Id = CommandIds.Highlight, TitleKey = "UxHighlight", DescriptionKey = "HighlightTool",
            Glyph = "", Category = CommandCategory.Comments, Group = MenuGroup.Quick,
            Keywords = new[] { "маркер", "выделение", "highlight", "подсветить" },
            Unavailable = NeedsEditableDocument,
        };
        yield return Markup(CommandIds.Underline, "UxUnderline", "",
            new[] { "подчеркнуть", "underline" });
        yield return Markup(CommandIds.Strikeout, "UxStrikeout", "",
            new[] { "зачеркнуть", "strikeout", "перечеркнуть" });
        yield return Comment(CommandIds.AddRect, "RectTool", "", "",
            new[] { "рамка", "прямоугольник", "rectangle" });
        yield return Comment(CommandIds.AddEllipse, "EllipseTool", "", "",
            new[] { "овал", "круг", "ellipse" });
        yield return Comment(CommandIds.DrawPencil, "UxPencil", "DrawPencilTool", "",
            new[] { "карандаш", "рисовать", "pencil", "от руки" });
        yield return Comment(CommandIds.DrawLine, "UxLine", "DrawLineTool", "",
            new[] { "линия", "line", "прямая" });
        yield return Comment(CommandIds.DrawArrow, "UxArrow", "DrawArrowTool", "",
            new[] { "стрелка", "arrow", "указатель" });
        yield return new CommandDescriptor
        {
            Id = CommandIds.RestoreStroke, TitleKey = "DrawRestoreFree",
            DescriptionKey = "DrawRestoreFreeHint",
            Category = CommandCategory.Comments, Group = MenuGroup.Quick,
            Keywords = new[] { "вернуть штрих", "как было", "от руки", "не выпрямлять" },
            Unavailable = NeedsDocument,
        };

        // ---------- Формы ----------
        yield return new CommandDescriptor
        {
            Id = CommandIds.ToggleFormMode, TitleKey = "FormMode", DescriptionKey = "FormHint",
            Glyph = "", Category = CommandCategory.Forms, Group = MenuGroup.Primary,
            Danger = CommandDanger.Safe,
            Keywords = new[] { "формы", "заполнить", "анкета", "forms" },
            Unavailable = NeedsDocument,
        };

        // ---------- Защита ----------
        yield return new CommandDescriptor
        {
            Id = CommandIds.Redact, TitleKey = "RedactShort", DescriptionKey = "RedactTool",
            Glyph = "", Category = CommandCategory.Security, Group = MenuGroup.Dangerous,
            Danger = CommandDanger.Irreversible,
            Keywords = new[] { "вымарать", "удалить секретное", "redaction", "зачернить" },
            Unavailable = NeedsEditableDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.ProtectWithPassword, TitleKey = "ProtectPdf", Glyph = "",
            Category = CommandCategory.Security, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "пароль", "защитить", "шифрование", "protect", "password" },
            Unavailable = c => NeedsDocument(c) ?? (!c.HasQpdf ? "UxNoQpdf" : null),
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.SignWithCertificate, TitleKey = "SignMenu", Glyph = "",
            Category = CommandCategory.Security, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "подписать", "подпись", "сертификат", "sign" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.VerifySignature, TitleKey = "UxVerifySignature", Glyph = "",
            Category = CommandCategory.Security, Group = MenuGroup.Primary,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "проверить подпись", "verify signature", "подписи документа" },
            Unavailable = c => !c.IsSigned ? "UxNoSignature" : null,
        };

        // ---------- Ссылки и закладки ----------
        yield return new CommandDescriptor
        {
            Id = CommandIds.OpenLink, TitleKey = "UxOpenLink", Glyph = "",
            Category = CommandCategory.View, Group = MenuGroup.Primary, Danger = CommandDanger.Safe,
            Keywords = new[] { "открыть ссылку", "перейти по ссылке" },
            Unavailable = c => c.Kind != SelectionKind.Link ? "UxWrongSelection" : null,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.CopyLinkAddress, TitleKey = "UxCopyLinkAddress", Glyph = "",
            Category = CommandCategory.Edit, Group = MenuGroup.Clipboard, Danger = CommandDanger.Safe,
            Keywords = new[] { "копировать адрес", "скопировать ссылку" },
            Unavailable = c => c.Kind != SelectionKind.Link ? "UxWrongSelection" : null,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.GoToBookmark, TitleKey = "UxGoToBookmark", Glyph = "",
            Category = CommandCategory.View, Group = MenuGroup.Primary, Danger = CommandDanger.Safe,
            Keywords = new[] { "перейти к закладке", "оглавление" },
            Unavailable = c => c.Kind != SelectionKind.Bookmark ? "UxWrongSelection" : null,
        };

        // ---------- Распознавание и печать ----------
        yield return new CommandDescriptor
        {
            Id = CommandIds.Ocr, TitleKey = "OcrMenu", Glyph = "",
            Category = CommandCategory.Recognition, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "распознать", "ocr", "скан", "текст со скана", "оцр" },
            Unavailable = c => NeedsDocument(c) ?? (!c.HasOcr ? "UxNoOcr" : null),
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.Print, TitleKey = "Print", DescriptionKey = "PrintTooltip", Glyph = "",
            Category = CommandCategory.Print, Group = MenuGroup.Primary,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+P", OpensDialog = true,
            Keywords = new[] { "печать", "распечатать", "print", "принтер" },
            Unavailable = NeedsPrintableDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.BatchPrint, TitleKey = "BpTitle", Glyph = "",
            Category = CommandCategory.Print, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "пакетная печать", "много файлов", "batch print" },
        };

        // ---------- Преобразование ----------
        yield return Tool(CommandIds.CompressPages, "CompressMenu", "",
            new[] { "сжать", "уменьшить размер", "compress", "оптимизировать картинки" });
        yield return Tool(CommandIds.OptimizeCopy, "OptimizeCopy", "",
            new[] { "оптимизировать", "линеаризовать", "быстрый просмотр в сети" });
        yield return Tool(CommandIds.ExportImages, "ExportImagesMenu", "",
            new[] { "экспорт страниц", "в картинки", "png", "jpeg" });
        yield return Tool(CommandIds.ExtractText, "ExtractTextMenu", "",
            new[] { "текст в файл", "извлечь текст", "txt" });
        yield return new CommandDescriptor
        {
            Id = CommandIds.CreateFromImages, TitleKey = "FromImagesMenu", Glyph = "",
            Category = CommandCategory.Convert, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "pdf из картинок", "собрать из фото", "сканы в pdf" },
        };
        yield return Tool(CommandIds.MergePdfs, "MergeMenu", "",
            new[] { "объединить", "склеить", "merge", "несколько файлов" });
        yield return Tool(CommandIds.CompareDocuments, "CompareMenu", "",
            new[] { "сравнить", "различия", "compare", "две версии" });
        yield return new CommandDescriptor
        {
            Id = CommandIds.BatchProcess, TitleKey = "BatchMenu", Glyph = "",
            Category = CommandCategory.Convert, Group = MenuGroup.Special,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "пакетная обработка", "много файлов", "batch" },
        };

        // ---------- Документ целиком ----------
        yield return Tool(CommandIds.ShowLayers, "LayersMenu", "",
            new[] { "слои", "ocg", "layers", "видимость слоёв" });
        yield return Tool(CommandIds.ShowAttachments, "AttachmentsMenu", "",
            new[] { "вложения", "приложенные файлы", "attachments" });

        // ---------- Окно ----------
        yield return new CommandDescriptor
        {
            Id = CommandIds.NewWindow, TitleKey = "NewWindow", Glyph = "",
            Category = CommandCategory.Window, Group = MenuGroup.Quick, Danger = CommandDanger.Safe,
            Keywords = new[] { "новое окно", "new window" },
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.NextTab, TitleKey = "UxNextTab",
            Category = CommandCategory.Window, Group = MenuGroup.Quick,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+Tab",
            Keywords = new[] { "следующая вкладка", "next tab" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.PreviousTab, TitleKey = "UxPreviousTab",
            Category = CommandCategory.Window, Group = MenuGroup.Quick,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+Shift+Tab",
            Keywords = new[] { "предыдущая вкладка", "previous tab" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.DetachTab, TitleKey = "DetachTab", Glyph = "",
            Category = CommandCategory.Window, Group = MenuGroup.Special, Danger = CommandDanger.Safe,
            Keywords = new[] { "открепить", "в отдельное окно", "detach" },
            Unavailable = NeedsDocument,
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.CommandPalette, TitleKey = "UxCommandPalette", Glyph = "",
            Category = CommandCategory.Window, Group = MenuGroup.Quick,
            Danger = CommandDanger.Safe, Shortcut = "Ctrl+K", OpensDialog = true,
            Keywords = new[] { "все команды", "палитра", "найти команду", "command palette" },
        };
        yield return new CommandDescriptor
        {
            Id = CommandIds.About, TitleKey = "About", Glyph = "",
            Category = CommandCategory.Help, Group = MenuGroup.Properties,
            Danger = CommandDanger.Safe, OpensDialog = true,
            Keywords = new[] { "о программе", "версия", "about", "лицензии" },
        };
    }

    // ----- Общие правила доступности -----

    private static string? NeedsDocument(SelectionContext c) =>
        !c.HasDocument ? "UxNoDocument" : null;

    private static string? NeedsEditableDocument(SelectionContext c) =>
        !c.HasDocument ? "UxNoDocument" : !c.AllowsEditing ? "UxEditForbidden" : null;

    private static string? NeedsPrintableDocument(SelectionContext c) =>
        !c.HasDocument ? "UxNoDocument" : !c.AllowsPrinting ? "UxPrintForbidden" : null;

    // ----- Заготовки, чтобы каталог читался, а не тонул в повторах -----

    private static CommandDescriptor Safe(
        string id, string titleKey, string glyph, CommandCategory category,
        string shortcut, string[] keywords) => new()
    {
        Id = id, TitleKey = titleKey, Glyph = glyph, Category = category,
        Group = MenuGroup.Quick, Danger = CommandDanger.Safe,
        Shortcut = shortcut, Keywords = keywords, Unavailable = NeedsDocument,
    };

    private static CommandDescriptor Page(string id, string titleKey, string glyph, string[] keywords) => new()
    {
        Id = id, TitleKey = titleKey, Glyph = glyph,
        Category = CommandCategory.Pages, Group = MenuGroup.Primary,
        SupportsMultiSelection = true, Keywords = keywords, Unavailable = NeedsEditableDocument,
    };

    private static CommandDescriptor Content(
        string id, string titleKey, string glyph, bool dialog, string[] keywords) => new()
    {
        Id = id, TitleKey = titleKey, Glyph = glyph,
        Category = CommandCategory.Content, Group = MenuGroup.Editing,
        OpensDialog = dialog, Keywords = keywords, Unavailable = NeedsEditableDocument,
    };

    private static CommandDescriptor Comment(
        string id, string titleKey, string descriptionKey, string glyph, string[] keywords) => new()
    {
        Id = id, TitleKey = titleKey, DescriptionKey = descriptionKey, Glyph = glyph,
        Category = CommandCategory.Comments, Group = MenuGroup.Editing, Keywords = keywords,
        Unavailable = NeedsEditableDocument,
    };

    /// <summary>Разметка текста: работает только по выделению, поэтому без него бессмысленна.</summary>
    private static CommandDescriptor Markup(string id, string titleKey, string glyph, string[] keywords) => new()
    {
        Id = id, TitleKey = titleKey, Glyph = glyph,
        Category = CommandCategory.Comments, Group = MenuGroup.Quick, Keywords = keywords,
        Unavailable = c => NeedsEditableDocument(c) ?? (!c.HasTextSelection ? "UxNoTextSelection" : null),
    };

    private static CommandDescriptor Tool(string id, string titleKey, string glyph, string[] keywords) => new()
    {
        Id = id, TitleKey = titleKey, Glyph = glyph,
        Category = CommandCategory.Convert, Group = MenuGroup.Special,
        Danger = CommandDanger.Safe, OpensDialog = true, Keywords = keywords,
        Unavailable = NeedsDocument,
    };
}
