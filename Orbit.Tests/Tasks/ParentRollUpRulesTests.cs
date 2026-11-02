using Orbit.Application;
using Orbit.Data.Entities;
using static Orbit.Data.Entities.TaskItemStatus;
using static Orbit.Data.Entities.TaskPriority;

namespace Orbit.Tests.Tasks;

/// <summary>A parent task's values come from its subtasks (design spec 2026-11-02 §3.1 and §3.2).</summary>
public class ParentRollUpRulesTests
{
    private static SubtaskFacts S(TaskItemStatus status = Todo, TaskPriority priority = Medium, DateOnly? start = null,
        DateOnly? due = null, int? estimate = null, Guid? sprint = null, DateOnly? sprintStart = null, bool backlog = false) =>
        new(status, priority, start, due, estimate, sprint, sprintStart, backlog);

    private static RolledUpValues Of(params SubtaskFacts[] s) => ParentRollUpRules.Of(s);

    private static DateOnly Nov(int n) => new(2026, 11, n);

    /// <summary>SUB-001: every subtask cancelled gives Cancelled.</summary>
    [Fact]
    public void All_cancelled_is_cancelled() =>
        Assert.Equal(Cancelled, ParentRollUpRules.Status([Cancelled, Cancelled]));

    /// <summary>SUB-002: all Done (cancelled ignored) gives Done.</summary>
    [Fact]
    public void All_done_is_done() =>
        Assert.Equal(Done, ParentRollUpRules.Status([Done, Done, Cancelled]));

    /// <summary>SUB-003: all Todo (cancelled ignored) gives Todo.</summary>
    [Fact]
    public void All_todo_is_todo() =>
        Assert.Equal(Todo, ParentRollUpRules.Status([Todo, Todo, Cancelled]));

    /// <summary>SUB-004: when every open subtask is Blocked or Waiting, Blocked wins a mix.</summary>
    [Fact]
    public void Blocked_and_waiting()
    {
        Assert.Equal(Blocked, ParentRollUpRules.Status([Blocked, Blocked]));
        Assert.Equal(Blocked, ParentRollUpRules.Status([Done, Blocked]));
        Assert.Equal(Waiting, ParentRollUpRules.Status([Waiting, Done]));
        Assert.Equal(Blocked, ParentRollUpRules.Status([Waiting, Blocked]));
    }

    /// <summary>SUB-005: anything else is In Progress.</summary>
    [Fact]
    public void Anything_else_is_in_progress()
    {
        foreach (var mix in new TaskItemStatus[][] { [Todo, Done], [Todo, Blocked], [Todo, Waiting], [InProgress, Blocked], [Todo, InProgress] })
            Assert.Equal(InProgress, ParentRollUpRules.Status(mix));
    }

    /// <summary>SUB-022: a new open subtask under a Done parent reopens it.</summary>
    [Fact]
    public void New_open_subtask_reopens_a_done_parent() =>
        Assert.Equal(InProgress, ParentRollUpRules.Status([Done, Todo]));

    /// <summary>SUB-010: rolling up two groups of subtasks equals rolling up the leaves, for every status combination.</summary>
    [Fact]
    public void Rolling_up_groups_equals_rolling_up_leaves()
    {
        var all = Enum.GetValues<TaskItemStatus>();
        foreach (var a in all) foreach (var b in all) foreach (var c in all) foreach (var d in all)
            Assert.Equal(ParentRollUpRules.Status([a, b, c, d]),
                         ParentRollUpRules.Status([ParentRollUpRules.Status([a, b]), ParentRollUpRules.Status([c, d])]));
    }

    /// <summary>SUB-006: highest open priority; none open gives highest overall; cancelled ignored.</summary>
    [Fact]
    public void Priority_is_highest_open()
    {
        Assert.Equal(Low, Of(S(Todo, Low), S(Done, Critical)).Priority);
        Assert.Equal(High, Of(S(Done, Low), S(Done, High)).Priority);
        Assert.Equal(Low, Of(S(Todo, Low), S(Cancelled, Critical)).Priority);
    }

    /// <summary>SUB-007: earliest start, latest due, missing dates skipped, cancelled ignored.</summary>
    [Fact]
    public void Dates_span_the_subtasks()
    {
        var v = Of(S(start: Nov(3), due: Nov(10)), S(start: Nov(1)), S(Cancelled, start: Nov(1).AddDays(-30), due: Nov(30)));
        Assert.Equal(Nov(1), v.StartDate);
        Assert.Equal(Nov(10), v.DueDate);
        Assert.Null(Of(S(), S()).StartDate);
        Assert.Null(Of(S(), S()).DueDate);
    }

    /// <summary>SUB-008: the estimate is the sum, null when no subtask has one.</summary>
    [Fact]
    public void Estimate_is_the_sum()
    {
        Assert.Equal(90, Of(S(estimate: 60), S(), S(estimate: 30), S(Cancelled, estimate: 600)).EstimateMinutes);
        Assert.Null(Of(S(), S()).EstimateMinutes);
    }

    /// <summary>SUB-009: earliest open sprint by start; else Backlog, else Not Planned; all closed uses the same rule over non-cancelled.</summary>
    [Fact]
    public void Sprint_is_the_earliest_open_one()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), z = Guid.NewGuid();
        Assert.Equal(SprintPlacement.In(a), Of(S(sprint: b, sprintStart: Nov(15)), S(sprint: a, sprintStart: Nov(1)), S(Done, sprint: z, sprintStart: Nov(1).AddDays(-31))).Sprint);
        Assert.Equal(SprintPlacement.Backlog, Of(S(backlog: true), S(), S(Done, sprint: z, sprintStart: Nov(1))).Sprint);
        Assert.Equal(SprintPlacement.NotPlanned, Of(S(), S(Done, backlog: true)).Sprint);
        Assert.Equal(SprintPlacement.In(a), Of(S(Done, sprint: b, sprintStart: Nov(15)), S(Done, sprint: a, sprintStart: Nov(1))).Sprint);
    }

    /// <summary>An empty subtask list is a programming error.</summary>
    [Fact]
    public void Empty_input_throws()
    {
        Assert.Throws<ArgumentException>(() => ParentRollUpRules.Of([]));
        Assert.Throws<ArgumentException>(() => ParentRollUpRules.Status([]));
    }

    /// <summary>SUB-011: the stamps are those a manual status change sets today.</summary>
    [Fact]
    public void Status_stamps()
    {
        var t = new TaskItem { Status = Todo };
        var now = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        TaskStatusStamps.Apply(t, InProgress, now);
        Assert.Equal(now, t.FirstRespondedAt);
        Assert.Null(t.CompletedAt);
        TaskStatusStamps.Apply(t, Done, now.AddHours(1));
        Assert.Equal(now.AddHours(1), t.CompletedAt);
        TaskStatusStamps.Apply(t, InProgress, now.AddHours(2));
        Assert.Null(t.CompletedAt);
        Assert.Equal(now, t.FirstRespondedAt);
    }
}
