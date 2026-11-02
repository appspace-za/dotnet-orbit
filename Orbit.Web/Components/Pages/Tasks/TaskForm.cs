using System.ComponentModel.DataAnnotations;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Components.Pages.Tasks;

public sealed class TaskForm
{
    [Required, StringLength(300)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Display(Name = "Project")]
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Blank = default (the project's department, or the caller's own for a standalone task). A System Admin
    /// may pick a department other than the project's to file a cross-department project task (§6.2.1).
    /// </summary>
    [Display(Name = "Department")]
    public Guid? DepartmentId { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public TaskType Type { get; set; } = TaskType.Task;

    /// <summary>Estimated effort in minutes (§6.10); blank or 0 = no estimate.</summary>
    [Display(Name = "Estimate (minutes)"), Range(0, 525600)]
    public int? EstimateMinutes { get; set; }

    /// <summary>The complete set of assignees (§6.2.3), one posted per chip in the picker; none = unassigned.</summary>
    [Display(Name = "Assignees")]
    public List<Guid> AssigneeIds { get; set; } = [];

    /// <summary>The requestee (§6.2.2): the person the task is for. Blank = nobody else. Offered with tasks.create_for above Own.</summary>
    [Display(Name = "Requestee")]
    public Guid? RequesteeId { get; set; }

    [Display(Name = "Start date"), DataType(DataType.Date)]
    public DateOnly? StartDate { get; set; }

    [Display(Name = "Due date"), DataType(DataType.Date)]
    public DateOnly? DueDate { get; set; }

    /// <summary>Makes this a subtask (§6.15): a task on the same project, or a standalone task in the same department.</summary>
    [Display(Name = "Parent task")]
    public Guid? ParentTaskId { get; set; }

    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;

    /// <summary>How the task was resolved (§6.2.4): required for Done, optional otherwise. On the edit form only.</summary>
    [Display(Name = "Solution notes")]
    public string? SolutionNotes { get; set; }

    /// <summary>A sprint, the Backlog or Not Planned (§6.3). A new task starts at <see cref="SprintPlacement.DefaultFor"/>.</summary>
    [Display(Name = "Sprint")]
    public SprintPlacement Sprint { get; set; }

    /// <summary>The asset this task is about (§6.19); blank = none. Chosen with the asset picker.</summary>
    [Display(Name = "Asset")]
    public Guid? AssetId { get; set; }

    public TaskInput ToInput(bool includeStatus) => new()
    {
        Title = Title,
        Description = Description,
        ProjectId = ProjectId,
        DepartmentId = DepartmentId,
        Priority = Priority,
        Type = Type,
        EstimateMinutes = EstimateMinutes,
        AssigneeIds = AssigneeIds,
        RequesteeId = RequesteeId,
        StartDate = StartDate,
        DueDate = DueDate,
        ParentTaskId = ParentTaskId,
        Status = includeStatus ? Status : null,
        SolutionNotes = includeStatus ? SolutionNotes : null,
        SprintId = Sprint.SprintId,
        Backlog = Sprint.IsBacklog,
        AssetId = AssetId
    };

    /// <summary>
    /// "Add subtask" (§6.15): the new subtask starts from the task it is added to - its assignees, priority, due date, estimate and
    /// sprint (not a completed one) - which the parent's roll-up would otherwise overwrite. A task that already has subtasks is a
    /// container with none of its own to copy, so nothing is carried (§13 item 146).
    /// </summary>
    public void PrefillFromParent(TaskItem parent)
    {
        if (parent.HasSubtasks) return;
        AssigneeIds = [.. parent.AssigneeIds];
        Priority = parent.Priority;
        DueDate = parent.DueDate;
        EstimateMinutes = parent.EstimateMinutes;
        if (InheritedSprint(parent) is { } sprint) Sprint = sprint;
    }

    /// <summary>The sprint "Add subtask" carries from a task without subtasks; null for a container or a completed sprint (§6.15, SUB-023).</summary>
    public static SprintPlacement? InheritedSprint(TaskItem parent) =>
        parent.HasSubtasks || parent.Sprint?.Status == SprintStatus.Completed ? null : SprintPlacement.Of(parent);

    /// <summary>A new task's Sprint: what the address asked for, else what Add subtask inherited, else the project's default (§6.3).</summary>
    public static SprintPlacement SprintForNew(SprintPlacement? asked, SprintPlacement? inherited, Guid? projectId) =>
        asked ?? inherited ?? SprintPlacement.DefaultFor(projectId);

    public static TaskForm From(TaskItem t) => new()
    {
        Title = t.Title,
        Description = t.Description,
        ProjectId = t.ProjectId,
        DepartmentId = t.DepartmentId,
        Priority = t.Priority,
        Type = t.Type,
        EstimateMinutes = t.EstimateMinutes,
        AssigneeIds = [.. t.AssigneeIds],
        RequesteeId = t.RequesteeId,
        StartDate = t.StartDate,
        DueDate = t.DueDate,
        ParentTaskId = t.ParentTaskId,
        Status = t.Status,
        SolutionNotes = t.SolutionNotes,
        Sprint = SprintPlacement.Of(t),
        AssetId = t.AssetId
    };
}
