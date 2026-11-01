using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using Orbit.Helpers;

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
    /// is replaced by the remembered filter). <paramref name="carriedKeys"/> the address holds are kept on the way there
    /// (<see cref="RememberedAddress"/>).
    /// </summary>
    public async Task<string?> ApplyAsync(string page, Uri uri, IReadOnlyCollection<string> filterKeys, bool reset, bool oneOff = false,
        IReadOnlyCollection<string>? carriedKeys = null)
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
        return RememberedAddress(uri, remembered, filterKeys, carriedKeys ?? []);
    }

    /// <summary>
    /// Where a page whose address names none of its filters goes: to its <paramref name="remembered"/> filter, followed by the
    /// <paramref name="carriedKeys"/> the address holds (the Planning page's sprint, which a sprint's page links with but which isn't
    /// remembered, decision 141). Null to stay, when nothing of the remembered filter is one of the page's
    /// <paramref name="filterKeys"/>. Only those keys are taken from it: one remembered before it stopped being a filter (the Planning
    /// page's sprint, remembered before decision 141) would otherwise send the page to an address that names no filter either, and
    /// so round and round. The address gone to always names a filter, so it is shown and the move happens once.
    /// </summary>
    public static string? RememberedAddress(Uri uri, string? remembered, IEnumerable<string> filterKeys, IEnumerable<string> carriedKeys)
    {
        if (string.IsNullOrEmpty(remembered)) return null;
        var filters = new FilterQuery().Keep(new Uri(uri, "?" + remembered).AbsoluteUri, filterKeys).ToString();
        if (filters.Length == 0) return null;
        var carried = new FilterQuery().Keep(uri.AbsoluteUri, carriedKeys).ToString();
        return $"{uri.AbsolutePath}?{(carried.Length == 0 ? filters : $"{filters}&{carried}")}";
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
