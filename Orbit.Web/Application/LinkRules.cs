namespace Orbit.Application;

/// <summary>One run of a description or comment: plain text, or a web address the pages show as a link (spec §6.2).</summary>
public sealed record TextSegment(string Text, bool IsLink);

/// <summary>
/// The web addresses in a description or comment (spec §6.2), so the pages can show them as links that open in a new tab.
/// Only <c>http://</c> and <c>https://</c> addresses count - not <c>www.</c> on its own, nor any other scheme. The text is never
/// changed, only cut into runs: joined back together they are exactly what was typed.
/// </summary>
public static class LinkRules
{
    private static readonly string[] Schemes = ["https://", "http://"];

    /// <summary>Left off the end of an address: a sentence's punctuation more often than the address's own.</summary>
    private const string TrailingPunctuation = ".,;:!?'*";

    /// <summary>The text as plain runs and links, in order; nothing for null or empty text.</summary>
    public static IReadOnlyList<TextSegment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var segments = new List<TextSegment>();
        var plainFrom = 0;
        var i = 0;
        while (i < text.Length)
        {
            var end = LinkEnd(text, i);
            if (end < 0)
            {
                i++;
                continue;
            }
            if (i > plainFrom) segments.Add(new TextSegment(text[plainFrom..i], false));
            segments.Add(new TextSegment(text[i..end], true));
            i = plainFrom = end;
        }
        if (plainFrom < text.Length) segments.Add(new TextSegment(text[plainFrom..], false));
        return segments;
    }

    /// <summary>Where the address starting at <paramref name="start"/> ends, or -1 when none does.</summary>
    private static int LinkEnd(string text, int start)
    {
        if (start > 0 && char.IsLetterOrDigit(text[start - 1])) return -1;
        if (!Schemes.Any(s => string.Compare(text, start, s, 0, s.Length, StringComparison.OrdinalIgnoreCase) == 0)) return -1;
        var end = start;
        while (end < text.Length && !EndsAddress(text[end])) end++;
        end = TrimEnd(text, start, end);
        return Uri.TryCreate(text[start..end], UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
                ? end
                : -1;
    }

    /// <summary>Whitespace, and the characters that wrap an address in text and markup: <c>&lt;https://x.org&gt;</c>, <c>"https://x.org"</c>.</summary>
    private static bool EndsAddress(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '"';

    /// <summary>
    /// Leaves off trailing punctuation and any closing bracket the address didn't open, so <c>(see https://x.org/a).</c> links
    /// <c>https://x.org/a</c> while <c>https://en.wikipedia.org/wiki/Foo_(bar)</c> keeps its own.
    /// </summary>
    private static int TrimEnd(string text, int start, int end)
    {
        while (end > start && (TrailingPunctuation.Contains(text[end - 1]) || IsUnmatchedCloser(text, start, end - 1)))
            end--;
        return end;
    }

    private static bool IsUnmatchedCloser(string text, int start, int at)
    {
        var close = text[at];
        var open = close switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
        if (open == '\0') return false;
        int opens = 0, closes = 0;
        for (var i = start; i <= at; i++)
        {
            if (text[i] == open) opens++;
            else if (text[i] == close) closes++;
        }
        return closes > opens;
    }
}
