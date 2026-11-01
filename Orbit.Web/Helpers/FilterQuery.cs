using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

namespace Orbit.Helpers;

/// <summary>
/// A list page's filters as a query string, in the keys the page reads (and the Razor page read, so old links still work): empty
/// values are left out. <see cref="ToString"/> gives "a=1&amp;b=2", without the "?". In Helpers rather than with the list pages
/// so the links other code builds to a list (<see cref="Orbit.Application.TaskListKeys"/>) are made the same way.
/// </summary>
public sealed class FilterQuery
{
    /// <summary>A ticked filter's value in the address.</summary>
    public const string Yes = "true";

    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public FilterQuery Add(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _pairs.Add(new(key, value.Trim()));
        return this;
    }

    public FilterQuery Add(string key, Guid? value) => Add(key, value?.ToString());
    public FilterQuery Add(string key, bool value) => value ? Add(key, Yes) : this;
    public FilterQuery Add(string key, DateOnly? value) => Add(key, value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    public FilterQuery Add(string key, int? value) => Add(key, value?.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The filters an address holds, for these keys and in their order, with one of them changed: what a list page goes to when
    /// a filter is changed (<c>OrbitListPage</c>). Any other key (the page number, say) is left out.
    /// </summary>
    public static FilterQuery FromAddress(string uri, IEnumerable<string> keys, string? changedKey = null, string? changedValue = null) =>
        new FilterQuery().Keep(uri, keys, changedKey, changedValue);

    /// <summary>Adds these keys with the values the address holds: a drill-down a page carries along without remembering it.</summary>
    public FilterQuery Keep(string uri, IEnumerable<string> keys) => Keep(uri, keys, null, null);

    private FilterQuery Keep(string uri, IEnumerable<string> keys, string? changedKey, string? changedValue)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri, UriKind.Absolute).Query);
        foreach (var key in keys)
            Add(key, key == changedKey ? changedValue : query.TryGetValue(key, out var values) ? values.ToString() : null);
        return this;
    }

    /// <summary>The address of <paramref name="path"/> with these filters: "/Tasks?Status=Todo", or just "/Tasks" with none.</summary>
    public string At(string path)
    {
        var query = ToString();
        return query.Length == 0 ? path : $"{path}?{query}";
    }

    public override string ToString() =>
        string.Join("&", _pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>A date in a filter's address ("2026-10-28"), or null when there is none or it isn't one.</summary>
    public static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>
    /// A period in the address (My Time, the reports): To defaults to today and From to <paramref name="daysBefore"/> days before
    /// To, and a From after To is swapped with it. A page reads it before it loads, so its date pickers never show an older
    /// period while the new one loads (§13 item 143).
    /// </summary>
    public static (DateOnly From, DateOnly To) Period(DateOnly? from, DateOnly? to, DateOnly today, int daysBefore)
    {
        var end = to ?? today;
        var start = from ?? end.AddDays(-daysBefore);
        return start <= end ? (start, end) : (end, start);
    }

    /// <summary>A tick in a filter's address ("true"): anything else, a bad value included, is off.</summary>
    public static bool Flag(string? value) => bool.TryParse(value?.Trim(), out var b) && b;

    /// <summary>An enum in a filter's address, by name, or null.</summary>
    public static T? Enum<T>(string? value) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(value, ignoreCase: true, out var v) && System.Enum.IsDefined(v) ? v : null;
}
