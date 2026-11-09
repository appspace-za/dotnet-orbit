using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>How many subtasks in a branch have one status.</summary>
public sealed record StatusCount(TaskItemStatus Status, int Count);

/// <summary>
/// The summary line under a parent's own values on the Tasks list and the project page (spec §6.15, decision 145). Worked out when
/// the page draws it, never stored: a parent's own status, priority, dates, estimate, sprint and assignees stay its own.
/// </summary>
/// <param name="SubtaskCount">Every subtask counted, at every level: the "(3)" beside the toggle.</param>
/// <param name="Statuses">One entry per status present among them, in workflow order.</param>
/// <param name="Priorities">The open subtasks' priorities, each once, highest first.</param>
/// <param name="Sprints">The sprints open subtasks are in, each once, by start date.</param>
/// <param name="OnBacklog">An open subtask is on the Backlog.</param>
/// <param name="NotPlanned">An open subtask is Not Planned.</param>
/// <param name="DueFrom">The open subtasks' earliest due date; null when none has one.</param>
/// <param name="DueTo">Their latest due date.</param>
/// <param name="Overdue">The earliest due date is before today.</param>
/// <param name="EstimateTotal">The parent's own estimate plus its subtasks' (cancelled ones left out); null when no subtask has one.</param>
/// <param name="OwnEstimate">The parent's own estimate, 0 for none.</param>
/// <param name="SubtasksEstimate">The subtasks' estimates, cancelled ones left out.</param>
/// <param name="Assignees">The people assigned to open subtasks, each once, by name.</param>
/// <param name="HasOpenSubtasks">A counted subtask is open, so the parent can't be closed yet (§6.15).</param>
public sealed record BranchSummary(
    int SubtaskCount, IReadOnlyList<StatusCount> Statuses, IReadOnlyList<TaskPriority> Priorities,
    IReadOnlyList<Sprint> Sprints, bool OnBacklog, bool NotPlanned,
    DateOnly? DueFrom, DateOnly? DueTo, bool Overdue,
    int? EstimateTotal, int OwnEstimate, int SubtasksEstimate,
    IReadOnlyList<ApplicationUser> Assignees, bool HasOpenSubtasks);

/// <summary>
/// A parent's branch and its summary line (spec §6.15, decision 145). Pure, so it is unit tested (TREE-010 to TREE-016).
/// </summary>
public static class BranchSummaryRules
{
    /// <summary>
    /// The tasks below <paramref name="parentId"/>, at every level, that are in <paramref name="countable"/> (what the page may show).
    /// The walk follows <paramref name="idsByParent"/> through every task, so a subtask under one the viewer can't see still counts.
    /// </summary>
    public static IReadOnlyList<TaskItem> BranchOf(Guid parentId, ILookup<Guid?, Guid> idsByParent, IReadOnlyDictionary<Guid, TaskItem> countable)
    {
        var visited = new HashSet<Guid> { parentId };
        var branch = new List<TaskItem>();
        var level = new List<Guid> { parentId };
        for (var depth = 0; depth < TaskTreeRules.MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<Guid>();
            foreach (var id in level.SelectMany(p => idsByParent[p]))
            {
                if (!visited.Add(id)) continue;
                next.Add(id);
                if (countable.TryGetValue(id, out var task)) branch.Add(task);
            }
            level = next;
        }
        return branch;
    }

    /// <summary>The summary of <paramref name="parent"/>'s <paramref name="branch"/>; null when the branch is empty.</summary>
    public static BranchSummary? Summarise(TaskItem parent, IReadOnlyCollection<TaskItem> branch, DateOnly today)
    {
        if (branch.Count == 0) return null;
        var open = branch.Where(t => t.IsOpen).ToList();
        var dues = open.Where(t => t.DueDate is not null).Select(t => t.DueDate!.Value).ToList();
        DateOnly? dueFrom = dues.Count == 0 ? null : dues.Min();
        var working = branch.Where(t => t.Status != TaskItemStatus.Cancelled).ToList();
        var own = parent.EstimateMinutes ?? 0;
        var subtasks = working.Sum(t => t.EstimateMinutes ?? 0);
        var placements = open.Select(SprintPlacement.Of).ToList();
        return new BranchSummary(
            branch.Count,
            branch.GroupBy(t => t.Status).OrderBy(g => g.Key).Select(g => new StatusCount(g.Key, g.Count())).ToList(),
            open.Select(t => t.Priority).Distinct().OrderDescending().ToList(),
            open.Where(t => t.Sprint is not null).Select(t => t.Sprint!).DistinctBy(s => s.Id).OrderBy(s => s.StartDate).ThenBy(s => s.Name).ToList(),
            placements.Any(p => p.IsBacklog),
            placements.Any(p => p.IsNotPlanned),
            dueFrom,
            dues.Count == 0 ? null : dues.Max(),
            dueFrom < today,
            working.Any(t => t.EstimateMinutes is not null) ? own + subtasks : null,
            own,
            subtasks,
            open.SelectMany(t => t.Assignees).DistinctBy(u => u.Id).OrderBy(u => u.DisplayName).ToList(),
            open.Count > 0);
    }
}
