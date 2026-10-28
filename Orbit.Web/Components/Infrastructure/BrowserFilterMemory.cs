using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace Orbit.Components.Infrastructure;

/// <summary>
/// Remembers a list page's filters for the browser session (spec §6.2): the last filter used is kept per page and per user, and
/// opening the page with no filter of its own goes to it. The address bar always shows the filters in force, so a filtered list
/// stays shareable and Back behaves. A Blazor page has no response to set a cookie on, so this keeps a session cookie of its own
/// through <c>orbit.filters</c> (orbit-interop.js). It replaced the Razor pages' HttpOnly <c>FilterMemory</c> (decision 102).
/// </summary>
public sealed class BrowserFilterMemory(IJSRuntime js, AuthenticationStateProvider authentication)
{
    /// <summary>
    /// Where a list page should go instead of showing <paramref name="uri"/>, or null to show it. <paramref name="reset"/> forgets
    /// the filter and goes to the plain page. A request naming any filter field, even empty, is remembered. One naming none goes to
    /// the remembered filter, unless <paramref name="oneOff"/> (a drill-down such as one task's subtasks, which neither replaces nor
    /// is replaced by the remembered filter).
    /// </summary>
    public async Task<string?> ApplyAsync(string page, Uri uri, IReadOnlyCollection<string> filterKeys, bool reset, bool oneOff = false)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (reset)
        {
            await ForgetAsync(page);
            return uri.AbsolutePath;
        }
        if (filterKeys.Any(query.ContainsKey))
        {
            await RememberAsync(page, Remembered(query, filterKeys));
            return null;
        }
        if (oneOff) return null;
        var remembered = await js.InvokeAsync<string?>("orbit.filters.recall", page, await OwnerAsync());
        return string.IsNullOrEmpty(remembered) || uri.Query == "?" + remembered ? null : $"{uri.AbsolutePath}?{remembered}";
    }

    /// <summary>Remembers a filter the user just chose on the page (a query string without "?"); an empty one forgets instead.</summary>
    public async Task RememberAsync(string page, string query) =>
        await js.InvokeVoidAsync("orbit.filters.remember", page, await OwnerAsync(), query);

    public async Task ForgetAsync(string page) => await js.InvokeVoidAsync("orbit.filters.forget", page);

    private static string Remembered(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, IEnumerable<string> keys) =>
        string.Join("&", keys.SelectMany(k => query.TryGetValue(k, out var values)
            ? values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => $"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v!)}")
            : []));

    private async Task<string> OwnerAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id.ToString("N") : "-";
    }
}

/// <summary>
/// A list page's filters as a query string, in the keys the page reads (and the Razor page read, so old links still work): empty
/// values are left out. <see cref="ToString"/> gives "a=1&amp;b=2", without the "?".
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
    public FilterQuery Add(string key, DateOnly? value) => Add(key, value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    public FilterQuery Add(string key, int? value) => Add(key, value?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The filters an address holds, for these keys and in their order, with one of them changed: what a list page goes to when
    /// a filter is changed (<see cref="OrbitListPage"/>). Any other key (the page number, say) is left out.
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
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>A tick in a filter's address ("true"): anything else, a bad value included, is off.</summary>
    public static bool Flag(string? value) => bool.TryParse(value?.Trim(), out var b) && b;

    /// <summary>An enum in a filter's address, by name, or null.</summary>
    public static T? Enum<T>(string? value) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(value, ignoreCase: true, out var v) && System.Enum.IsDefined(v) ? v : null;
}
