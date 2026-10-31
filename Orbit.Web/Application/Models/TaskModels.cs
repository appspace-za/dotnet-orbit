using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class TaskFilter
{
    public Guid? ProjectId { get; set; }
    /// <summary>Only tasks on no project - standalone tasks. Takes precedence over <see cref="ProjectId"/>.</summary>
    public bool NoProject { get; set; }
    public Guid? DepartmentId { get; set; }
    public TaskItemStatus? Status { get; set; }
    /// <summary>Only tasks this person is one of the assignees of (§6.2.3).</summary>
    public Guid? AssigneeId { get; set; }
    /// <summary>Only tasks with no assignee - work nobody has picked up yet. Takes precedence over <see cref="AssigneeId"/>.</summary>
    public bool Unassigned { get; set; }
    /// <summary>
    /// Only tasks created for this person - their requestee (§6.2.2). "Requestee: me" finds one another department holds too,
    /// since a person's lists include their own tasks wherever they are filed (<see cref="Scoping.Tasks"/>).
    /// </summary>
    public Guid? RequesteeId { get; set; }
    /// <summary>Only tasks with no requestee - created for nobody else (§6.2.2). Takes precedence over <see cref="RequesteeId"/>.</summary>
    public bool NoRequestee { get; set; }
    public TaskPriority? Priority { get; set; }
    public TaskType? Type { get; set; }
    public TaskSource? Source { get; set; }
    public DateOnly? DueBefore { get; set; }
    public DateOnly? DueAfter { get; set; }
    /// <summary>Only tasks due in this window, counted from today (UTC date, <see cref="DueWindowRules"/>); on top of the dates above.</summary>
    public DueWindow? Due { get; set; }
    public Guid? SprintId { get; set; }
    public Guid? RecurringTaskDefinitionId { get; set; }
    /// <summary>Only the direct subtasks of this task (§6.15).</summary>
    public Guid? ParentTaskId { get; set; }
    /// <summary>Only tasks about this asset (§6.19).</summary>
    public Guid? AssetId { get; set; }
    /// <summary>Only tasks on the Backlog: no sprint, waiting for sprint planning (§6.3).</summary>
    public bool BacklogOnly { get; set; }
    /// <summary>Only Not Planned tasks: no sprint and not on the Backlog (§6.3).</summary>
    public bool NotPlannedOnly { get; set; }
    /// <summary>Exclude Done/Cancelled.</summary>
    public bool OpenOnly { get; set; }
    /// <summary>Only tasks on the day plan for this date (§6.12).</summary>
    public DateOnly? PlannedFor { get; set; }
    /// <summary>Only tasks on today's day plan (UTC date). Ignored when <see cref="PlannedFor"/> is set.</summary>
    public bool PlannedToday { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>The Tasks list's Due filter (§6.2, decision 135): due dates counted from today, so a link to it never goes stale.</summary>
public enum DueWindow
{
    /// <summary>Due before today.</summary>
    Overdue,
    /// <summary>Due today or in the next <see cref="DueWindowRules.SoonDays"/> days.</summary>
    Soon
}

/// <summary>Input for creating a task, or the complete new state when updating one.</summary>
public sealed class TaskInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ProjectId { get; set; }
    /// <summary>
    /// The task's department. Null = the project's department when ProjectId is set (or, on update, the task's
    /// current department while it stays on the same project); otherwise the caller's own department.
    /// A caller with tasks.create everywhere may pass a department other than the project's to file a cross-department project task (§6.2.1).
    /// </summary>
    public Guid? DepartmentId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public TaskType Type { get; set; } = TaskType.Task;
    /// <summary>Estimated effort in minutes (§6.10). Null or 0 = no estimate.</summary>
    public int? EstimateMinutes { get; set; }
    /// <summary>
    /// The complete set of assignees (§6.2.3); empty = nobody. Null on create also means nobody; null on update keeps the task's
    /// assignees as they are. Each person added must be an active user in the task's department, or one whose role sees tasks
    /// everywhere; people kept aren't re-checked unless the task changes department.
    /// </summary>
    public IReadOnlyList<Guid>? AssigneeIds { get; set; }
    /// <summary>
    /// The requestee, whom the task is for (§6.2.2); null = nobody else. Must be an active person within the caller's
    /// tasks.create_for reach. On update a change needs that reach over the person removed as well as the person added; an
    /// unchanged requestee is kept without the check.
    /// </summary>
    public Guid? RequesteeId { get; set; }
    /// <summary>Planned start (§6.15); never after <see cref="DueDate"/>.</summary>
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>The parent task (§6.15): a task on the same project (or a standalone task in the same department). Null = top level.</summary>
    public Guid? ParentTaskId { get; set; }
    /// <summary>
    /// The asset the task is about (§6.19); null = none. A new or changed asset must be one the caller can see that isn't disposed;
    /// on update, the task's current asset is kept without that check.
    /// </summary>
    public Guid? AssetId { get; set; }
    /// <summary>Null on create = Todo; null on update = unchanged.</summary>
    public TaskItemStatus? Status { get; set; }
    /// <summary>How the task was resolved (§6.2.4); null = none. Like the description, the complete new value on update. Required when the status is Done.</summary>
    public string? SolutionNotes { get; set; }
    /// <summary>The sprint to plan the task into; null = the Backlog or Not Planned, as <see cref="Backlog"/> says (§6.3).</summary>
    public Guid? SprintId { get; set; }
    /// <summary>
    /// With no sprint: true = the Backlog, false = Not Planned. Ignored when <see cref="SprintId"/> is set. Null on create = the
    /// default (<see cref="SprintPlacement.DefaultFor"/>: the Backlog on a project, otherwise Not Planned); null on update = the
    /// task's own flag, or the Backlog for a task leaving a sprint (<see cref="SprintPlacement.WithoutSprint"/>).
    /// </summary>
    public bool? Backlog { get; set; }
    /// <summary>Optional client-supplied key so retried API creates don't duplicate.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>One day's plan (spec §6.12): what the team picked to work on that day, and what was left over from last time.</summary>
public sealed class DayPlan
{
    public required DateOnly Date { get; init; }
    /// <summary>Every task with PlannedFor == Date, any status, so "done today" stays visible.</summary>
    public IReadOnlyList<TaskItem> Planned { get; init; } = [];
    /// <summary>The most recent day before Date that still has open planned tasks in scope; null when there are none.</summary>
    public DateOnly? PreviousDate { get; init; }
    /// <summary>Open tasks still sitting on PreviousDate's plan - candidates to carry over.</summary>
    public IReadOnlyList<TaskItem> Unfinished { get; init; } = [];
    /// <summary>How many times each task in Planned or Unfinished has been carried over to a later day's plan; absent = never.</summary>
    public IReadOnlyDictionary<Guid, int> CarryOvers { get; init; } = new Dictionary<Guid, int>();
    /// <summary>The tasks in Planned carried over onto Date from an earlier day's plan, with the day each came from (from the audit trail).</summary>
    public IReadOnlyDictionary<Guid, DateOnly> CarriedIn { get; init; } = new Dictionary<Guid, DateOnly>();
    /// <summary>
    /// The last plan before Date (<see cref="DayPlanRules.LastPlanDate"/>): the latest earlier day with tasks in scope still dated
    /// it, or tasks carried over from it to Date. Null when there is none. Usually <see cref="PreviousDate"/>, which only counts
    /// days with open tasks left on them.
    /// </summary>
    public DateOnly? LastPlanDate { get; init; }
    /// <summary>The Done and Cancelled tasks still dated LastPlanDate: what got finished on the last plan.</summary>
    public IReadOnlyList<TaskItem> LastPlanClosed { get; init; } = [];
    /// <summary>The day <see cref="Logged"/> covers: LastPlanDate, or the day before Date when there is no earlier plan.</summary>
    public DateOnly LoggedDate { get; init; }
    /// <summary>The time logged on LoggedDate on tasks in scope, planned or not, per person and task.</summary>
    public IReadOnlyList<LoggedTask> Logged { get; init; } = [];
    public int DoneCount => Planned.Count(t => t.Status == TaskItemStatus.Done);
    public int OpenCount => Planned.Count(t => t.IsOpen);
    public int OverdueCount => Planned.Count(t => t.IsOverdue(Date));
    /// <summary>Whether there is an earlier plan to look back on, the Today page's Yesterday view. Any Unfinished task means there is.</summary>
    public bool HasLastPlan => LastPlanDate is not null;
}

/// <summary>The time one person logged on one task on a day (§6.10), for the Today page's stand-up (§6.12).</summary>
public sealed record LoggedTask(Guid UserId, string UserName, Guid TaskId, string Number, string Title, int Minutes);
