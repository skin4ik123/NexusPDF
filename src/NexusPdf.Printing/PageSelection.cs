using System.Globalization;

namespace NexusPdf.Printing;

/// <summary>Какие страницы документа попадают в задание.</summary>
public enum PageScope
{
    All,
    CurrentPage,
    Selected,
    Range,
}

/// <summary>Дополнительный фильтр поверх выбранного объёма.</summary>
public enum PageParity
{
    All,
    OddOnly,
    EvenOnly,
}

/// <summary>Результат разбора диапазона: либо номера, либо понятная причина отказа.</summary>
public sealed record PageRangeResult(
    IReadOnlyList<int> Indices,
    string? Error,
    string Normalized)
{
    public bool IsValid => Error == null;

    public static PageRangeResult Failure(string error) =>
        new(Array.Empty<int>(), error, "");
}

/// <summary>
/// Разбор пользовательского диапазона страниц. Поддерживает всё, что человек
/// реально пишет: 1-5, 1,3,7, 10- (до конца), -10 (с начала), обратный 10-1,
/// повторы 1,1,2 и логические метки страниц.
/// Номера на входе и в нормализованном выводе — ОДИН-БАЗНЫЕ, наружу отдаются
/// нуль-базные индексы: путаница между ними здесь была бы самой дорогой ошибкой.
/// </summary>
public static class PageRangeParser
{
    /// <param name="pageCount">Число страниц документа.</param>
    /// <param name="labels">
    /// Логические метки страниц («iv», «A-3»), если они есть. Метка ищется
    /// раньше числа: в документе с римской нумерацией «4» — это метка, а не
    /// четвёртая страница файла.
    /// </param>
    public static PageRangeResult Parse(string? text, int pageCount, IReadOnlyList<string>? labels = null)
    {
        if (pageCount <= 0)
            return PageRangeResult.Failure("В документе нет страниц.");
        if (string.IsNullOrWhiteSpace(text))
            return PageRangeResult.Failure("Укажите диапазон, например 1-5 или 1,3,7.");

        var result = new List<int>();
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return PageRangeResult.Failure("Укажите диапазон, например 1-5 или 1,3,7.");

        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;

            // Дефис в середине — диапазон; в начале или конце — открытая граница.
            var dash = FindRangeSeparator(part);
            if (dash < 0)
            {
                var single = ResolveOne(part, pageCount, labels);
                if (single == null)
                    return PageRangeResult.Failure($"Не удалось понять «{part}».");
                result.Add(single.Value);
                continue;
            }

            var leftText = part[..dash].Trim();
            var rightText = part[(dash + 1)..].Trim();

            int from, to;
            if (leftText.Length == 0 && rightText.Length == 0)
                return PageRangeResult.Failure($"Не удалось понять «{part}».");

            if (leftText.Length == 0)
            {
                // «-10»: с первой страницы по десятую.
                var right = ResolveOne(rightText, pageCount, labels);
                if (right == null) return PageRangeResult.Failure($"Не удалось понять «{part}».");
                from = 0;
                to = right.Value;
            }
            else if (rightText.Length == 0)
            {
                // «10-»: с десятой до конца.
                var left = ResolveOne(leftText, pageCount, labels);
                if (left == null) return PageRangeResult.Failure($"Не удалось понять «{part}».");
                from = left.Value;
                to = pageCount - 1;
            }
            else
            {
                var left = ResolveOne(leftText, pageCount, labels);
                var right = ResolveOne(rightText, pageCount, labels);
                if (left == null || right == null)
                    return PageRangeResult.Failure($"Не удалось понять «{part}».");
                from = left.Value;
                to = right.Value;
            }

            if (from <= to)
                for (var i = from; i <= to; i++) result.Add(i);
            else
                // Обратный диапазон 10-1 печатает страницы в обратном порядке —
                // это осмысленное намерение, а не ошибка ввода.
                for (var i = from; i >= to; i--) result.Add(i);
        }

        if (result.Count == 0)
            return PageRangeResult.Failure("Под указанный диапазон не попала ни одна страница.");

