using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Tasks;

/// <summary>The task tree: ordering, paging by whole groups, collapsing (design spec 2026-11-02 §6; orbit-spec §6.15).</summary>
public class TaskTreeRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TaskTreeKey K(Guid id, Guid? parent = null, bool match = true, bool hasSubtasks = false,
        TaskItemStatus status = TaskItemStatus.Todo, DateOnly? due = null, TaskPriority priority = TaskPriority.Medium, DateTime? created = null) =>
        new(id, parent, match, hasSubtasks, status, due, priority, created ?? Now);

    private static IReadOnlyList<TreeRow> Build(params TaskTreeKey[] keys) =>
        TaskTreeRules.Build(keys, k => k.Id, k => k.ParentTaskId, k => k.IsMatch, TaskTreeRules.ListOrder);

    /// <summary>SUB-016: a match sits under its parent chain, shown as context; groups follow the list order.</summary>
    [Fact]
    public void Matches_sit_under_their_parent_chain()
    {
        Guid r = Guid.NewGuid(), p = Guid.NewGuid(), c = Guid.NewGuid(), t = Guid.NewGuid();
        var rows = Build(
            K(r, match: false, hasSubtasks: true, due: new DateOnly(2026, 12, 1)),
            K(p, r, match: false, hasSubtasks: true, due: new DateOnly(2026, 12, 1)),
            K(c, p, due: new DateOnly(2026, 12, 1)),
            K(t, due: new DateOnly(2026, 11, 5)));
        Assert.Equal([t, r, p, c], rows.Select(x => x.Id));
        Assert.Equal([0, 0, 1, 2], rows.Select(x => x.Depth));
        Assert.Equal([false, true, true, false], rows.Select(x => x.IsContext));
        Assert.Equal([false, true, true, false], rows.Select(x => x.HasShownChildren));
    }

    /// <summary>Review Focus 2: a parent that matches is not context, and has no shown children when none match.</summary>
    [Fact]
    public void A_parent_that_matches_is_not_context()
    {
        var p = Guid.NewGuid();
        var rows = Build(K(p, hasSubtasks: true));
        var row = Assert.Single(rows);
        Assert.False(row.IsContext);
        Assert.False(row.HasShownChildren);
    }

    private static List<TreeRow> Group(int size) =>
        Enumerable.Range(0, size).Select(i => new TreeRow(Guid.NewGuid(), null, i == 0 ? 0 : 1, false, false)).ToList();

    /// <summary>SUB-017: a page holds whole top-level groups; one bigger than the page takes a page alone.</summary>
    [Fact]
    public void Pages_never_split_a_group()
    {
        List<TreeRow> a = Group(3), b = Group(3), c = Group(60);
        var rows = a.Concat(b).Concat(c).ToList();
        var (p1, total) = TaskTreeRules.Page(rows, 1, 5);
        Assert.Equal(3, total);
        Assert.Equal(a, p1);
        Assert.Equal(b, TaskTreeRules.Page(rows, 2, 5).Rows);
        Assert.Equal(c, TaskTreeRules.Page(rows, 3, 5).Rows);
        Assert.Equal(c, TaskTreeRules.Page(rows, 9, 5).Rows);
        Assert.Equal(a, TaskTreeRules.Page(rows, 0, 5).Rows);
    }

    /// <summary>SUB-018: indentation stops at four levels.</summary>
    [Fact]
    public void Indent_is_capped()
    {
        Assert.Equal(4, TaskTreeRules.Indent(7));
        Assert.Equal(2, TaskTreeRules.Indent(2));
    }

    /// <summary>Review Focus 3: collapsing a parent hides everything under it, nothing else.</summary>
    [Fact]
    public void Visible_hides_everything_under_a_collapsed_parent()
    {
        Guid r = Guid.NewGuid(), p = Guid.NewGuid(), c = Guid.NewGuid(), s = Guid.NewGuid();
        var rows = new List<TreeRow>
        {
            new(r, null, 0, false, true), new(p, r, 1, false, true), new(c, p, 2, false, false), new(s, null, 0, false, false)
        };
        Assert.Equal([r, p, s], TaskTreeRules.Visible(rows, new HashSet<Guid> { p }).Select(x => x.Id));
        Assert.Equal([r, s], TaskTreeRules.Visible(rows, new HashSet<Guid> { r }).Select(x => x.Id));
        Assert.Equal([r, p, c, s], TaskTreeRules.Visible(rows, new HashSet<Guid>()).Select(x => x.Id));
    }

    /// <summary>Review Focus 4: a parent loop still lists every task once.</summary>
    [Fact]
    public void A_parent_loop_still_lists_every_task_once()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var rows = Build(K(a, b), K(b, a));
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(x => x.Id).Distinct().Count());
    }

    /// <summary>The order matches TaskService.ListAsync: open first, earlier due first, none last, higher priority, newer.</summary>
    [Fact]
    public void ListOrder_matches_the_list_sql_order()
    {
        var o = TaskTreeRules.ListOrder;
        Assert.True(o.Compare(K(Guid.NewGuid()), K(Guid.NewGuid(), status: TaskItemStatus.Done, due: new DateOnly(2020, 1, 1))) < 0);
        Assert.True(o.Compare(K(Guid.NewGuid(), due: new DateOnly(2026, 1, 1)), K(Guid.NewGuid(), due: new DateOnly(2026, 2, 1))) < 0);
        Assert.True(o.Compare(K(Guid.NewGuid(), due: new DateOnly(2026, 2, 1)), K(Guid.NewGuid())) < 0);
        Assert.True(o.Compare(K(Guid.NewGuid(), priority: TaskPriority.Critical), K(Guid.NewGuid(), priority: TaskPriority.Low)) < 0);
        Assert.True(o.Compare(K(Guid.NewGuid(), created: Now.AddDays(1)), K(Guid.NewGuid(), created: Now)) < 0);
    }

    private static TaskItem T(Guid id, Guid? parent = null, bool hasSubtasks = false, DateOnly? due = null) =>
        new() { Id = id, ParentTaskId = parent, HasSubtasks = hasSubtasks, DueDate = due, Status = TaskItemStatus.Todo, Priority = TaskPriority.Medium, CreatedAt = Now };

    /// <summary>SUB-016: a match's ancestors from the full set become context rows; tasks with no matching descendant are left out; rows map back to their task.</summary>
    [Fact]
    public void ForTasks_adds_ancestors_as_context_and_drops_unrelated_tasks()
    {
        Guid r = Guid.NewGuid(), p = Guid.NewGuid(), c = Guid.NewGuid(), other = Guid.NewGuid(), sibling = Guid.NewGuid();
        var all = new[] { T(r, hasSubtasks: true), T(p, r, true), T(c, p), T(sibling, p), T(other) };
        var rows = TaskTreeRules.ForTasks(all, t => t.Id == c);
        Assert.Equal([r, p, c], rows.Select(x => x.Task.Id));
        Assert.Equal([0, 1, 2], rows.Select(x => x.Depth));
        Assert.Equal([true, true, false], rows.Select(x => x.IsContext));
        Assert.Equal([true, true, false], rows.Select(x => x.HasShownChildren));
        Assert.Same(all[2], rows[2].Task);
    }

    /// <summary>SUB-016: a match whose parent is absent from the set is a root.</summary>
    [Fact]
    public void ForTasks_treats_a_match_with_an_unknown_parent_as_a_root()
    {
        var c = Guid.NewGuid();
        var row = Assert.Single(TaskTreeRules.ForTasks([T(c, Guid.NewGuid())], _ => true));
        Assert.Equal(0, row.Depth);
        Assert.False(row.IsContext);
    }

    /// <summary>
    /// Review Focus 3, for the task tables' rows: collapsing a parent hides every task row under it and nothing else, the rows
    /// staying the same objects in the same order.
    /// </summary>
    [Fact]
    public void Visible_task_rows_hide_everything_under_a_collapsed_parent()
    {
        Guid r = Guid.NewGuid(), p = Guid.NewGuid(), c = Guid.NewGuid(), s = Guid.NewGuid();
        var rows = TaskTreeRules.ForTasks([T(r, hasSubtasks: true), T(p, r, true), T(c, p), T(s)], _ => true);
        Assert.Equal([r, p, c, s], rows.Select(x => x.Task.Id));

        Assert.Equal([r, p, s], TaskTreeRules.Visible(rows, new HashSet<Guid> { p }).Select(x => x.Task.Id));
        Assert.Equal([r, s], TaskTreeRules.Visible(rows, new HashSet<Guid> { r, p }).Select(x => x.Task.Id));
        var all = TaskTreeRules.Visible(rows, new HashSet<Guid> { c, s });
        Assert.Equal(rows, all);
    }

    private static TaskItem In(Guid department, Guid? parent = null, bool hasSubtasks = false) =>
        new()
        {
            Id = Guid.NewGuid(), DepartmentId = department, ParentTaskId = parent, HasSubtasks = hasSubtasks, Status = TaskItemStatus.Todo,
            CreatedAt = Now, Assignments = Assigned.To()
        };

    /// <summary>
    /// SUB-016 with §6.5 scoping (ruling R13): a context ancestor the viewer may not view shows its title only; one they may
    /// view, and any row that matched, keeps its cells.
    /// </summary>
    [Fact]
    public void TitleOnly_is_a_context_row_the_viewer_may_not_view()
    {
        Guid mine = Guid.NewGuid(), theirs = Guid.NewGuid();
        var viewer = Access.TestActors.Member(mine);
        var hidden = In(theirs, hasSubtasks: true);
        var visible = In(mine, hasSubtasks: true);
        var child = In(mine, hidden.Id);

        Assert.True(TaskTreeRules.TitleOnly(viewer, new TaskTreeRow(hidden, 0, true, true)));
        Assert.False(TaskTreeRules.TitleOnly(viewer, new TaskTreeRow(visible, 0, true, true)));
        Assert.False(TaskTreeRules.TitleOnly(viewer, new TaskTreeRow(hidden, 0, false, false)));
        Assert.False(TaskTreeRules.TitleOnly(viewer, new TaskTreeRow(child, 1, false, false)));
        Assert.False(TaskTreeRules.TitleOnly(Access.TestActors.SystemAdmin(), new TaskTreeRow(hidden, 0, true, true)));
    }
}
