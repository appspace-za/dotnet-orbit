using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Tasks;

/// <summary>
/// The tree view on the Tasks list and the project page (spec §6.15, decision 145): subtasks under their parent, context rows
/// for unmatched ancestors, the order by first match, pages of whole branches and collapsing.
/// </summary>
public class TaskTreeRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Guid G() => Guid.NewGuid();

    private static DateOnly D(int day) => new(2026, 11, day);

    private static TaskTreeNode N(Guid id, Guid? parent = null, bool matched = true, DateOnly? due = null,
        TaskPriority priority = TaskPriority.Medium, bool closed = false, int ageDays = 0) =>
        new(id, parent, matched, closed, due, priority, T0.AddDays(-ageDays));

    private static TaskItem Item(TaskItem? parent = null) => new() { ParentTaskId = parent?.Id, Assignments = Assigned.To() };

    private static IEnumerable<TaskTreeRow> Branch(int size)
    {
        var root = G();
        yield return new(root, null, 0, false, size > 1);
        for (var i = 1; i < size; i++) yield return new(G(), root, 1, false, false);
    }

    /// <summary>TREE-001: a subtask sits directly under its parent, one depth step per level; the indent stops at six.</summary>
    [Fact]
    public void A_subtask_sits_directly_under_its_parent()
    {
        Guid a = G(), b = G(), c = G(), x = G();
        var rows = TaskTreeRules.Build([N(c, b, due: D(5)), N(x, due: D(3)), N(a, due: D(20)), N(b, a, due: D(10))]);
        Assert.Equal([x, a, b, c], rows.Select(r => r.Id));
        Assert.Equal([0, 0, 1, 2], rows.Select(r => r.Depth));
        Assert.True(rows[1].HasChildren);
        Assert.False(rows[3].HasChildren);
        Assert.Equal(6, TaskTreeRules.Indent(9));
        Assert.Equal(2, TaskTreeRules.Indent(2));
    }

    /// <summary>TREE-002: the parent is due last but its subtask first, so the branch goes ahead of a task due in between.</summary>
    [Fact]
    public void A_branch_sits_where_its_first_match_would()
    {
        Guid parent = G(), sub = G(), middle = G(), closedEarly = G(), noDue = G();
        var rows = TaskTreeRules.Build([N(parent, due: D(30)), N(sub, parent, due: D(2)), N(middle, due: D(10)),
            N(closedEarly, due: D(1), closed: true), N(noDue)]);
        Assert.Equal([parent, sub, middle, noDue, closedEarly], rows.Select(r => r.Id));
    }

    /// <summary>TREE-002: siblings with the same due date go highest priority first, then newest first.</summary>
    [Fact]
    public void Siblings_follow_todays_order()
    {
        Guid top = G(), high = G(), lowNew = G(), lowOld = G();
        var rows = TaskTreeRules.Build([N(top, due: D(1)), N(lowOld, top, due: D(5), priority: TaskPriority.Low, ageDays: 3),
            N(high, top, due: D(5), priority: TaskPriority.High), N(lowNew, top, due: D(5), priority: TaskPriority.Low, ageDays: 1)]);
        Assert.Equal([top, high, lowNew, lowOld], rows.Select(r => r.Id));
    }

    /// <summary>TREE-003: ancestors that didn't match the filter are context rows.</summary>
    [Fact]
    public void Unmatched_ancestors_are_context_rows()
    {
        Guid top = G(), mid = G(), match = G();
        var rows = TaskTreeRules.Build([N(top, matched: false), N(mid, top, matched: false), N(match, mid)]);
        Assert.Equal([true, true, false], rows.Select(r => r.IsContext));
    }

    /// <summary>TREE-003: NodesFor adds a match's ancestors as context and leaves out what lies below or beside it.</summary>
    [Fact]
    public void NodesFor_adds_ancestors_and_leaves_out_unmatched_descendants()
    {
        var top = Item(); var mid = Item(top); var match = Item(mid); var below = Item(match); var other = Item();
        var nodes = TaskTreeRules.NodesFor([top, mid, match, below, other], t => t == match);
        Assert.Equal(new[] { top.Id, mid.Id, match.Id }.Order(), nodes.Select(n => n.Id).Order());
        Assert.Equal([match.Id], nodes.Where(n => n.Matched).Select(n => n.Id));
    }

    /// <summary>TREE-003: a matched parent whose subtasks were filtered out has nothing to collapse.</summary>
    [Fact]
    public void A_matched_parent_with_its_subtasks_filtered_out_has_no_children()
    {
        var parent = Item(); var child = Item(parent);
        var row = Assert.Single(TaskTreeRules.Build(TaskTreeRules.NodesFor([parent, child], t => t == parent)));
        Assert.False(row.HasChildren);
    }

    /// <summary>TREE-004: a page holds whole branches and never splits one.</summary>
    [Fact]
    public void A_page_never_splits_a_branch()
    {
        var rows = Branch(30).Concat(Branch(30)).Concat(Branch(10)).ToList();
        Assert.Equal(2, TaskTreeRules.Cut(rows, 1, 50).PageCount);
        Assert.Equal(30, TaskTreeRules.Cut(rows, 1, 50).Rows.Count);
        Assert.Equal(40, TaskTreeRules.Cut(rows, 2, 50).Rows.Count);
    }

    /// <summary>TREE-004: a branch larger than a page gets a page of its own.</summary>
    [Fact]
    public void An_oversized_branch_gets_a_page_of_its_own()
    {
        var rows = Branch(5).Concat(Branch(80)).Concat(Branch(5)).ToList();
        Assert.Equal([5, 80, 5], Enumerable.Range(1, 3).Select(p => TaskTreeRules.Cut(rows, p, 50).Rows.Count));
        Assert.Equal(3, TaskTreeRules.Cut(rows, 1, 50).PageCount);
    }

    /// <summary>TREE-004: a page number past the end shows the last page, and an empty list is one empty page.</summary>
    [Fact]
    public void A_page_past_the_end_shows_the_last_page()
    {
        var rows = Branch(30).Concat(Branch(30)).ToList();
        var cut = TaskTreeRules.Cut(rows, 99, 50);
        Assert.Equal(2, cut.Page);
        Assert.Equal(30, cut.Rows.Count);
        var empty = TaskTreeRules.Cut([], 3, 50);
        Assert.Equal(1, empty.Page);
        Assert.Equal(1, empty.PageCount);
        Assert.Empty(empty.Rows);
    }

    /// <summary>TREE-005: the match count leaves out context rows.</summary>
    [Fact]
    public void The_match_count_leaves_out_context_rows()
    {
        Guid top = G(), a = G(), b = G();
        Assert.Equal(2, TaskTreeRules.Cut(TaskTreeRules.Build([N(top, matched: false), N(a, top), N(b, top)]), 1, 50).MatchCount);
    }

    /// <summary>TREE-005: a loop or a very deep chain doesn't hang or overflow, and lists every node once.</summary>
    [Fact]
    public void A_loop_or_a_deep_chain_lists_every_node_once()
    {
        Guid a = G(), b = G();
        var loop = TaskTreeRules.Build([N(a, b), N(b, a)]);
        Assert.Equal(2, loop.Count);
        Assert.Equal(2, loop.Select(r => r.Id).Distinct().Count());

        var chain = new List<TaskTreeNode>();
        Guid? parent = null;
        for (var i = 0; i < 100; i++)
        {
            var id = G();
            chain.Add(N(id, parent));
            parent = id;
        }
        var deep = TaskTreeRules.Build(chain);
        Assert.Equal(100, deep.Count);
        Assert.All(deep, r => Assert.InRange(r.Depth, 0, TaskTreeRules.MaxDepth));
    }

    /// <summary>TREE-006: collapsing hides every row below, at any depth, and nothing beside it.</summary>
    [Fact]
    public void Collapsing_hides_the_whole_branch_below()
    {
        Guid a = G(), b = G(), c = G(), d = G();
        (Guid Id, int Depth, bool IsContext)[] rows = [(a, 0, false), (b, 1, false), (c, 2, false), (d, 0, false)];
        Assert.Equal(new[] { b, c }.Order(), TaskTreeRules.Hidden(rows, new HashSet<Guid> { a }).Order());
        Assert.Equal([c], TaskTreeRules.Hidden(rows, new HashSet<Guid> { b }));
        Assert.Empty(TaskTreeRules.Hidden(rows, new HashSet<Guid> { d }));
    }

    /// <summary>
    /// TREE-006: a context row is there only because something under it matched the filter, so a collapse remembered from an
    /// unfiltered visit doesn't hide those matches; a matched parent under it still collapses as usual.
    /// </summary>
    [Fact]
    public void A_context_row_never_hides_the_matches_under_it()
    {
        Guid top = G(), match = G(), parent = G(), child = G();
        (Guid Id, int Depth, bool IsContext)[] rows = [(top, 0, true), (match, 1, false), (parent, 1, false), (child, 2, false)];
        Assert.Empty(TaskTreeRules.Hidden(rows, new HashSet<Guid> { top }));
        Assert.Equal([child], TaskTreeRules.Hidden(rows, new HashSet<Guid> { top, parent }));
    }
}
