namespace NexusPdf.Ux;

/// <summary>Установленная гарнитура для списка выбора.</summary>
/// <param name="IsLarge">Файл шрифта настолько велик, что заметно раздует PDF.</param>
public sealed record FontChoice(string Family, bool HasCyrillic, bool Embeddable, bool Supported, bool IsLarge = false);

public enum FontGroup { Recent, InDocument, All }

/// <summary>Строка списка шрифтов.</summary>
/// <param name="Family">Показываемое имя (у шрифта документа — его имя из PDF).</param>
/// <param name="SubstituteFamily">Чем будет писаться шрифт документа; null — самой гарнитурой.</param>
/// <param name="NoteKey">Ключ строки-пояснения: почему нельзя выбрать или о чём предупредить.</param>
public sealed record FontPickerItem(FontGroup Group, string Family, string? SubstituteFamily, bool Enabled, string? NoteKey)
{
    /// <summary>Гарнитура, которую получит текст при выборе этой строки.</summary>
    public string AppliedFamily => SubstituteFamily ?? Family;
}

/// <summary>
/// Список шрифтов в духе редакторов вёрстки: недавние, шрифты документа, все
/// установленные. Ничего не прячется: шрифт, который нельзя вписать в PDF,
/// виден с причиной, но не выбирается.
/// </summary>
public static class FontPickerModel
{
    public static IReadOnlyList<FontPickerItem> Build(
        IReadOnlyList<FontChoice> all, IReadOnlyList<string> recent,
        IReadOnlyList<(string PdfName, string? Family)> inDocument, string query)
    {
        var byName = new Dictionary<string, FontChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in all)
            byName.TryAdd(choice.Family, choice);
        bool Matches(string name) =>
            string.IsNullOrWhiteSpace(query) || name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

        var items = new List<FontPickerItem>();
        var seenRecent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in recent)
        {
            if (byName.TryGetValue(name, out var choice) && seenRecent.Add(choice.Family) && Matches(choice.Family))
                items.Add(Item(FontGroup.Recent, choice));
        }

        var seenDocument = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pdfName, family) in inDocument)
        {
            // Шрифт, которому нечем найти замену, выбирать бессмысленно.
            if (family == null || !byName.TryGetValue(family, out var substitute))
                continue;
            var shown = DisplayName(pdfName);
            if (shown.Length == 0 || !seenDocument.Add(shown) || !Matches(shown))
                continue;
            var same = Compact(shown) == Compact(substitute.Family);
            items.Add(new FontPickerItem(FontGroup.InDocument, shown, substitute.Family,
                substitute.Embeddable && substitute.Supported, same ? null : "FontWrittenWith"));
        }

        items.AddRange(all
            .Where(c => Matches(c.Family))
            .DistinctBy(c => c.Family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Family, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => Item(FontGroup.All, c)));
        return items;
    }

    private static FontPickerItem Item(FontGroup group, FontChoice choice)
    {
        var note = !choice.Supported ? "FontNotSupported"
            : !choice.Embeddable ? "FontNotEmbeddable"
            : !choice.HasCyrillic ? "FontNoCyrillic"
            : choice.IsLarge ? "FontLarge"
            : null;
        return new FontPickerItem(group, choice.Family, null, choice.Supported && choice.Embeddable, note);
    }

    /// <summary>Имя шрифта из PDF без приставки подмножества («ABCDEF+») и начертания после «-» или «,».</summary>
    public static string DisplayName(string pdfName)
    {
        var name = pdfName.Trim();
        var plus = name.IndexOf('+');
        if (plus == 6 && name[..6].All(char.IsAsciiLetterUpper))
            name = name[7..];
        var cut = name.IndexOfAny([',', '-']);
        return (cut > 0 ? name[..cut] : name).Trim();
    }

    private static string Compact(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
