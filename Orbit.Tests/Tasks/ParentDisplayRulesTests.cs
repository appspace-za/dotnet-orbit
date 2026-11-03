using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Tasks;

/// <summary>A parent row's display-only values, from its bottom-level descendants (design spec 2026-11-02 §4; orbit-spec §6.15).</summary>
public class ParentDisplayRulesTests
{
    private static readonly Guid P = Guid.NewGuid(), A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static DescendantFacts F(Guid id, Guid parent, bool hasSubtasks = false, TaskItemStatus status = TaskItemStatus.Todo,
        DateOnly? due = null, Guid? sprint = null, string? sprintName = null, DateOnly? sprintStart = null, params string[] who) =>
        new(id, parent, hasSubtasks, status, due, sprint, sprintName, sprintStart, who);

    private static ParentSummary Of(DateOnly today, params DescendantFacts[] facts) =>
        ParentDisplayRules.Summarise([P], facts, today)[P];

    private static DescendantFacts Pa() => F(A, P, hasSubtasks: true);

    /// <summary>SUB-012: statuses count bottom-level tasks in enum order; the sub-parent is not counted; SubtaskCount is the direct children.</summary>
    [Fact]
    public void Statuses_count_the_bottom_level()
    {
        var s = Of(new DateOnly(2026, 11, 12), Pa(), F(B, P, status: TaskItemStatus.Blocked),
            F(C, A, status: TaskItemStatus.Done), F(D, A, status: TaskItemStatus.InProgress));
        Assert.Equal([new StatusCount(TaskItemStatus.InProgress, 1), new StatusCount(TaskItemStatus.Blocked, 1), new StatusCount(TaskItemStatus.Done, 1)], s.Statuses);
        Assert.Equal(2, s.SubtaskCount);
    }

    /// <summary>SUB-013: next due is the earliest open date; overdue when any open one is past; the range shows one date when they agree.</summary>
    [Fact]
    public void Next_due_and_overdue()
    {
        var s = Of(new DateOnly(2026, 11, 12), Pa(), F(B, P, due: new DateOnly(2026, 12, 15)),
            F(C, A, status: TaskItemStatus.Done, due: new DateOnly(2026, 10, 1)), F(D, A, due: new DateOnly(2026, 11, 10)));
        Assert.Equal(new DateOnly(2026, 11, 10), s.NextDue);
        Assert.True(s.Overdue);
        Assert.Equal((new DateOnly(2026, 11, 10), new DateOnly(2026, 12, 15)), ParentDisplayRules.DueRange(new DateOnly(2026, 11, 10), new DateOnly(2026, 12, 15)));
        Assert.Equal((null, new DateOnly(2026, 12, 15)), ParentDisplayRules.DueRange(new DateOnly(2026, 12, 15), new DateOnly(2026, 12, 15)));
        Assert.Equal((null, new DateOnly(2026, 12, 15)), ParentDisplayRules.DueRange(null, new DateOnly(2026, 12, 15)));
    }

    /// <summary>SUB-014: the sprint range is the first and last open sprint by start; open tasks in none are unplanned.</summary>
    [Fact]
    public void Sprint_range_and_unplanned()
    {
        Guid s12 = Guid.NewGuid(), s14 = Guid.NewGuid();
        var today = new DateOnly(2026, 11, 12);
        var s = Of(today, Pa(), F(B, P, sprint: s14, sprintName: "Sprint 14", sprintStart: new DateOnly(2026, 12, 1)),
            F(C, A), F(D, A, sprint: s12, sprintName: "Sprint 12", sprintStart: new DateOnly(2026, 11, 1)));
        Assert.Equal(("Sprint 12", "Sprint 14", 1), (s.FirstSprint, s.LastSprint, s.Unplanned));
        var none = Of(today, F(B, P));
        Assert.Null(none.FirstSprint);
        Assert.Null(none.LastSprint);
    }

    /// <summary>SUB-015: assignees are the distinct names on open bottom-level tasks, sorted.</summary>
    [Fact]
    public void Assignees_are_distinct_and_open_only()
    {
        var s = Of(new DateOnly(2026, 11, 12), Pa(), F(B, P, who: "Alice"),
            F(C, A, status: TaskItemStatus.Done, who: "Zed"), F(D, A, who: ["Bob", "Alice"]));
        Assert.Equal(["Alice", "Bob"], s.Assignees);
    }
}
