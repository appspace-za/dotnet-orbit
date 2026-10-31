using Microsoft.AspNetCore.Components;
using Orbit.Helpers;

namespace Orbit.Components.Infrastructure;

/// <summary>
/// A list page whose filters live in its address and are remembered for the browser session (spec §6.2): Tasks, Backlog, Assets
/// and Projects. A filter changed on the page is remembered and the page moves to its new address; opening the page with no filter
/// of its own goes to the remembered one; Reset forgets it (<see cref="BrowserFilterMemory"/>). One copy of what the pages used to
/// copy from each other (decision 112). The current filters are read from the address, which is where the page's own
/// <c>[SupplyParameterFromQuery]</c> properties come from.
/// </summary>
public abstract class OrbitListPage : OrbitComponentBase
{
    /// <summary>The page number's key in the address; a change of filter goes back to the first page.</summary>
    protected const string PageKey = "PageNumber";

    [Inject] protected BrowserFilterMemory Memory { get; set; } = null!;

    /// <summary>The page's name in the filter memory ("tasks").</summary>
    protected abstract string MemoryKey { get; }

    /// <summary>The address keys of the page's filters, in address order. Only these are remembered.</summary>
    protected abstract IReadOnlyList<string> FilterKeys { get; }

    /// <summary>Keys carried along in the address but never remembered, such as the Tasks list's one-task drill-down.</summary>
    protected virtual IReadOnlyList<string> CarriedKeys => [];

    /// <summary>
    /// Goes to the remembered filter when the address names none, or to the plain page on <paramref name="reset"/>. True when it
    /// moved, so the caller stops: the page loads again at its new address.
    /// </summary>
    protected async Task<bool> GoToRememberedAsync(bool reset, bool oneOff = false)
    {
        if (await Memory.ApplyAsync(MemoryKey, new Uri(Nav.Uri), FilterKeys, reset, oneOff) is not { } go) return false;
        Nav.NavigateTo(go, replace: true);
        return true;
    }

    /// <summary>Changes one filter: remembers the page's filters, then moves to their address, back on the first page.</summary>
    protected Task GoAsync(string key, string? value) => GoToAsync(FilterQuery.FromAddress(Nav.Uri, FilterKeys, key, value));

    /// <summary>Clears several filters at once, such as a quick filter turned off, keeping the rest: remembered and moved to as by <see cref="GoAsync"/>.</summary>
    protected Task ClearAsync(params string[] keys) => GoToAsync(FilterQuery.FromAddress(Nav.Uri, FilterKeys.Except(keys)));

    private async Task GoToAsync(FilterQuery filters)
    {
        await Memory.RememberAsync(MemoryKey, filters.ToString());
        Nav.NavigateTo(filters.Keep(Nav.Uri, CarriedKeys).At(Path));
    }

    /// <summary>The address of another page of the list, with the same filters.</summary>
    protected string PageUrl(int page) =>
        FilterQuery.FromAddress(Nav.Uri, FilterKeys).Keep(Nav.Uri, CarriedKeys).Add(PageKey, page).At(Path);

    /// <summary>Forgets the remembered filter and shows the plain list.</summary>
    protected async Task ResetAsync()
    {
        await Memory.ForgetAsync(MemoryKey);
        Nav.NavigateTo(Path);
    }

    private string Path => new Uri(Nav.Uri).AbsolutePath;
}
