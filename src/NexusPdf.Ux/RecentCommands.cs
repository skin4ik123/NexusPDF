namespace NexusPdf.Ux;

/// <summary>
/// Недавно использованные команды.
///
/// У каждого своя пятёрка привычных действий, и она не совпадает ни с чьей
/// другой: один каждый день поворачивает сканы, другой жмёт файлы, третий
/// подписывает. Держать их наверху панели дешевле, чем заставлять человека
/// каждый раз искать одно и то же — и честнее, чем угадывать «популярное»
/// за него.
///
/// Список ведёт себя как стопка: последнее использованное всегда первое,
/// повторы не копятся, длина ограничена — иначе «недавнее» превращается во
/// вторую простыню.
/// </summary>
public static class RecentCommands
{
    /// <summary>Сколько помнить. Больше семи глаз уже не охватывает одним взглядом.</summary>
    public const int Limit = 6;

    /// <summary>
    /// Новый список после использования команды: она встаёт первой, прежнее
    /// вхождение убирается, хвост обрезается.
    /// </summary>
    public static IReadOnlyList<string> Use(IReadOnlyList<string> recent, string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return recent;

        var result = new List<string>(Limit) { commandId };
        foreach (var id in recent)
        {
            if (result.Count >= Limit) break;
            if (id == commandId || string.IsNullOrWhiteSpace(id)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Разбор сохранённого списка: неизвестные команды (после обновления
    /// программы или правки настроек руками) отбрасываются молча.
    /// </summary>
    public static IReadOnlyList<string> Sanitize(IEnumerable<string>? saved, Func<string, bool> isKnown)
    {
        if (saved == null) return Array.Empty<string>();
        var result = new List<string>(Limit);
        foreach (var id in saved)
        {
            if (result.Count >= Limit) break;
            if (string.IsNullOrWhiteSpace(id) || result.Contains(id) || !isKnown(id)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Стоит ли вообще показывать раздел. Одна кнопка «недавнего» — это не
    /// помощь, а лишняя строка: раздел появляется с двух.
    /// </summary>
    public static bool WorthShowing(IReadOnlyList<string> recent) => recent.Count >= 2;
}
