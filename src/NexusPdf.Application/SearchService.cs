using System.Collections.Concurrent;

namespace NexusPdf.Application;

public sealed record SearchMatch(int LogicalPageIndex, int CharIndex, int Length, string Snippet);

/// <summary>
/// Поиск по текстовому слою: текст страниц извлекается движком один раз и
/// кэшируется на время жизни источника, совпадения ищутся управляемым кодом.
/// </summary>
public sealed class SearchService
{
    private readonly ConcurrentDictionary<(Guid SourceId, int PageIndex), string> _textCache = new();

    public async Task<IReadOnlyList<SearchMatch>> SearchAsync(
        OpenedDocument document, string query, bool caseSensitive, CancellationToken ct)
    {
        var matches = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query))
            return matches;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var pages = document.Session.Model.Pages;

        for (var logicalIndex = 0; logicalIndex < pages.Count; logicalIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[logicalIndex];
            var key = (page.SourceId, page.SourcePageIndex);
            if (!_textCache.TryGetValue(key, out var text))
            {
                text = await document.Handles[page.SourceId]
                    .GetPageTextAsync(page.SourcePageIndex, ct).ConfigureAwait(false);
                _textCache[key] = text;
            }

            var start = 0;
            while (start < text.Length)
            {
                var found = text.IndexOf(query, start, comparison);
                if (found < 0) break;
                matches.Add(new SearchMatch(logicalIndex, found, query.Length, BuildSnippet(text, found, query.Length)));
                start = found + Math.Max(1, query.Length);
            }
        }

        return matches;
    }

    public void InvalidateSource(Guid sourceId)
    {
        foreach (var key in _textCache.Keys.Where(k => k.SourceId == sourceId).ToList())
            _textCache.TryRemove(key, out _);
    }

    private static string BuildSnippet(string text, int index, int length)
    {
        const int context = 32;
        var from = Math.Max(0, index - context);
        var to = Math.Min(text.Length, index + length + context);
        var snippet = text[from..to].Replace('\r', ' ').Replace('\n', ' ');
        return (from > 0 ? "…" : "") + snippet + (to < text.Length ? "…" : "");
    }
}
