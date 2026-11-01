using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>The Planning page's filters (spec §6.3): the sprint being planned, then which tasks are listed and counted.</summary>
public sealed class SprintPlanningFilter
{
    /// <summary>The sprint being planned. Null, or one that is no longer open, picks the default (<see cref="SprintPlanningRules.DefaultTarget"/>).</summary>
    public Guid? SprintId { get; set; }
    public Guid? DepartmentId { get; set; }
    /// <summary>Only tasks this person is one of the assignees of.</summary>
    public Guid? AssigneeId { get; set; }
    /// <summary>Only tasks nobody is assigned to. Takes precedence over <see cref="AssigneeId"/>.</summary>
    public bool Unassigned { get; set; }
    public string? Search { get; set; }

    /// <summary>A filter narrows the tasks, so the sprint's own tasks, for each person's load, are read on their own (§6.3).</summary>
    public bool Narrows => DepartmentId is not null || AssigneeId is not null || Unassigned || !string.IsNullOrWhiteSpace(Search);
}

/// <summary>What a row's one button does to a task, measured against the sprint being planned (§6.3).</summary>
public enum PlanningAction
{
    /// <summary>From the Backlog or Not Planned into the sprint.</summary>
    Plan,
    /// <summary>Out of the sprint, back to the Backlog.</summary>
    Remove,
    /// <summary>From another sprint into this one.</summary>
    MoveHere
}

/// <summary>
/// The size of a set of tasks: how many there are, how many have no estimate, the sum of their estimates, and what is left of
/// those estimates after the time already logged (<see cref="SprintPlanningRules.Remaining"/>).
/// </summary>
public sealed record PlanningLoad(int Tasks, int Unestimated, int EstimatedMinutes, int RemainingMinutes)
{
    public static readonly PlanningLoad None = new(0, 0, 0, 0);

    /// <summary>Time has been logged against the estimate, so the remaining figure differs from the estimate.</summary>
    public bool HasLogged => RemainingMinutes != EstimatedMinutes;
}

/// <summary>A project panel's figures: its open tasks, those in the sprint being planned and on the Backlog, and the rest.</summary>
public sealed record PlanningCounts(int Open, PlanningLoad Target, PlanningLoad Backlog, int OtherSprints, int NotPlanned);

/// <summary>One project's open tasks on the Planning page, or the standalone ones when <see cref="Project"/> is null.</summary>
public sealed record PlanningGroup(Project? Project, IReadOnlyList<TaskItem> Tasks, PlanningCounts Counts)
{
    /// <summary>The panel's key: the project's id, or <see cref="Guid.Empty"/> for the tasks on no project.</summary>
    public Guid Key => Project?.Id ?? Guid.Empty;
}

/// <summary>
/// A person's share of the sprint being planned: all their open tasks in it that the viewer may see, whatever the filters (§6.3,
/// decision 141). <see cref="UserId"/> null is the tasks nobody is assigned to.
/// </summary>
public sealed record PlanningPerson(Guid? UserId, string Name, PlanningLoad Load, bool Overloaded);

/// <summary>
/// The Planning page (spec §6.3): the open sprints, the sprint being planned, each person's capacity in it, its totals and the
/// Backlog's, each person's load, and the tasks by project (<see cref="SprintPlanningRules.Build"/>).
/// </summary>
public sealed record SprintPlanning(
    IReadOnlyList<Sprint> OpenSprints,
    Sprint? Target,
    DateOnly Today,
    int WorkingDays,
    int WorkingDayMinutes,
    PlanningLoad TargetTotals,
    PlanningLoad BacklogTotals,
    IReadOnlyList<PlanningPerson> People,
    IReadOnlyList<PlanningGroup> Groups,
    IReadOnlyDictionary<Guid, int> LoggedMinutes)
{
    /// <summary>One person's capacity in the sprint: its working days left (<see cref="WorkingDays"/>) times a working day.</summary>
    public int CapacityMinutes => WorkingDays * WorkingDayMinutes;

    public int OverloadedCount => People.Count(p => p.Overloaded);

    /// <summary>All the time logged on a task so far, by anyone.</summary>
    public int Logged(Guid taskId) => LoggedMinutes.GetValueOrDefault(taskId);
}
