using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>Model for the shared task table partial.</summary>
public sealed class TaskRowsVm
{
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
    public required Actor Actor { get; init; }
    public required string ReturnUrl { get; init; }
    /// <summary>
    /// When set, rows the actor may edit get inline assignee and due-date controls (like the inline status control).
    /// Candidates are filtered per row to the task's department plus System Admins. Null = read-only cells.
    /// </summary>
    public IReadOnlyList<UserSummary>? QuickEditAssignees { get; init; }
    /// <summary>
    /// Inline due-date control on rows the actor may edit, without the assignee control (My Tasks, where every row is
    /// the viewer's own). Implied when <see cref="QuickEditAssignees"/> is set.
    /// </summary>
    public bool QuickEditDueDate { get; init; }
    /// <summary>
    /// When set (with <see cref="ShowSprint"/>), open rows the actor may plan get an inline sprint control (§6.3) offering
    /// the backlog and these sprints: the planned and active ones. Null = read-only cells.
    /// </summary>
    public IReadOnlyList<Sprint>? QuickEditSprints { get; init; }
    /// <summary>Show the "Today" day-plan checkbox column (§6.12) on open rows the actor may plan.</summary>
    public bool ShowPlanToday { get; init; }
    public bool ShowProject { get; init; } = true;
    public bool ShowDepartment { get; init; } = true;
    public bool ShowSprint { get; init; }
    public bool ShowAssignee { get; init; } = true;
    /// <summary>Show each task's estimated effort (§6.10) as an "Est." column - the Today page's load view (§6.12).</summary>
    public bool ShowEstimate { get; init; }
    /// <summary>When set, each plannable row gets a checkbox bound (via the HTML form attribute) to the form with this id.</summary>
    public string? SelectionFormId { get; init; }
    /// <summary>Tasks whose next move is gated by a dependency (§6.15), with the reason for the Gated badge. Null = badge not computed.</summary>
    public IReadOnlyDictionary<Guid, WaitingSummary>? Waiting { get; init; }
    /// <summary>How many times each task has been carried over on the day plan (§6.12), for the "Carried over" badge. Null = badge not computed.</summary>
    public IReadOnlyDictionary<Guid, int>? CarryOvers { get; init; }
    public string? EmptyMessage { get; init; }
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}

/// <summary>
/// Model for the inline status control partial (spec §6.2): a select that posts to /Tasks/Status as soon as a status is picked, on
/// task list rows, the task page and sprint board cards. Picking Done asks first, in site.js: the "no time logged" question
/// (§6.10), then the solution-notes dialog the layout renders once (§6.2.4), whose notes are posted with the status.
/// </summary>
public sealed class StatusControlVm
{
    public required TaskItem Task { get; init; }
    /// <summary>The statuses the viewer may pick: <see cref="Ui.AllowedStatuses"/>.</summary>
    public required IReadOnlyList<TaskItemStatus> Statuses { get; init; }
    public string? ReturnUrl { get; init; }
    /// <summary>How the form sits in its cell, header or card.</summary>
    public string FormClass { get; init; } = "d-inline";
    /// <summary>Classes for the select beyond the small form-select: <c>status-select</c> sizes it for a table or the task page.</summary>
    public string SelectClass { get; init; } = "status-select";
    /// <summary>What the select is called: its aria-label, or with <see cref="ShowLabel"/> a visible label (which needs an <see cref="Id"/>).</summary>
    public string Label { get; init; } = "Status";
    public bool ShowLabel { get; init; }
    public string? Id { get; init; }
}

/// <summary>
/// Model for the inline assignee control partial (spec §6.2, §6.2.3): a select that posts to /Tasks/Quick as soon as someone is
/// picked, setting the task's one assignee or nobody. It is for a task with at most one assignee; the caller shows several by name.
/// </summary>
public sealed class AssigneeControlVm
{
    public required TaskItem Task { get; init; }
    /// <summary>The people this task may be given to: its department's members plus those who see every department.</summary>
    public required IReadOnlyList<UserSummary> Candidates { get; init; }
    public string? ReturnUrl { get; init; }
    /// <summary>How the form sits in its cell.</summary>
    public string FormClass { get; init; } = "d-inline";
    /// <summary>Classes for the select beyond the small form-select: <c>assignee-select</c> sizes it for a table.</summary>
    public string SelectClass { get; init; } = "assignee-select";
    /// <summary>The select's aria-label.</summary>
    public string Label { get; init; } = "Assignee";
}

/// <summary>
/// Model for the inline due-date control partial (spec §6.2): a date picker that posts to /Tasks/Quick once typing pauses
/// (site.js), and clears the due date when emptied.
/// </summary>
public sealed class DueDateControlVm
{
    public required TaskItem Task { get; init; }
    /// <summary>The day an overdue date is judged against, shown in red.</summary>
    public required DateOnly Today { get; init; }
    public string? ReturnUrl { get; init; }
    /// <summary>How the form sits in its cell.</summary>
    public string FormClass { get; init; } = "d-inline";
    /// <summary>Classes for the input beyond the small form-control: <c>due-input</c> sizes it for a table.</summary>
    public string InputClass { get; init; } = "due-input";
    /// <summary>The input's aria-label.</summary>
    public string Label { get; init; } = "Due date";
}

/// <summary>
/// Model for the inline sprint control partial (spec §6.2, §6.3): a select that posts to /Tasks/Quick as soon as a sprint is
/// picked, planning the task into it or back to the backlog.
/// </summary>
public sealed class SprintControlVm
{
    public required TaskItem Task { get; init; }
    /// <summary>The sprints a task can be planned into: the planned and active ones (<see cref="Orbit.Application.Services.SprintService.ListOpenAsync"/>).</summary>
    public required IReadOnlyList<Sprint> Sprints { get; init; }
    public string? ReturnUrl { get; init; }
    /// <summary>How the form sits in its cell.</summary>
    public string FormClass { get; init; } = "d-inline";
    /// <summary>Classes for the select beyond the small form-select: <c>sprint-select</c> sizes it for a table.</summary>
    public string SelectClass { get; init; } = "sprint-select";
    /// <summary>The select's aria-label.</summary>
    public string Label { get; init; } = "Sprint";
}

/// <summary>
/// Model for the dashboard's My waiting approvals card (spec §6.9): the request approvals asked of the viewer, oldest first, and
/// who the viewer is, so a request they logged themselves reads "logged by you".
/// </summary>
public sealed record WaitingApprovalsVm(IReadOnlyList<RequestActionItem> Items, Guid? MeId)
{
    /// <summary>How many rows the card lists before it links to the Requests page for the rest.</summary>
    public const int Shown = 8;
}
