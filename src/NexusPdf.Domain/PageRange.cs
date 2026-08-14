using System.Globalization;

namespace NexusPdf.Domain;

/// <summary>
/// Разбор пользовательских диапазонов страниц вида «1,3-8,10».
/// Номера отображаются с единицы, результат — нулевые индексы в порядке перечисления.
/// </summary>
public static class PageRange
{
    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> indices, out string? error)
    {
        var result = new List<int>();
        indices = result;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Диапазон пуст.";
            return false;
        }

        foreach (var rawPart in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = rawPart.IndexOf('-', 1); // минус первым символом не считается разделителем
            if (dash < 0)
            {
                if (!TryPageNumber(rawPart, pageCount, out var page, out error))
                    return false;
                result.Add(page);
            }
            else
            {
                var fromText = rawPart[..dash].Trim();
                var toText = rawPart[(dash + 1)..].Trim();
                if (!TryPageNumber(fromText, pageCount, out var from, out error) ||
                    !TryPageNumber(toText, pageCount, out var to, out error))
                    return false;

                if (from <= to)
                    for (var i = from; i <= to; i++) result.Add(i);
                else
                    for (var i = from; i >= to; i--) result.Add(i);
            }
        }

        if (result.Count == 0)
        {
            error = "Диапазон пуст.";
            return false;
        }
        return true;
    }

    public static IReadOnlyList<int> Parse(string text, int pageCount)
    {
        if (!TryParse(text, pageCount, out var indices, out var error))
            throw new FormatException(error);
        return indices;
    }

    private static bool TryPageNumber(string text, int pageCount, out int zeroBased, out string? error)
    {
        zeroBased = -1;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var oneBased))
        {
            error = $"«{text}» — не номер страницы.";
            return false;
        }
        if (oneBased < 1 || oneBased > pageCount)
        {
            error = $"Страница {oneBased} вне документа (1–{pageCount}).";
            return false;
        }
        zeroBased = oneBased - 1;
        error = null;
        return true;
    }
}
