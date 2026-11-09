using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// One task as the tree view sees it (spec §6.15, decision 145): its place in the tree, whether the page's filter matched it
/// (an unmatched one is an ancestor shown for context) and the fields today's list order sorts by.
/// </summary>
public sealed record TaskTreeNode(Guid Id, Guid? ParentId, bool Matched, bool IsClosed, DateOnly? DueDate, TaskPriority Priority, DateTime CreatedAt)
{
    public static TaskTreeNode Of(TaskItem task, bool matched) =>
        new(task.Id, task.ParentTaskId, matched, task.Status.IsClosed(), task.DueDate, task.Priority, task.CreatedAt);
}

/// <summary>
/// A row of the tree, in display order. <see cref="IsContext"/>: an ancestor shown greyed because a task under it matched.
/// <see cref="HasChildren"/>: rows sit under it, so it gets a collapse toggle.
/// </summary>
public sealed record TaskTreeRow(Guid Id, Guid? ParentId, int Depth, bool IsContext, bool HasChildren);

/// <summary>One page of the tree: its rows, which page it is of how many, and how many rows matched the filter on every page.</summary>
public sealed record TaskTreeCut(IReadOnlyList<TaskTreeRow> Rows, int Page, int PageCount, int MatchCount);

/// <summary>
/// The tree view on the Tasks list and the project page (spec §6.15, decision 145): each subtask directly under its parent, the
/// filter's matches under their chain of ancestors (unmatched ones as context rows), branches in today's list order by their first
/// match, pages of whole branches, and which rows a collapsed parent hides. Pure, so it is unit tested (TREE-001 to TREE-006).
/// </summary>
public static class TaskTreeRules
{
    /// <summary>Rows on a Tasks list page, counting context rows and every row of a branch whether expanded or not.</summary>
    public const int PageRows = 50;
    /// <summary>The indent stops growing at this many levels, so a deep tree still fits a narrow screen.</summary>
    public const int MaxIndent = 6;
    /// <summary>How deep the walks go: a guard against a loop in the data, which the services never allow.</summary>
    public const int MaxDepth = 64;

    /// <summary>The tasks matching a filter plus their ancestors among <paramref name="all"/>, each once; the ancestors that don't match are context.</summary>
    public static IReadOnlyList<TaskTreeNode> NodesFor(IReadOnlyCollection<TaskItem> all, Func<TaskItem, bool> matches)
    {
        var byId = new Dictionary<Guid, TaskItem>();
        foreach (var t in all) byId.TryAdd(t.Id, t);
        var matched = byId.Values.Where(matches).ToList();
        var nodes = new Dictionary<Guid, TaskTreeNode>();
        foreach (var t in matched) nodes[t.Id] = TaskTreeNode.Of(t, true);
        foreach (var t in matched)
        {
            var parentId = t.ParentTaskId;
            for (var steps = 0; steps < MaxDepth && parentId is Guid id && !nodes.ContainsKey(id) && byId.TryGetValue(id, out var parent); steps++)
            {
                nodes[id] = TaskTreeNode.Of(parent, false);
                parentId = parent.ParentTaskId;
            }
        }
        return nodes.Values.ToList();
    }

