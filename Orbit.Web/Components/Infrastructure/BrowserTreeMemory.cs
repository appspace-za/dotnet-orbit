using Microsoft.JSInterop;

namespace Orbit.Components.Infrastructure;

/// <summary>
/// Remembers which parents are collapsed in the task tree (spec §6.2, §6.15): one set of task ids in the browser's localStorage,
/// through <c>orbit.tree</c> (orbit-interop.js), shared by the Tasks list and every project page and kept across sessions. A page
/// reads it as it loads, before its rows are drawn, so collapsed rows never flash open. Unlike <see cref="BrowserFilterMemory"/> it
/// has no owner: an id shows nothing without the right to see its task, and every parent starts expanded, so someone else
/// signing in on the same browser only finds a few groups folded.
/// </summary>
public sealed class BrowserTreeMemory(IJSRuntime js)
{
    /// <summary>The collapsed task ids; empty when nothing is stored or the storage can't be read.</summary>
    public async Task<HashSet<Guid>> CollapsedAsync()
    {
        var ids = await js.InvokeAsync<string[]?>("orbit.tree.collapsed");
        var result = new HashSet<Guid>();
        foreach (var text in ids ?? [])
            if (Guid.TryParse(text, out var id)) result.Add(id);
        return result;
    }

    /// <summary>Collapses (or expands) these parents; the most recent 2000 collapsed are kept.</summary>
    public async Task SetAsync(IEnumerable<Guid> ids, bool collapsed) =>
        await js.InvokeVoidAsync("orbit.tree.set", ids.Select(id => id.ToString()).ToArray(), collapsed);
}
