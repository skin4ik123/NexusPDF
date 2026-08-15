using NexusPdf.App.Desktop.Localization;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>Группа панели инструментов: заголовок и кнопки под ним.</summary>
public sealed class ToolGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<QuickPanelItem> Items { get; init; }
}

/// <summary>
/// Панель инструментов: всё, что умеет программа, ВИДНО списком, а не спрятано
/// в меню.
///
/// Меню нужно, когда знаешь, что ищешь. Пока не знаешь — нужен список, в
/// котором видно и название, и значок, и то, что команда сейчас недоступна.
/// Строится из того же реестра, что панель и меню: третьего описания команд
/// не появляется.
/// </summary>
public sealed class ToolsPanel
{
    /// <summary>Разделы панели и их содержимое.</summary>
    private static readonly (string TitleKey, string[] Commands)[] Layout =
    {
        ("MenuPages", new[]
        {
            CommandIds.ToggleOrganize, CommandIds.RotateRight, CommandIds.RotateLeft,
            CommandIds.Rotate180, CommandIds.Duplicate, CommandIds.ExtractPages,
            CommandIds.DeletePages,
        }),
        ("MenuContent", new[]
        {
            CommandIds.EditText, CommandIds.AddText, CommandIds.InsertImage,
            CommandIds.InsertSignatureImage, CommandIds.HeaderFooter, CommandIds.Watermark,
            CommandIds.EditPageInPaint, CommandIds.EditRegionInPaint, CommandIds.EditImageInPaint,
        }),
        ("MenuComments", new[]
        {
            CommandIds.AddNote, CommandIds.Highlight, CommandIds.Underline, CommandIds.Strikeout,
            CommandIds.AddRect, CommandIds.AddEllipse,
            CommandIds.DrawPencil, CommandIds.DrawLine, CommandIds.DrawArrow,
        }),
        ("MenuSecurity", new[]
        {
            CommandIds.Redact, CommandIds.ProtectWithPassword,
            CommandIds.SignWithCertificate, CommandIds.VerifySignature,
        }),
        ("MenuRecognize", new[] { CommandIds.Ocr }),
        ("MenuConvert", new[]
        {
            CommandIds.ExportImages, CommandIds.ExtractText, CommandIds.CreateFromImages,
            CommandIds.MergePdfs, CommandIds.CompareDocuments,
            CommandIds.CompressPages, CommandIds.OptimizeCopy, CommandIds.BatchProcess,
        }),
        ("MenuDocument", new[]
        {
            CommandIds.DocumentProperties, CommandIds.ShowLayers, CommandIds.ShowAttachments,
        }),
        ("MenuPrint", new[] { CommandIds.Print, CommandIds.BatchPrint }),
    };

    public ToolsPanel(UxCommandHub hub)
    {
        Groups = Layout
            .Select(g => new ToolGroup
            {
                Title = Loc.Get(g.TitleKey),
                Items = g.Commands
                    .Where(id => hub.Registry.Find(id) != null)
                    .Select(id => new QuickPanelItem(hub, id))
                    .ToList(),
            })
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    public IReadOnlyList<ToolGroup> Groups { get; }
}
