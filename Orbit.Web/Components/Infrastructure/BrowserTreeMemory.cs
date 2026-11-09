using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Orbit.Components.Infrastructure;

/// <summary>
/// The parents collapsed in the tree view on the Tasks list and the project page (spec §6.15, decision 145), kept per user in the
/// browser's local storage through <c>orbit.tree</c> (orbit-interop.js), so they stay collapsed across navigation and a browser
/// restart. One list serves both pages. Every parent starts expanded; the list holds at most <see cref="Limit"/> ids, the oldest
/// dropped first. Nothing is stored server-side.
/// </summary>
public sealed class BrowserTreeMemory(IJSRuntime js, AuthenticationStateProvider authentication)
{
    public const int Limit = 1000;

    /// <summary>The collapsed task ids; ids the browser holds that aren't ids are skipped.</summary>
    public async Task<HashSet<Guid>> CollapsedAsync() => [.. await ReadAsync()];

    /// <summary>Collapses or expands <paramref name="taskIds"/> in the stored list.</summary>
    public async Task SetAsync(IEnumerable<Guid> taskIds, bool collapsed)
    {
        var owner = await BrowserOwner.IdAsync(authentication);
        var next = Next(await ReadAsync(owner), taskIds, collapsed);
        await js.InvokeVoidAsync("orbit.tree.write", owner, next.Select(id => id.ToString()).ToArray());
    }

    /// <summary>
    /// TREE-020: the stored list after a change. Collapsing moves the ids to the end, each once, and keeps the newest
    /// <paramref name="limit"/>; expanding removes only the ids given.
    /// </summary>
    public static IReadOnlyList<Guid> Next(IReadOnlyList<Guid> current, IEnumerable<Guid> taskIds, bool collapsed, int limit = Limit)
    {
        var changed = taskIds.Distinct().ToList();
        var kept = current.Except(changed).ToList();
        if (!collapsed) return kept;
        kept.AddRange(changed);
        return kept.Count > limit ? kept[^limit..] : kept;
    }

    private async Task<IReadOnlyList<Guid>> ReadAsync(string? owner = null)
    {
        var raw = await js.InvokeAsync<string[]?>("orbit.tree.read", owner ?? await BrowserOwner.IdAsync(authentication));
        return (raw ?? []).Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null).OfType<Guid>().Distinct().ToList();
    }
}
