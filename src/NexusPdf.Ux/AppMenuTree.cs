namespace NexusPdf.Ux;

/// <summary>Раздел главного меню: заголовок и команды под ним.</summary>
public sealed record MenuSection(string TitleKey, IReadOnlyList<string> CommandIds);

/// <summary>
/// Структура главного меню программы.
///
/// Заводится потому, что плоский список из двадцати пяти пунктов подряд —
/// это не меню, а простыня: в ней ничего не найти, и она растёт с каждой новой
/// возможностью. Разделы совпадают с тем, как человек думает о задаче:
/// «мне нужно со страницами», «мне нужно защитить».
///
/// Идентификаторы берутся из реестра команд, поэтому пункт меню не может
/// разойтись с одноимённой кнопкой панели.
/// </summary>
public static class AppMenuTree
{
    public static IReadOnlyList<MenuSection> Sections { get; } = new[]
    {
        new MenuSection("MenuFile", new[]
        {
            CommandIds.Open, CommandIds.Save, CommandIds.SaveAs,
            CommandIds.Print, CommandIds.BatchPrint,
            CommandIds.DocumentProperties,
            CommandIds.DetachTab, CommandIds.NewWindow, CommandIds.CloseTab,
        }),
        new MenuSection("MenuEdit", new[]
        {
            CommandIds.Undo, CommandIds.Redo,
            CommandIds.Copy, CommandIds.SelectAllOnPage,
            CommandIds.Find, CommandIds.CommandPalette,
        }),
        new MenuSection("MenuPages", new[]
        {
            CommandIds.ToggleOrganize,
            CommandIds.RotateRight, CommandIds.RotateLeft, CommandIds.Rotate180,
            CommandIds.Duplicate, CommandIds.ExtractPages, CommandIds.DeletePages,
            CommandIds.PageProperties,
        }),
        new MenuSection("MenuContent", new[]
        {
            CommandIds.EditText, CommandIds.AddText,
            CommandIds.InsertImage, CommandIds.InsertSignatureImage,
            CommandIds.HeaderFooter, CommandIds.Watermark,
            CommandIds.EditPageInPaint, CommandIds.EditRegionInPaint, CommandIds.EditImageInPaint,
        }),
        new MenuSection("MenuComments", new[]
        {
            CommandIds.AddNote, CommandIds.Highlight, CommandIds.Underline, CommandIds.Strikeout,
            CommandIds.AddRect, CommandIds.AddEllipse,
            CommandIds.DrawPencil, CommandIds.DrawLine, CommandIds.DrawArrow,
            CommandIds.ToggleCommentsPanel,
        }),
        new MenuSection("MenuSecurity", new[]
        {
            CommandIds.Redact, CommandIds.ProtectWithPassword,
            CommandIds.SignWithCertificate, CommandIds.VerifySignature,
        }),
        new MenuSection("MenuRecognize", new[]
        {
            CommandIds.Ocr,
        }),
        new MenuSection("MenuConvert", new[]
        {
            CommandIds.ExportImages, CommandIds.ExportWord, CommandIds.ExportExcel,
            CommandIds.ExtractText,
            CommandIds.CreateFromImages, CommandIds.MergePdfs, CommandIds.CompareDocuments,
            CommandIds.CompressPages, CommandIds.OptimizeCopy,
            CommandIds.BatchProcess,
        }),
        new MenuSection("MenuDocument", new[]
        {
            CommandIds.ShowLayers, CommandIds.ShowAttachments, CommandIds.ToggleOutline,
        }),
    };

    /// <summary>Раздел «Вид» собирается отдельно: там переключатели, а не команды.</summary>
    public const string ViewSectionKey = "MenuView";

    /// <summary>Раздел настроек: тема, язык, размеры, панель.</summary>
    public const string SettingsSectionKey = "MenuSettings";
}