        return new PageRangeResult(result, null, Normalize(result));
    }

    /// <summary>
    /// Позиция дефиса, который разделяет диапазон. Дефис на краю строки —
    /// открытая граница и разделителем не считается.
    /// </summary>
    private static int FindRangeSeparator(string part)
    {
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] != '-') continue;
            if (i == 0) return 0;                 // «-10»
            if (i == part.Length - 1) return i;   // «10-»
            return i;
        }
        return -1;
    }

    /// <summary>Один элемент: сначала логическая метка, потом физический номер.</summary>
    private static int? ResolveOne(string token, int pageCount, IReadOnlyList<string>? labels)
    {
        if (labels != null)
        {
            for (var i = 0; i < labels.Count && i < pageCount; i++)
            {
                if (string.Equals(labels[i], token, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return null;
        if (number < 1 || number > pageCount)
            return null;
        return number - 1;
    }

    /// <summary>
    /// Человеческая запись результата: «1, 2, 3, 8, 10-12». Соседние номера
    /// сворачиваются, повторы и обратный порядок сохраняются как есть — иначе
    /// пользователь не увидит, что именно он попросил.
    /// </summary>
    public static string Normalize(IReadOnlyList<int> zeroBased)
    {
        if (zeroBased.Count == 0) return "";

        var parts = new List<string>();
        var runStart = zeroBased[0];
        var runEnd = zeroBased[0];

        for (var i = 1; i <= zeroBased.Count; i++)
        {
            var isContinuation = i < zeroBased.Count && zeroBased[i] == runEnd + 1;
            if (isContinuation)
            {
                runEnd = zeroBased[i];
                continue;
            }

            parts.Add(runStart == runEnd
                ? (runStart + 1).ToString(CultureInfo.InvariantCulture)
                : runEnd - runStart == 1
                    ? $"{runStart + 1}, {runEnd + 1}"
                    : $"{runStart + 1}-{runEnd + 1}");

            if (i < zeroBased.Count)
            {
                runStart = zeroBased[i];
                runEnd = zeroBased[i];
            }
        }

        return string.Join(", ", parts);
    }
}

/// <summary>Настройки выбора страниц — то, что пользователь задал в интерфейсе.</summary>
public sealed record PageSelection
{
    public PageScope Scope { get; init; } = PageScope.All;
    public string? RangeText { get; init; }

    /// <summary>Страницы, выбранные мышью в панели, — для Scope = Selected.</summary>
    public IReadOnlyList<int> ExplicitIndices { get; init; } = Array.Empty<int>();

    /// <summary>Текущая страница — для Scope = CurrentPage.</summary>
    public int CurrentPageIndex { get; init; }

    public PageParity Parity { get; init; } = PageParity.All;
    public bool ReverseOrder { get; init; }

    /// <summary>Повторить каждую страницу указанное число раз.</summary>
    public int RepeatEachPage { get; init; } = 1;

    /// <summary>
    /// Разворачивает настройки в конкретный список нуль-базных индексов.
    /// Порядок применения важен: сначала объём, потом чётность, потом повтор,
    /// и только в самом конце обратный порядок — иначе «обратный порядок с
    /// повтором» дал бы перемешанные копии.
    /// </summary>
    public PageRangeResult Resolve(int pageCount, IReadOnlyList<string>? labels = null)
    {
        IReadOnlyList<int> indices;
        switch (Scope)
        {
            case PageScope.All:
                indices = Enumerable.Range(0, pageCount).ToList();
                break;

            case PageScope.CurrentPage:
                if (CurrentPageIndex < 0 || CurrentPageIndex >= pageCount)
                    return PageRangeResult.Failure("Текущая страница вне документа.");
                indices = new[] { CurrentPageIndex };
                break;

            case PageScope.Selected:
                indices = ExplicitIndices.Where(i => i >= 0 && i < pageCount).ToList();
                if (indices.Count == 0)
                    return PageRangeResult.Failure("Не выбрано ни одной страницы.");
                break;

            default:
                var parsed = PageRangeParser.Parse(RangeText, pageCount, labels);
                if (!parsed.IsValid) return parsed;
                indices = parsed.Indices;
                break;
        }

        var filtered = Parity switch
        {
            // Чётность считается по номеру страницы для человека, с единицы.
            PageParity.OddOnly => indices.Where(i => (i + 1) % 2 == 1).ToList(),
            PageParity.EvenOnly => indices.Where(i => (i + 1) % 2 == 0).ToList(),
            _ => indices.ToList(),
        };

        if (filtered.Count == 0)
            return PageRangeResult.Failure("Под выбранный фильтр не попала ни одна страница.");

        var repeat = Math.Max(1, RepeatEachPage);
        if (repeat > 1)
            filtered = filtered.SelectMany(i => Enumerable.Repeat(i, repeat)).ToList();

        if (ReverseOrder)
            filtered.Reverse();

        return new PageRangeResult(filtered, null, PageRangeParser.Normalize(filtered));
    }
}
