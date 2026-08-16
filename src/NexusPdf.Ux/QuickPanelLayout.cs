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

    /// <summary>
    /// Панель по умолчанию — то, чем пользуются в первый день.
    ///
    /// Систематизация и оптимизация вынесены сюда наравне с сохранением и
    /// печатью: это не редкие настройки, а то, ради чего документ открывают —
    /// разобрать страницы и привести файл в порядок. Искать их в меню каждый
    /// раз значит делать частую работу неудобной.
    /// </summary>
    public static IReadOnlyList<string> Default { get; } = new[]
    {
        CommandIds.Open, CommandIds.Save, CommandIds.SaveAs, CommandIds.Print,
        Separator,
        CommandIds.Undo, CommandIds.Redo,
        Separator,
        CommandIds.ToggleOrganize, CommandIds.OptimizeDocument,
        Separator,
        CommandIds.Find,
    };

    /// <summary>
    /// Поколение набора по умолчанию. Растёт, когда в панель добавляются новые
    /// кнопки, и по нему решается, доливать ли их в уже настроенный список.
    /// </summary>
    public const int Generation = 1;

    /// <summary>
    /// Что добавилось в каждом поколении. Только это и доливается в сохранённую
    /// панель: остальной набор по умолчанию пользователь мог убрать намеренно, и
    /// возвращать его обновлением нельзя.
    /// </summary>
    private static readonly Dictionary<int, string[]> AddedIn = new()
    {
        [1] = new[] { CommandIds.ToggleOrganize, CommandIds.OptimizeDocument },
    };

    /// <summary>
    /// Доливает в сохранённую панель кнопки, появившиеся после того, как её
    /// настраивали. Возвращает новый список и поколение, до которого он дотянут.
    /// </summary>
    public static (IReadOnlyList<string> Ids, int Generation) Upgrade(
        IReadOnlyList<string>? saved, int savedGeneration)
    {
        // Пустая настройка означает «умолчание»: доливать в неё нечего, она и
        // так всегда равна нынешнему набору.
        if (saved == null || saved.Count == 0)
            return (Default, Generation);
        if (savedGeneration >= Generation)
            return (saved, Generation);

        var result = saved.ToList();
        var present = new HashSet<string>(result, StringComparer.Ordinal);
        var added = new List<string>();
        for (var g = savedGeneration + 1; g <= Generation; g++)
        {
            if (!AddedIn.TryGetValue(g, out var ids)) continue;
            foreach (var id in ids)
                if (present.Add(id)) added.Add(id);
        }

        if (added.Count > 0)
        {
            // Новое ставится отдельной группой в конец: вклиниваться в порядок,
            // который человек выстроил сам, обновление не должно.
            result.Add(Separator);
            result.AddRange(added);
        }
        return (result, Generation);
    }

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
