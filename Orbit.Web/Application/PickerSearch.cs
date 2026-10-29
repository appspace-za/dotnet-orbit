namespace Orbit.Application;

/// <summary>
/// How a searchable choice list narrows as you type (spec §6.2, §13 item 121): every word typed must appear in an option's text
/// or its detail line, ignoring case, and options whose text starts with what was typed come first. The leading
/// <em>pinned</em> options - "All", "None", "(standalone task)" and the like - always stay at the top, so you can get back to
/// them whatever is typed.
/// </summary>
public static class PickerSearch
{
    /// <summary>PCK-008: the words of a search, split on any whitespace; none for a blank one.</summary>
    public static IReadOnlyList<string> Words(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// PCK-001 to PCK-007, PCK-009: the options matching <paramref name="query"/>. The first <paramref name="pinned"/> items
    /// always come first; a blank query returns the rest in their order; otherwise every word must be in the text or the detail,
    /// and the ones whose text starts with the query come first, each group in its original order.
    /// </summary>
    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string? query, Func<T, string> text, Func<T, string?>? detail = null, int pinned = 0)
    {
        var all = items as IReadOnlyList<T> ?? items.ToList();
        var keep = Math.Clamp(pinned, 0, all.Count);
        var words = Words(query);
        var head = all.Take(keep);
        var rest = all.Skip(keep);
        if (words.Count == 0) return [.. head, .. rest];

        var trimmed = query!.Trim();
        var matches = rest.Where(o => Matches(words, text(o), detail?.Invoke(o)))
            .OrderBy(o => text(o).StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        return [.. head, .. matches];
    }

    private static bool Matches(IReadOnlyList<string> words, string? text, string? detail)
    {
        var haystack = string.IsNullOrEmpty(detail) ? text ?? "" : $"{text} {detail}";
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}
