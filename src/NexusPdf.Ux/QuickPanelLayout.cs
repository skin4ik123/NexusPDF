namespace NexusPdf.Ux;

/// <summary>
/// Состав быстрой панели: разбор сохранённого списка команд.
///
/// Логика вынесена из окна намеренно — именно здесь ломается настройка,
/// пережившая обновление программы: команда исчезла, остались одни
/// разделители, список пуст. Всё это должно кончаться рабочей панелью, а не
/// пустой полосой без кнопок.
/// </summary>
public static class QuickPanelLayout
{
    /// <summary>Разделитель между группами кнопок.</summary>
    public const string Separator = "|";

    /// <summary>Панель по умолчанию — то, чем пользуются в первый день.</summary>
    public static IReadOnlyList<string> Default { get; } = new[]
    {
        CommandIds.Open, CommandIds.Save, CommandIds.SaveAs, CommandIds.Print,
        Separator,
        CommandIds.Undo, CommandIds.Redo,
        Separator,
        CommandIds.Find,
    };

    /// <summary>
    /// Приводит сохранённый список к рабочему виду: убирает неизвестные
    /// команды и повторы, схлопывает разделители, отбрасывает крайние.
    /// Если после чистки не осталось ни одной команды, возвращается набор по
    /// умолчанию — пустая панель не бывает чьим-то осознанным выбором.
    /// </summary>
    public static IReadOnlyList<string> Sanitize(IEnumerable<string>? ids, Func<string, bool> isKnown)
    {
        var source = ids?.ToList();
        if (source == null || source.Count == 0)
            return Default;

        var result = new List<string>(source.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in source)
        {
            if (id == Separator)
            {
                // Два разделителя подряд рисуют двойную черту, и первый
                // разделитель на панели просто висит слева от всего.
                if (result.Count > 0 && result[^1] != Separator)
                    result.Add(Separator);
                continue;
            }
            if (!isKnown(id) || !seen.Add(id))
                continue;
            result.Add(id);
        }

        while (result.Count > 0 && result[^1] == Separator)
            result.RemoveAt(result.Count - 1);

        return result.Count == 0 ? Default : result;
    }
}
