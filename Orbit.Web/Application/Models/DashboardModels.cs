using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>Tasks per status (spec §6.9): the tiles, Today's plan, a sprint's progress and each bar of the dashboard's charts.</summary>
public sealed record StatusCounts(int Todo, int InProgress, int Waiting, int Blocked, int Done, int Cancelled)
{
    public static readonly StatusCounts None = new(0, 0, 0, 0, 0, 0);

    public int Open => Todo + InProgress + Waiting + Blocked;
    public int Closed => Done + Cancelled;
    public int Total => Open + Closed;
    /// <summary>Every task but the cancelled ones: the sprint's Work done is done of these (decision 135).</summary>
    public int NotCancelled => Total - Cancelled;

    public int Of(TaskItemStatus status) => status switch
    {
        TaskItemStatus.Todo => Todo,
        TaskItemStatus.InProgress => InProgress,
        TaskItemStatus.Waiting => Waiting,
        TaskItemStatus.Blocked => Blocked,
        TaskItemStatus.Done => Done,
        TaskItemStatus.Cancelled => Cancelled,
        _ => 0
    };

    public StatusCounts Plus(StatusCounts o) =>
        new(Todo + o.Todo, InProgress + o.InProgress, Waiting + o.Waiting, Blocked + o.Blocked, Done + o.Done, Cancelled + o.Cancelled);

    /// <summary>Counts from grouped rows; a status that comes more than once is summed, one that doesn't come is zero (DSH-003).</summary>
    public static StatusCounts From(IEnumerable<(TaskItemStatus Status, int Count)> rows)
    {
        var n = new int[6];
        foreach (var (status, count) in rows)
            if ((int)status is >= 0 and < 6) n[(int)status] += count;
        return new StatusCounts(n[(int)TaskItemStatus.Todo], n[(int)TaskItemStatus.InProgress], n[(int)TaskItemStatus.Waiting],
            n[(int)TaskItemStatus.Blocked], n[(int)TaskItemStatus.Done], n[(int)TaskItemStatus.Cancelled]);
    }
}

public sealed record EnumCount<T>(T Value, int Count) where T : struct, Enum;

/// <summary>Open tasks with one status, priority and type: the one query behind the dashboard's tiles and donuts.</summary>
public sealed record OpenFacet(TaskItemStatus Status, TaskPriority Priority, TaskType Type, int Count);

/// <summary>One bar of a by-assignee or by-department chart: its label and its open tasks per status.</summary>
public sealed record LoadBar(string Name, StatusCounts Counts);

/// <summary>One day of the Last 14 days chart: tasks created and tasks done that UTC day.</summary>
public sealed record DayCount(DateOnly Day, int Created, int Done);

/// <summary>How far through the active sprint today is: day <see cref="Day"/> of <see cref="Days"/>.</summary>
public sealed record SprintDay(int Day, int Days)
{
    public int Percent => Percentage.Of(Day, Days);
}

public sealed record DepartmentOption(Guid Id, string Name);

/// <summary>One department's part of the active sprint: its sprint tasks done, of those not cancelled (decision 135).</summary>
public sealed record DepartmentSprintProgress(string Department, int Total, int Done);

/// <summary>My work's Up for grabs line (§6.5): the department's open unassigned tasks, and the Tasks list of them.</summary>
public sealed record UpForGrabsLine(int Count, string Href);

/// <summary>
/// One view of the dashboard (spec §6.9, decision 134), as <c>DashboardService.BuildAsync</c> fills it: the same figures for
/// every view, over that view's scope, plus the parts only one view has.
/// </summary>
public sealed class DashboardModel
{
    public required Actor Actor { get; init; }
    /// <summary>The view shown; null for a viewer who reaches none (no tasks.view), who gets the places they can go instead.</summary>
    public required DashboardView? View { get; init; }
    /// <summary>The views the viewer reaches (<see cref="DashboardRules.Views"/>), for the switcher.</summary>
    public required IReadOnlyList<DashboardView> Views { get; init; }
    public required DateOnly Today { get; init; }
    public required DashboardLinks Links { get; init; }
    /// <summary>What the view counts, under the Open work card's heading.</summary>
    public required string ScopeLabel { get; init; }

    /// <summary>The viewer's own department, for the header and the Up for grabs line.</summary>
    public string? HomeDepartmentName { get; init; }
    /// <summary>The Department view's department; null on the other views.</summary>
    public Guid? DepartmentId { get; init; }
    /// <summary>The Department view's picker, for a viewer who sees every department; empty for anyone else.</summary>
    public IReadOnlyList<DepartmentOption> Departments { get; init; } = [];

    /// <summary>The open tasks per status: the first four tiles.</summary>
    public StatusCounts Open { get; init; } = StatusCounts.None;
    public int OverdueCount { get; init; }
    /// <summary>Open tasks due today or in the next <see cref="DueWindowRules.SoonDays"/> days.</summary>
    public int DueSoonCount { get; init; }
    public IReadOnlyList<EnumCount<TaskPriority>> ByPriority { get; init; } = [];
    public IReadOnlyList<EnumCount<TaskType>> ByType { get; init; } = [];

    /// <summary>My work, for a role that may take tasks in its department: the open tasks there nobody is assigned to (§6.5).</summary>
    public UpForGrabsLine? UpForGrabs { get; init; }

    /// <summary>Today's day plan (§6.12) within the view, closed tasks included; null without tasks.edit.</summary>
    public StatusCounts? TodayPlan { get; init; }

    /// <summary>The Department view: open tasks per assignee (§6.2.3: a shared task under each of them), then Unassigned.</summary>
    public IReadOnlyList<LoadBar> OpenByAssignee { get; init; } = [];
    /// <summary>The Company view: open tasks per department.</summary>
    public IReadOnlyList<LoadBar> OpenByDepartment { get; init; } = [];

    public IReadOnlyList<DayCount> Trend { get; init; } = [];

    public Sprint? ActiveSprint { get; init; }
    /// <summary>The whole sprint, every department's tasks: sprints are shared company-wide (§6.3).</summary>
    public StatusCounts Sprint { get; init; } = StatusCounts.None;
    /// <summary>The sprint's tasks within the view.</summary>
    public StatusCounts SprintInScope { get; init; } = StatusCounts.None;
    public SprintDay? SprintDay { get; init; }
    /// <summary>The Company view: each department's part of the sprint.</summary>
    public IReadOnlyList<DepartmentSprintProgress> SprintByDepartment { get; init; } = [];

    public IReadOnlyList<ProjectListItem> Projects { get; init; } = [];
}
