namespace NexusPdf.Ux;

/// <summary>Найденная команда с оценкой соответствия запросу.</summary>
public sealed record CommandMatch(CommandDescriptor Command, int Score, CommandAvailability Availability);

/// <summary>
/// Реестр команд. Единственный источник сведений о командах для всех точек
/// интерфейса. Поиск понимает русские синонимы и опечатки: пользователь ищет
/// «перевернуть», а команда называется «Повернуть», и не найти её — значит
/// заставить его лезть в меню.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _byId = new(StringComparer.Ordinal);

    public CommandRegistry(IEnumerable<CommandDescriptor> commands)
    {
        foreach (var command in commands)
        {
            if (_byId.ContainsKey(command.Id))
                throw new InvalidOperationException(
                    $"Команда «{command.Id}» зарегистрирована дважды. " +
                    "Одно действие — одна запись, иначе точки интерфейса разойдутся.");
            _byId[command.Id] = command;
        }
    }

    public IReadOnlyCollection<CommandDescriptor> All => _byId.Values;

    public CommandDescriptor? Find(string id) => _byId.GetValueOrDefault(id);

    public CommandDescriptor Require(string id) =>
        _byId.TryGetValue(id, out var command)
            ? command
            : throw new KeyNotFoundException($"Команда «{id}» не зарегистрирована.");

    /// <summary>
    /// Поиск для командной палитры. Сортировка: сначала подходящие текущему
    /// выделению, потом остальные; недоступные не выбрасываются, а показываются
    /// с причиной — так пользователь понимает, что нужно сделать сначала.
    /// </summary>
    /// <param name="titleResolver">Перевод ключа названия — реестр не знает про локализацию.</param>
    public IReadOnlyList<CommandMatch> Search(
        string query, SelectionContext context, Func<string, string> titleResolver, int limit = 30)
    {
        var normalized = Normalize(query);
        var results = new List<CommandMatch>();

        foreach (var command in _byId.Values)
        {
            var availability = command.Evaluate(context);
            var title = titleResolver(command.TitleKey);

            var score = normalized.Length == 0
                ? 1
                : ScoreOf(command, title, normalized);
            if (score <= 0) continue;

            results.Add(new CommandMatch(command, score, availability));
        }

        // Доступность важнее качества совпадения: показывать сверху команду,
        // которую нельзя выполнить, — плохой совет, даже если её название
        // совпало с запросом буква в букву. Недоступные не выбрасываются
        // совсем — они уходят вниз вместе с причиной.
        return results
            .OrderByDescending(r => r.Availability.IsAvailable)
            .ThenByDescending(r => r.Score)
            .ThenBy(r => titleResolver(r.Command.TitleKey), StringComparer.CurrentCulture)
            .Take(limit)
            .ToList();
    }

    private static int ScoreOf(CommandDescriptor command, string title, string query)
    {
        var normalizedTitle = Normalize(title);

        if (normalizedTitle == query) return 1000;
        if (normalizedTitle.StartsWith(query, StringComparison.Ordinal)) return 700;
        if (normalizedTitle.Contains(query, StringComparison.Ordinal)) return 500;

        foreach (var keyword in command.Keywords)
        {
            var normalizedKeyword = Normalize(keyword);
            if (normalizedKeyword == query) return 600;
            if (normalizedKeyword.StartsWith(query, StringComparison.Ordinal)) return 400;
            if (normalizedKeyword.Contains(query, StringComparison.Ordinal)) return 300;
        }

        // Нечёткое совпадение: буквы запроса идут по порядку внутри названия.
        // Так «пвстр» находит «Повернуть страницы».
        if (IsSubsequence(query, normalizedTitle)) return 150;

        foreach (var keyword in command.Keywords)
        {
            if (IsSubsequence(query, Normalize(keyword))) return 100;
        }
        return 0;
    }

    /// <summary>
    /// Приведение к сравнимому виду. «ё» и «е» смешиваются намеренно: их
    /// путают при вводе постоянно, и «поворот» не должен теряться из-за буквы.
    /// </summary>
    public static string Normalize(string text) =>
        text.ToLowerInvariant()
            .Replace('ё', 'е')
            .Replace("…", "")
            .Replace("&", "")
            .Trim();

    private static bool IsSubsequence(string needle, string haystack)
    {
        if (needle.Length == 0) return true;
        var index = 0;
        foreach (var c in haystack)
        {
            if (c == needle[index] && ++index == needle.Length) return true;
        }
        return false;
    }
}
