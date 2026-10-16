namespace Orbit.Helpers;

/// <summary>
/// A list filter's select that offers "None" beside its items (spec §6.2): the Project, Assignee and Requestee filters of the
/// Tasks list and the Backlog. The select posts <see cref="None"/> or an id under the filter's usual address key, and the page
/// maps the value onto the typed filter, so there is one way to say it.
/// </summary>
public static class FilterChoice
{
    public const string None = "none";

    public static bool IsNone(string? value) => string.Equals(value?.Trim(), None, StringComparison.OrdinalIgnoreCase);

    /// <summary>The chosen id, or null for "All", "None" or a value that isn't one.</summary>
    public static Guid? Id(string? value) => Guid.TryParse(value?.Trim(), out var id) ? id : null;
}