    /// <summary>
    /// Every node as a row, in display order. A subtree sits where its earliest matching row would sit in today's list order (open
    /// first, then due date with none last, highest priority, newest), at the top level and among siblings alike.
    /// </summary>
    public static IReadOnlyList<TaskTreeRow> Build(IReadOnlyCollection<TaskTreeNode> nodes)
    {
        var byId = new Dictionary<Guid, TaskTreeNode>();
        foreach (var n in nodes) byId.TryAdd(n.Id, n);
        bool HasParent(TaskTreeNode n) => n.ParentId is Guid p && p != n.Id && byId.ContainsKey(p);
        var children = byId.Values.Where(HasParent).ToLookup(n => n.ParentId!.Value);

        // The earliest matching row in each subtree: each match is carried up its ancestors while it is earlier than what they have.
        var first = new Dictionary<Guid, TaskTreeNode>();
        foreach (var match in byId.Values.Where(n => n.Matched))
        {
            for (Guid? at = match.Id; at is Guid id && byId.TryGetValue(id, out var node); at = node.ParentId)
            {
                if (first.TryGetValue(id, out var have) && Compare(have, match) <= 0) break;
                first[id] = match;
            }
        }
        List<TaskTreeNode> Sorted(IEnumerable<TaskTreeNode> list) => list.OrderBy(n => n, Comparer<TaskTreeNode>.Create((x, y) =>
        {
            var fx = first.GetValueOrDefault(x.Id);
            var fy = first.GetValueOrDefault(y.Id);
            if ((fx is null) != (fy is null)) return fx is null ? 1 : -1;
            var c = fx is null ? 0 : Compare(fx, fy!);
            return c != 0 ? c : Compare(x, y);
        })).ToList();

        var walked = new List<(TaskTreeNode Node, int Depth)>();
        var visited = new HashSet<Guid>();
        void Visit(TaskTreeNode n, int depth)
        {
            if (!visited.Add(n.Id)) return;
            walked.Add((n, depth));
            if (depth >= MaxDepth) return;
            foreach (var c in Sorted(children[n.Id])) Visit(c, depth + 1);
        }
        foreach (var root in Sorted(byId.Values.Where(n => !HasParent(n)))) Visit(root, 0);
        // A loop, or a chain cut off at MaxDepth, leaves nodes unvisited: they start again at the top, so none is lost.
        while (visited.Count < byId.Count) Visit(Sorted(byId.Values.Where(n => !visited.Contains(n.Id)))[0], 0);

        return walked.Select((w, i) => new TaskTreeRow(w.Node.Id, w.Node.ParentId, w.Depth, !w.Node.Matched,
            i + 1 < walked.Count && walked[i + 1].Depth > w.Depth)).ToList();
    }

    /// <summary>
    /// Page <paramref name="page"/> of the rows: whole branches, taken in order while the page stays within <paramref name="pageRows"/>;
    /// a branch alone over the limit has a page of its own. A page past the end is the last page; no rows is one empty page.
    /// </summary>
    public static TaskTreeCut Cut(IReadOnlyList<TaskTreeRow> rows, int page, int pageRows = PageRows)
    {
        pageRows = Math.Max(1, pageRows);
        var branches = new List<List<TaskTreeRow>>();
        foreach (var row in rows)
        {
            if (row.Depth == 0 || branches.Count == 0) branches.Add([]);
            branches[^1].Add(row);
        }
        var pages = new List<List<TaskTreeRow>>();
        var current = new List<TaskTreeRow>();
        foreach (var branch in branches)
        {
            if (current.Count > 0 && current.Count + branch.Count > pageRows)
            {
                pages.Add(current);
                current = [];
            }
            current.AddRange(branch);
        }
        if (current.Count > 0 || pages.Count == 0) pages.Add(current);
        var at = Math.Clamp(page, 1, pages.Count);
        return new TaskTreeCut(pages[at - 1], at, pages.Count, rows.Count(r => !r.IsContext));
    }

    /// <summary>The rows a collapsed row hides: every following row deeper than it, until one at its depth or above.</summary>
    public static IReadOnlySet<Guid> Hidden(IReadOnlyList<(Guid Id, int Depth)> rows, IReadOnlySet<Guid> collapsed)
    {
        var hidden = new HashSet<Guid>();
        int? under = null;
        foreach (var (id, depth) in rows)
        {
            if (under is int top && depth > top)
            {
                hidden.Add(id);
                continue;
            }
            under = collapsed.Contains(id) ? depth : null;
        }
        return hidden;
    }

    /// <summary>The indent for a row at <paramref name="depth"/>, capped at <see cref="MaxIndent"/>.</summary>
    public static int Indent(int depth) => Math.Min(depth, MaxIndent);

    /// <summary>Today's list order: open before closed, then due date (none last), highest priority, newest, then id for a stable result.</summary>
    private static int Compare(TaskTreeNode x, TaskTreeNode y)
    {
        var c = x.IsClosed.CompareTo(y.IsClosed);
        if (c == 0) c = (x.DueDate is null).CompareTo(y.DueDate is null);
        if (c == 0) c = Nullable.Compare(x.DueDate, y.DueDate);
        if (c == 0) c = y.Priority.CompareTo(x.Priority);
        if (c == 0) c = y.CreatedAt.CompareTo(x.CreatedAt);
        return c != 0 ? c : x.Id.CompareTo(y.Id);
    }
}
