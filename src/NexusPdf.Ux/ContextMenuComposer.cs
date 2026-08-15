namespace NexusPdf.Ux;

/// <summary>Пункт готового контекстного меню.</summary>
public sealed record ContextMenuItem(
    CommandDescriptor Command,
    CommandAvailability Availability,
    bool IsSeparatorBefore);

/// <summary>
/// Сборка контекстных меню из реестра команд.
///
/// Меню НЕ пишутся руками в XAML: тогда они неизбежно расходятся с панелями
/// в названиях, доступности и горячих клавишах. Здесь они собираются из тех
/// же дескрипторов, поэтому «Повернуть вправо» в меню и на панели — буквально
/// одна и та же команда.
/// </summary>
public sealed class ContextMenuComposer
{
    private readonly CommandRegistry _registry;

    public ContextMenuComposer(CommandRegistry registry) => _registry = registry;

    /// <summary>Какие команды показывать для каждого вида выделения.</summary>
    private static readonly Dictionary<SelectionKind, string[]> Menus = new()
    {
        [SelectionKind.Nothing] = new[]
        {
            CommandIds.Paste, CommandIds.SelectAllOnPage,
            CommandIds.AddText, CommandIds.InsertImage, CommandIds.AddNote,
            CommandIds.FitPage, CommandIds.FitWidth, CommandIds.ZoomActual,
            CommandIds.EditPageInPaint, CommandIds.PrintCurrentPage,
            CommandIds.DocumentProperties,
        },
        [SelectionKind.Text] = new[]
        {
            CommandIds.Copy, CommandIds.Highlight, CommandIds.Underline, CommandIds.Strikeout,
            CommandIds.AddNote, CommandIds.FindSelection, CommandIds.Redact,
        },
        [SelectionKind.TextObject] = new[]
        {
            CommandIds.EditText, CommandIds.Cut, CommandIds.Copy, CommandIds.Duplicate,
            CommandIds.BringForward, CommandIds.SendBackward,
            CommandIds.Delete, CommandIds.ObjectProperties,
        },
        [SelectionKind.Image] = new[]
        {
            CommandIds.EditImageInPaint, CommandIds.ReplaceImage, CommandIds.ExportImage,
            CommandIds.Cut, CommandIds.Copy, CommandIds.Duplicate,
            CommandIds.RotateRight, CommandIds.RotateLeft,
            CommandIds.BringForward, CommandIds.SendBackward,
            CommandIds.Delete, CommandIds.ObjectProperties,
        },
        [SelectionKind.Shape] = new[]
        {
            CommandIds.StraightenStroke, CommandIds.RestoreStroke,
            CommandIds.Copy, CommandIds.Duplicate,
            CommandIds.BringForward, CommandIds.SendBackward,
            CommandIds.Delete, CommandIds.ObjectProperties,
        },
        [SelectionKind.Annotation] = new[]
        {
            CommandIds.OpenComment, CommandIds.Copy, CommandIds.Duplicate,
            CommandIds.Delete, CommandIds.ObjectProperties,
        },
        [SelectionKind.Link] = new[]
        {
            CommandIds.OpenLink, CommandIds.CopyLinkAddress, CommandIds.Delete,
        },
        [SelectionKind.Signature] = new[]
        {
            CommandIds.VerifySignature, CommandIds.ShowCertificate, CommandIds.ObjectProperties,
        },
        [SelectionKind.Page] = new[]
        {
            CommandIds.RotateRight, CommandIds.RotateLeft, CommandIds.Rotate180,
            CommandIds.Duplicate, CommandIds.Copy,
            CommandIds.ExtractPages, CommandIds.EditPageInPaint,
            CommandIds.OcrPages, CommandIds.CompressPages, CommandIds.PrintSelectedPages,
            CommandIds.DeletePages, CommandIds.PageProperties,
        },
        [SelectionKind.Bookmark] = new[]
        {
            CommandIds.GoToBookmark, CommandIds.RenameBookmark, CommandIds.DeleteBookmark,
        },
        [SelectionKind.Layer] = new[]
        {
            CommandIds.ShowLayer, CommandIds.HideLayer, CommandIds.ShowOnlyThisLayer,
            CommandIds.ShowAllLayers,
        },
        [SelectionKind.Attachment] = new[]
        {
            CommandIds.SaveAttachmentAs, CommandIds.CopyAttachmentName,
            CommandIds.CheckAttachmentSafety,
        },
        [SelectionKind.SearchResult] = new[]
        {
            CommandIds.GoToSearchResult, CommandIds.Copy, CommandIds.Highlight, CommandIds.AddNote,
        },
        [SelectionKind.FormField] = new[]
        {
            CommandIds.PreviewField, CommandIds.Duplicate, CommandIds.Delete,
            CommandIds.ObjectProperties,
        },
    };

    /// <summary>
    /// Меню для текущего выделения. Пункты идут группами; разделитель ставится
    /// на границе групп, а опасные команды оказываются внизу автоматически —
    /// порядок задаётся <see cref="MenuGroup"/>, а не порядком в списке.
    /// </summary>
    public IReadOnlyList<ContextMenuItem> Compose(SelectionContext context)
    {
        if (!Menus.TryGetValue(context.Kind, out var ids))
            return Array.Empty<ContextMenuItem>();

        var commands = ids
            .Select(id => _registry.Find(id))
            .Where(c => c != null)
            .Select(c => c!)
            // Команды, не имеющие смысла для нескольких объектов, при
            // множественном выделении просто не показываются: недоступный
            // пункт без смысла — это шум.
            .Where(c => !context.HasMultipleSelection || c.SupportsMultiSelection)
            .OrderBy(c => (int)c.Group)
            .ToList();

        var items = new List<ContextMenuItem>(commands.Count);
        MenuGroup? previousGroup = null;

        foreach (var command in commands)
        {
            items.Add(new ContextMenuItem(
                command,
                command.Evaluate(context),
                IsSeparatorBefore: previousGroup != null && previousGroup != command.Group));
            previousGroup = command.Group;
        }
        return items;
    }

    /// <summary>
    /// Название пункта с учётом числа выделенных объектов: «Повернуть 12
    /// страниц вправо» вместо «Повернуть страницу». Без этого пользователь не
    /// понимает, сколько всего он сейчас изменит.
    /// </summary>
    public static string Title(
        CommandDescriptor command, SelectionContext context,
        Func<string, string> resolve, Func<string, object[], string> format)
    {
        if (!context.HasMultipleSelection || !command.SupportsMultiSelection)
            return resolve(command.TitleKey);

        var pluralKey = command.TitleKey + "Many";
        var plural = resolve(pluralKey);
        // Ключа множественной формы нет — показываем обычное название, а не
        // выдуманную строку с числом в скобках.
        return plural == pluralKey
            ? resolve(command.TitleKey)
            : format(pluralKey, new object[] { context.SelectedCount });
    }
}
