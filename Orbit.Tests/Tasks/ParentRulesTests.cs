using Orbit.Application;
using Orbit.Data.Entities;
using Orbit.Helpers;
using Orbit.Tests.Access;
using static Orbit.Data.Entities.TaskItemStatus;

namespace Orbit.Tests.Tasks;

/// <summary>
/// SUB-020, SUB-021: a task with subtasks takes its status, priority, dates, estimate, sprint and assignees from them, so those
/// can't be set by hand; it can't be planned, taken or have time logged on it; and closing the last open subtask finishes it.
/// </summary>
public class ParentRulesTests
{
    private static readonly Guid It = Guid.NewGuid();

    private static TaskItem Parent() =>
        new() { Number = "T-26-00012", HasSubtasks = true, Assignments = Assigned.To(), DepartmentId = It };

    private static TaskItem Plain() =>
        new() { Number = "T-26-00012", HasSubtasks = false, Assignments = Assigned.To(), DepartmentId = It };

    [Fact]
    public void SUB_020_derived_fields_are_refused_on_a_parent_allowed_unchanged_and_ignored_on_a_plain_task()
    {
        var p = Parent();
        p.DueDate = new DateOnly(2026, 11, 10);
        Assert.Null(ParentRules.DerivedChange(p, p.Status, p.Priority, p.StartDate, p.DueDate, p.EstimateMinutes, SprintPlacement.Of(p), false));
        Assert.Equal("due date", ParentRules.DerivedChange(p, p.Status, p.Priority, p.StartDate, new DateOnly(2026, 11, 11), p.EstimateMinutes, SprintPlacement.Of(p), false));
        Assert.Equal("assignees", ParentRules.DerivedChange(p, p.Status, p.Priority, p.StartDate, p.DueDate, p.EstimateMinutes, SprintPlacement.Of(p), true));
        Assert.Null(ParentRules.DerivedChange(Plain(), Done, TaskPriority.Critical, null, null, 60, SprintPlacement.Backlog, true));
        Assert.Equal("T-26-00012 has subtasks; its due date comes from them.", ParentRules.DerivedMessage(p, "due date"));
        Assert.Throws<ValidationException>(() => ParentRules.RequireNotParent(p, "sprint"));
        ParentRules.RequireNotParent(Plain(), "sprint");
    }

    [Fact]
    public void SUB_020_no_planning_taking_or_time_on_a_parent()
    {
        var member = TestActors.Member(It);
        Assert.False(AccessPolicy.CanPlanTask(member, Parent()));
        Assert.True(AccessPolicy.CanPlanTask(member, Plain()));
        Assert.False(AccessPolicy.CanTakeTask(member, Parent()));
        Assert.True(AccessPolicy.CanTakeTask(member, Plain()));
        Assert.False(AccessPolicy.CanLogTimeForOthers(TestActors.DepartmentAdmin(It), Parent()));
        Assert.False(AccessPolicy.CanLogTimeFor(TestActors.DepartmentAdmin(It), Parent(), Guid.NewGuid()));
        Assert.Equal([Parent().Status], Ui.AllowedStatuses(TestActors.SystemAdmin(), Parent()));
    }

    [Fact]
    public void SUB_021_closing_the_last_open_subtask_finishes_the_parent_but_closing_another_does_not()
    {
        Assert.True(ParentRollUpRules.FinishesParent([Done, Cancelled], Done, InProgress));
        Assert.True(ParentRollUpRules.FinishesParent([Done], Cancelled, InProgress));
        Assert.False(ParentRollUpRules.FinishesParent([Todo], Done, InProgress));
        Assert.False(ParentRollUpRules.FinishesParent([Done], Done, Done));
    }
}
