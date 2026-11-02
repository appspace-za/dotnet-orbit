using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>What a parent's roll-up needs to know about one direct subtask; <see cref="SprintStart"/> is its sprint's start date.</summary>
public sealed record SubtaskFacts(TaskItemStatus Status, TaskPriority Priority, DateOnly? StartDate, DateOnly? DueDate,
    int? EstimateMinutes, Guid? SprintId, DateOnly? SprintStart, bool InBacklog);

/// <summary>The values a parent task takes from its subtasks.</summary>
public sealed record RolledUpValues(TaskItemStatus Status, TaskPriority Priority, DateOnly? StartDate, DateOnly? DueDate,
    int? EstimateMinutes, SprintPlacement Sprint);

/// <summary>
/// One task a save writes, as the save hook sees it (design spec 2026-11-02 §3.3): whether it is new, its parent before and
/// after the save, and whether one of <see cref="ParentRollUpRules.RollUpFields"/> changed.
/// </summary>
public sealed record SubtaskChange(bool Added, Guid? OriginalParentId, Guid? ParentId, bool RollUpFieldChanged);

/// <summary>
/// A task with subtasks is a container: its status, priority, dates, estimate and sprint come from its direct subtasks
/// (design spec 2026-11-02 §3.1 and §3.2). Cancelled subtasks are ignored unless every subtask is cancelled.
/// </summary>
public static class ParentRollUpRules
{
    /// <summary>The <see cref="TaskItem"/> properties a parent takes from its subtasks; a change to one on a subtask recalculates its parent.</summary>
    public static readonly IReadOnlyList<string> RollUpFields =
    [
        nameof(TaskItem.Status), nameof(TaskItem.Priority), nameof(TaskItem.StartDate), nameof(TaskItem.DueDate),
        nameof(TaskItem.EstimateMinutes), nameof(TaskItem.SprintId), nameof(TaskItem.InBacklog)
    ];

    /// <summary>
    /// §3.3: the parents a save must recalculate: a new subtask's parent, both the old and the new parent of a moved task, and
    /// the parent of a subtask whose rolled-up field changed.
    /// </summary>
    public static IReadOnlySet<Guid> AffectedParents(IEnumerable<SubtaskChange> changes)
    {
        var parents = new HashSet<Guid>();
        foreach (var change in changes)
        {
            if (change.Added && change.ParentId is Guid added) parents.Add(added);
            if (change.OriginalParentId != change.ParentId)
            {
                if (change.OriginalParentId is Guid from) parents.Add(from);
                if (change.ParentId is Guid to) parents.Add(to);
            }
            if (change.RollUpFieldChanged && change.ParentId is Guid parent) parents.Add(parent);
        }
        return parents;
    }

    /// <summary>SUB-001 to SUB-005, SUB-010, SUB-022: the parent's status. Rolling up groups equals rolling up their leaves.</summary>
    public static TaskItemStatus Status(IEnumerable<TaskItemStatus> subtasks)
    {
        var all = subtasks.ToList();
        if (all.Count == 0) throw new ArgumentException("A parent needs at least one subtask.", nameof(subtasks));

        var counted = all.Where(s => s != TaskItemStatus.Cancelled).ToList();
        if (counted.Count == 0) return TaskItemStatus.Cancelled;
        if (counted.All(s => s == TaskItemStatus.Done)) return TaskItemStatus.Done;
        if (counted.All(s => s == TaskItemStatus.Todo)) return TaskItemStatus.Todo;

        var open = counted.Where(s => s != TaskItemStatus.Done).ToList();
        if (open.All(s => s == TaskItemStatus.Blocked)) return TaskItemStatus.Blocked;
        if (open.All(s => s == TaskItemStatus.Waiting)) return TaskItemStatus.Waiting;
        if (open.All(s => s is TaskItemStatus.Waiting or TaskItemStatus.Blocked)) return TaskItemStatus.Blocked;
        return TaskItemStatus.InProgress;
    }

    /// <summary>SUB-006 to SUB-009: every rolled-up value.</summary>
    public static RolledUpValues Of(IReadOnlyCollection<SubtaskFacts> subtasks)
    {
        if (subtasks.Count == 0) throw new ArgumentException("A parent needs at least one subtask.", nameof(subtasks));

        var status = Status(subtasks.Select(s => s.Status));
        var counted = subtasks.Where(s => s.Status != TaskItemStatus.Cancelled).ToList();
        if (counted.Count == 0) counted = subtasks.ToList();
        var open = counted.Where(s => s.Status != TaskItemStatus.Done).ToList();

        var priority = (open.Count > 0 ? open : counted).Max(s => s.Priority);
        var start = counted.Where(s => s.StartDate is not null).Min(s => s.StartDate);
        var due = counted.Where(s => s.DueDate is not null).Max(s => s.DueDate);
        var estimates = counted.Where(s => s.EstimateMinutes is not null).ToList();
        int? estimate = estimates.Count == 0 ? null : estimates.Sum(s => s.EstimateMinutes!.Value);

        return new RolledUpValues(status, priority, start, due, estimate, Sprint(open.Count > 0 ? open : counted));
    }

    // The earliest sprint by start date (the id breaks a tie), else the Backlog if any is on it, else Not Planned.
    private static SprintPlacement Sprint(List<SubtaskFacts> decide)
    {
        var inSprint = decide.Where(s => s.SprintId is not null)
            .OrderBy(s => s.SprintStart ?? DateOnly.MaxValue).ThenBy(s => s.SprintId)
            .FirstOrDefault();
        if (inSprint is not null) return SprintPlacement.In(inSprint.SprintId!.Value);
        return decide.Any(s => s.InBacklog) ? SprintPlacement.Backlog : SprintPlacement.NotPlanned;
    }
}
