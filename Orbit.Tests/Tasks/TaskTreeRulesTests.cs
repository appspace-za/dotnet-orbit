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
}
