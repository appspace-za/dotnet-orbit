using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// SUB-020: what a person (or Claude) may not do to a task with subtasks, whose status, priority, dates, estimate, sprint and
/// assignees come from them (design spec 2026-11-02 §5; orbit-spec §6.15).
/// </summary>
public static class ParentRules
{
    public const string NoTimeMessage = "This task has subtasks; log time on one of them.";

    public static string DerivedMessage(TaskItem task, string field) => $"{task.Number} has subtasks; its {field} comes from them.";

    /// <summary>Refuses a change to a derived field of a parent; a plain task is never refused.</summary>
    public static void RequireNotParent(TaskItem task, string field)
    {
        if (task.HasSubtasks) throw new ValidationException(DerivedMessage(task, field));
    }

    /// <summary>
    /// The first derived field (status, priority, start date, due date, estimate, sprint, assignees) a save would change on a
    /// parent, or null when it changes none - or the task isn't a parent. A value sent back unchanged is not a change.
    /// </summary>
    public static string? DerivedChange(TaskItem task, TaskItemStatus status, TaskPriority priority, DateOnly? startDate,
        DateOnly? dueDate, int? estimateMinutes, SprintPlacement sprint, bool assigneesChange)
    {
        if (!task.HasSubtasks) return null;
        if (status != task.Status) return "status";
        if (priority != task.Priority) return "priority";
        if (startDate != task.StartDate) return "start date";
        if (dueDate != task.DueDate) return "due date";
        if (estimateMinutes != task.EstimateMinutes) return "estimate";
        if (sprint != SprintPlacement.Of(task)) return "sprint";
        return assigneesChange ? "assignees" : null;
    }
}
