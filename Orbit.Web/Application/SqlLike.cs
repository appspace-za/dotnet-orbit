namespace Orbit.Application;

/// <summary>
/// ILIKE patterns for the text someone typed into a search (spec §6.2, §13 item 122). The typed text is matched literally:
/// <c>%</c>, <c>_</c> and <c>\</c> are escaped with a backslash, so "100%" doesn't also find "1000 users" and "_" doesn't find
/// everything. Every <c>EF.Functions.ILike</c> given one of these patterns must pass <see cref="EscapeCharacter"/>: without
/// it Npgsql writes <c>ESCAPE ''</c>, which turns PostgreSQL's backslash escape off and makes <c>\%</c> match a backslash.
/// </summary>
public static class SqlLike
{
    /// <summary>The escape character the patterns use; the third argument of every <c>EF.Functions.ILike</c> given one.</summary>
    public const string EscapeCharacter = @"\";

    /// <summary>PCK-010: a pattern matching the text anywhere.</summary>
    public static string Contains(string text) => $"%{Escape(text)}%";

    /// <summary>PCK-010: a pattern matching the text at the start.</summary>
    public static string StartsWith(string text) => $"{Escape(text)}%";

    /// <summary>PCK-010: the text with the LIKE wildcards and the escape character escaped.</summary>
    public static string Escape(string text) =>
        text.Replace(EscapeCharacter, EscapeCharacter + EscapeCharacter)
            .Replace("%", EscapeCharacter + "%")
            .Replace("_", EscapeCharacter + "_");
}
