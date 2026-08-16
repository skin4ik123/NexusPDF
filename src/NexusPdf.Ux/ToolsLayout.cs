namespace NexusPdf.Ux;

/// <summary>Раздел панели инструментов: ключ названия и команды по порядку.</summary>
public sealed record ToolsGroupLayout(string TitleKey, IReadOnlyList<string> Commands);

/// <summary>
/// Состав панели инструментов: какие команды, в каких разделах и в каком
/// порядке. Пользователь переставляет их мышью, поэтому раскладку нужно
/// хранить и, главное, чинить при обновлении программы.
///
/// Правило, ради которого всё это здесь: команда, появившаяся в новой версии,
/// обязана попасть в панель САМА. Иначе человек с сохранённой раскладкой
/// никогда не увидит новых возможностей и будет уверен, что их нет.
/// </summary>
public static class ToolsLayout
{
    /// <summary>Раскладка по умолчанию — она же источник разделов и «новых» команд.</summary>
    public static IReadOnlyList<ToolsGroupLayout> Default { get; } = new[]
    {
        new ToolsGroupLayout("MenuPages", new[]
        {
            CommandIds.ToggleOrganize, CommandIds.RotateRight, CommandIds.RotateLeft,
            CommandIds.Rotate180, CommandIds.Duplicate, CommandIds.ExtractPages,
            CommandIds.DeletePages,
        }),
        new ToolsGroupLayout("MenuContent", new[]
        {
            CommandIds.EditText, CommandIds.AddText, CommandIds.InsertImage,
            CommandIds.InsertSignatureImage, CommandIds.HeaderFooter, CommandIds.Watermark,
            CommandIds.EditPageInPaint, CommandIds.EditRegionInPaint, CommandIds.EditImageInPaint,
        }),
        new ToolsGroupLayout("MenuComments", new[]
        {
            CommandIds.AddNote, CommandIds.Highlight, CommandIds.Underline, CommandIds.Strikeout,
            CommandIds.AddRect, CommandIds.AddEllipse,
            CommandIds.DrawPencil, CommandIds.DrawLine, CommandIds.DrawArrow,
        }),
        new ToolsGroupLayout("MenuSecurity", new[]
        {
            CommandIds.Redact, CommandIds.ProtectWithPassword,
            CommandIds.SignWithCertificate, CommandIds.VerifySignature,
        }),
        new ToolsGroupLayout("MenuRecognize", new[] { CommandIds.Ocr }),
        new ToolsGroupLayout("MenuConvert", new[]
        {
            CommandIds.ExportImages, CommandIds.ExportExcel, CommandIds.ExtractText,
            CommandIds.CreateFromImages,
            CommandIds.MergePdfs, CommandIds.CompareDocuments,
            CommandIds.CompressPages, CommandIds.OptimizeCopy, CommandIds.EnhanceScans,
            CommandIds.BatchProcess,
        }),
        new ToolsGroupLayout("MenuDocument", new[]
        {
            CommandIds.DocumentProperties, CommandIds.ShowLayers, CommandIds.ShowAttachments,
        }),
        new ToolsGroupLayout("MenuPrint", new[] { CommandIds.Print, CommandIds.PrintQueue, CommandIds.BatchPrint }),
    };

    /// <summary>
    /// Приводит сохранённую раскладку к рабочему виду: выбрасывает исчезнувшие
    /// команды и повторы, дописывает новые в их раздел по умолчанию, сохраняет
    /// порядок разделов из умолчания и убирает опустевшие.
    /// </summary>
    public static IReadOnlyList<ToolsGroupLayout> Sanitize(
        IReadOnlyList<ToolsGroupLayout>? saved, Func<string, bool> isKnown)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in saved ?? Array.Empty<ToolsGroupLayout>())
        {
            var list = byKey.TryGetValue(group.TitleKey, out var existing) ? existing : new List<string>();
            foreach (var id in group.Commands)
            {
                if (!isKnown(id) || !placed.Add(id)) continue;
                list.Add(id);
            }
            byKey[group.TitleKey] = list;
        }

        var result = new List<ToolsGroupLayout>();
        foreach (var group in Default)
        {
            var list = byKey.TryGetValue(group.TitleKey, out var saved2) ? saved2 : new List<string>();

            // Команды, которых не было в сохранённой раскладке: они появились в
            // новой версии программы и обязаны показаться сами.
            foreach (var id in group.Commands)
            {
                if (!isKnown(id) || placed.Contains(id)) continue;
                placed.Add(id);
                list.Add(id);
            }
            if (list.Count > 0)
                result.Add(new ToolsGroupLayout(group.TitleKey, list));
        }

        // Разделы, которых нет в умолчании (переименовали ключ) — их команды
        // уже разошлись по своим разделам выше, добавлять нечего.
        return result.Count > 0 ? result : Default;
    }

    /// <summary>Строка настроек: «раздел:команда|команда;раздел:…».</summary>
    public static string ToSetting(IReadOnlyList<ToolsGroupLayout> groups) =>
        string.Join(";", groups.Select(g => g.TitleKey + ":" + string.Join("|", g.Commands)));

    public static IReadOnlyList<ToolsGroupLayout>? FromSetting(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;
        var groups = new List<ToolsGroupLayout>();
        foreach (var part in setting.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var key = part[..colon];
            var ids = part[(colon + 1)..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(i => i.Trim())
                .Where(i => i.Length > 0)
                .ToList();
            groups.Add(new ToolsGroupLayout(key, ids));
        }
        return groups.Count > 0 ? groups : null;
    }
}
