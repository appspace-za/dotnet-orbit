using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>One line of a task tree: <see cref="IsContext"/> when shown only above a match, <see cref="HasShownChildren"/> when something is listed under it.</summary>
public sealed record TreeRow(Guid Id, Guid? ParentId, int Depth, bool IsContext, bool HasShownChildren);

/// <summary>
/// Orders task rows as a tree (a parent before its subtasks), pages them by whole top-level groups and hides the rows
/// under collapsed parents (design spec 2026-11-02 §6; orbit-spec §6.15). Pure.
/// </summary>
public static class TaskTreeRules
{
    public const int MaxIndent = 4, MaxDepth = 64;

    /// <summary>The Tasks list's SQL order (TaskService.ListAsync): open first, due date ascending with none last, priority descending, newest first.</summary>
    public static readonly IComparer<TaskTreeKey> ListOrder = Comparer<TaskTreeKey>.Create((a, b) =>
    {
        var c = a.Status.IsClosed().CompareTo(b.Status.IsClosed());
        if (c != 0) return c;
        c = b.DueDate.HasValue.CompareTo(a.DueDate.HasValue);
        if (c != 0) return c;
        if (a.DueDate.HasValue)
        {
            c = a.DueDate.Value.CompareTo(b.DueDate!.Value);
            if (c != 0) return c;
        }
        c = b.Priority.CompareTo(a.Priority);
        if (c != 0) return c;
        return b.CreatedAt.CompareTo(a.CreatedAt);
    });

    /// <summary>
    /// Depth-first rows. An item whose parent isn't among the items is a root; an item no root reaches (a parent loop)
    /// becomes a root itself, so every item is listed once.
    /// </summary>
    public static IReadOnlyList<TreeRow> Build<T>(IEnumerable<T> items, Func<T, Guid> id, Func<T, Guid?> parentId,
        Func<T, bool> isMatch, IComparer<T> siblingOrder)
    {
        var all = items.ToList();
        var ids = all.Select(id).ToHashSet();
        bool IsRoot(T t) => parentId(t) is not Guid p || !ids.Contains(p) || p == id(t);
        var children = all.Where(t => !IsRoot(t)).ToLookup(t => parentId(t)!.Value);
        var rows = new List<TreeRow>();
        var visited = new HashSet<Guid>();

        void Visit(T t, int depth)
        {
            if (depth > MaxDepth || !visited.Add(id(t))) return;
            // The row goes in first so a parent precedes its subtasks; its flag is set once the children are known.
            var index = rows.Count;
            rows.Add(new TreeRow(id(t), parentId(t), depth, !isMatch(t), false));
            foreach (var c in children[id(t)].OrderBy(x => x, siblingOrder)) Visit(c, depth + 1);
            if (rows.Count > index + 1) rows[index] = rows[index] with { HasShownChildren = true };
        }

        foreach (var root in all.Where(IsRoot).OrderBy(x => x, siblingOrder)) Visit(root, 0);
        foreach (var loose in all.Where(t => !visited.Contains(id(t))).OrderBy(x => x, siblingOrder)) Visit(loose, 0);
        return rows;
    }

    /// <summary>A page of whole top-level groups (each starts at a depth 0 row); a group larger than the page takes one alone. Page numbers clamp to 1..TotalPages.</summary>
    public static (IReadOnlyList<TreeRow> Rows, int TotalPages) Page(IReadOnlyList<TreeRow> rows, int page, int pageSize)
    {
        pageSize = Math.Max(1, pageSize);
        var groups = new List<List<TreeRow>>();
        List<TreeRow>? group = null;
        foreach (var r in rows)
        {
            if (group is null || r.Depth == 0) groups.Add(group = []);
            group.Add(r);
        }
        var pages = new List<List<TreeRow>>();
        foreach (var g in groups)
        {
            if (pages.Count == 0 || pages[^1].Count + g.Count > pageSize) pages.Add([]);
            pages[^1].AddRange(g);
        }
        if (pages.Count == 0) return ([], 1);
        return (pages[Math.Clamp(page, 1, pages.Count) - 1], pages.Count);
    }

    /// <summary>Matches plus their ancestors found in <paramref name="all"/>, as tree rows in <see cref="ListOrder"/>.</summary>
    public static IReadOnlyList<TaskTreeRow> ForTasks(IReadOnlyList<TaskItem> all, Func<TaskItem, bool> isMatch)
    {
        var byId = all.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var matches = all.Where(isMatch).Select(t => t.Id).ToHashSet();
        var included = new HashSet<Guid>(matches);
        foreach (var id in matches)
        {
            var cursor = byId[id].ParentTaskId;
            for (var guard = 0; cursor is Guid p && byId.ContainsKey(p) && guard < MaxDepth; guard++)
            {
                included.Add(p);
                cursor = byId[p].ParentTaskId;
            }
        }
        var keys = byId.Values.Where(t => included.Contains(t.Id))
            .Select(t => new TaskTreeKey(t.Id, t.ParentTaskId, matches.Contains(t.Id), t.HasSubtasks, t.Status, t.DueDate, t.Priority, t.CreatedAt));
        return Build(keys, k => k.Id, k => k.ParentTaskId, k => k.IsMatch, ListOrder)
            .Select(row => new TaskTreeRow(byId[row.Id], row.Depth, row.IsContext, row.HasShownChildren)).ToList();
    }

    /// <summary>Drops every row below a collapsed row.</summary>
    public static IReadOnlyList<TreeRow> Visible(IReadOnlyList<TreeRow> rows, IReadOnlySet<Guid> collapsed)
    {
        var result = new List<TreeRow>();
        int? hideBelow = null;
        foreach (var r in rows)
        {
            if (hideBelow is int d)
            {
                if (r.Depth > d) continue;
                hideBelow = null;
            }
            result.Add(r);
            if (collapsed.Contains(r.Id)) hideBelow = r.Depth;
        }
        return result;
    }

    /// <summary>The task rows left showing under <paramref name="collapsed"/> parents, as <see cref="Visible(IReadOnlyList{TreeRow}, IReadOnlySet{Guid})"/> drops them.</summary>
    public static IReadOnlyList<TaskTreeRow> Visible(IReadOnlyList<TaskTreeRow> rows, IReadOnlySet<Guid> collapsed)
    {
        var shown = Visible(rows.Select(r => new TreeRow(r.Task.Id, r.Task.ParentTaskId, r.Depth, r.IsContext, r.HasShownChildren)).ToList(), collapsed);
        var kept = shown.Select(r => r.Id).ToHashSet();
        return rows.Where(r => kept.Contains(r.Task.Id)).ToList();
    }

    /// <summary>
    /// A context row the viewer may not view: an ancestor loaded only to place a match under it, outside the viewer's scope (§6.5).
    /// It shows its number and title and nothing else, as the old "in: Parent" line did (controller ruling R13). A row that
    /// matched the page's own filter keeps what the page always showed of it.
    /// </summary>
    public static bool TitleOnly(Actor actor, TaskTreeRow row) => row.IsContext && !AccessPolicy.CanViewTask(actor, row.Task);

    public static int Indent(int depth) => Math.Min(depth, MaxIndent);
}
