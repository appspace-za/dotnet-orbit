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
