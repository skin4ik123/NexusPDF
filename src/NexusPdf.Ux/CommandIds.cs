namespace NexusPdf.Ux;

/// <summary>
/// Постоянные идентификаторы команд.
///
/// Значения не меняются никогда: на них ссылаются сохранённые настройки
/// панелей, профили рабочих пространств и назначенные пользователем клавиши.
/// Переименование идентификатора молча ломает чужие настройки.
/// </summary>
public static class CommandIds
{
    // Файл
    public const string Open = "file.open";
    public const string Save = "file.save";
    public const string SaveAs = "file.saveAs";
    public const string CloseTab = "file.closeTab";
    public const string DocumentProperties = "file.properties";

    // Правка
    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string Cut = "edit.cut";
    public const string Copy = "edit.copy";
    public const string Paste = "edit.paste";
    public const string Duplicate = "edit.duplicate";
    public const string Delete = "edit.delete";
    public const string SelectAllOnPage = "edit.selectAllOnPage";
    public const string ObjectProperties = "edit.objectProperties";

    // Просмотр
    public const string ZoomIn = "view.zoomIn";
    public const string ZoomOut = "view.zoomOut";
    public const string ZoomActual = "view.zoomActual";
    public const string FitWidth = "view.fitWidth";
    public const string FitPage = "view.fitPage";
    public const string Find = "view.find";
    public const string FindSelection = "view.findSelection";
    public const string GoToSearchResult = "view.goToSearchResult";

    // Страницы
    public const string RotateLeft = "pages.rotateLeft";
    public const string RotateRight = "pages.rotateRight";
    public const string Rotate180 = "pages.rotate180";
    public const string DeletePages = "pages.delete";
    public const string ExtractPages = "pages.extract";
    public const string PageProperties = "pages.properties";
    public const string PrintSelectedPages = "pages.printSelected";
    public const string PrintCurrentPage = "pages.printCurrent";
    public const string CompressPages = "pages.compress";

    // Содержимое
    public const string AddText = "content.addText";
    public const string EditText = "content.editText";
    public const string InsertImage = "content.insertImage";
    public const string ReplaceImage = "content.replaceImage";
    public const string ExportImage = "content.exportImage";
    public const string EditImageInPaint = "content.editImageInPaint";
    public const string EditPageInPaint = "content.editPageInPaint";
    public const string BringForward = "content.bringForward";
    public const string SendBackward = "content.sendBackward";
    public const string HeaderFooter = "content.headerFooter";
    public const string Watermark = "content.watermark";

    // Комментарии
    public const string AddNote = "comments.addNote";
    public const string OpenComment = "comments.open";
    public const string Highlight = "comments.highlight";
    public const string Underline = "comments.underline";
    public const string Strikeout = "comments.strikeout";
    public const string DrawPencil = "comments.pencil";
    public const string DrawLine = "comments.line";
    public const string DrawArrow = "comments.arrow";
    public const string StraightenStroke = "comments.straighten";
    public const string RestoreStroke = "comments.restoreStroke";

    // Формы
    public const string ToggleFormMode = "forms.toggleMode";
    public const string PreviewField = "forms.previewField";

    // Защита
    public const string Redact = "security.redact";
    public const string ProtectWithPassword = "security.protect";
    public const string SignWithCertificate = "security.sign";
    public const string VerifySignature = "security.verify";
    public const string ShowCertificate = "security.showCertificate";

    // Ссылки
    public const string OpenLink = "link.open";
    public const string CopyLinkAddress = "link.copyAddress";

    // Закладки, слои, вложения
    public const string GoToBookmark = "bookmark.goTo";
    public const string RenameBookmark = "bookmark.rename";
    public const string DeleteBookmark = "bookmark.delete";
    public const string ShowLayer = "layer.show";
    public const string HideLayer = "layer.hide";
    public const string ShowOnlyThisLayer = "layer.showOnlyThis";
    public const string ShowAllLayers = "layer.showAll";
    public const string SaveAttachmentAs = "attachment.saveAs";
    public const string CopyAttachmentName = "attachment.copyName";
    public const string CheckAttachmentSafety = "attachment.checkSafety";

    // Распознавание и печать
    public const string Ocr = "ocr.recognize";
    public const string OcrPages = "ocr.recognizePages";
    public const string Print = "print.open";
    public const string BatchPrint = "print.batch";

    // Окно
    public const string CommandPalette = "window.commandPalette";
    public const string NewWindow = "window.new";
}
