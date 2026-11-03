using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>A descendant of a parent, with what its row summarises. <see cref="ParentId"/> is the direct parent.</summary>
public sealed record DescendantFacts(Guid Id, Guid ParentId, bool HasSubtasks, TaskItemStatus Status, DateOnly? DueDate,
    Guid? SprintId, string? SprintName, DateOnly? SprintStart, IReadOnlyList<string> AssigneeNames);

public sealed record StatusCount(TaskItemStatus Status, int Count);

/// <summary>What a parent's row shows in place of values of its own (design spec 2026-11-02 §4).</summary>
public sealed record ParentSummary(int SubtaskCount, IReadOnlyList<StatusCount> Statuses, DateOnly? NextDue, bool Overdue,
    string? FirstSprint, string? LastSprint, int Unplanned, IReadOnlyList<string> Assignees);

/// <summary>Display-only values for a parent row, worked out from its bottom-level descendants (orbit-spec §6.15). Pure.</summary>
public static class ParentDisplayRules
{
    public static IReadOnlyDictionary<Guid, ParentSummary> Summarise(IEnumerable<Guid> parentIds,
        IReadOnlyCollection<DescendantFacts> descendants, DateOnly today)
    {
        var byParent = descendants.ToLookup(d => d.ParentId);
        var result = new Dictionary<Guid, ParentSummary>();
        foreach (var parent in parentIds.Distinct())
        {
            var leaves = new List<DescendantFacts>();
            var seen = new HashSet<Guid> { parent };
            var stack = new Stack<Guid>();
            stack.Push(parent);
            while (stack.Count > 0)
                foreach (var d in byParent[stack.Pop()])
                {
                    if (!seen.Add(d.Id)) continue;
                    if (d.HasSubtasks) stack.Push(d.Id); else leaves.Add(d);
                }

            var open = leaves.Where(l => !l.Status.IsClosed()).ToList();
            var statuses = leaves.GroupBy(l => l.Status).OrderBy(g => g.Key).Select(g => new StatusCount(g.Key, g.Count())).ToList();
            var dues = open.Where(l => l.DueDate.HasValue).Select(l => l.DueDate!.Value).ToList();
            var sprints = open.Where(l => l.SprintId.HasValue)
                .OrderBy(l => l.SprintStart is null).ThenBy(l => l.SprintStart).ToList();
            result[parent] = new ParentSummary(
                byParent[parent].Count(), statuses,
                dues.Count > 0 ? dues.Min() : null, dues.Any(d => d < today),
                sprints.FirstOrDefault()?.SprintName, sprints.LastOrDefault()?.SprintName,
                open.Count(l => l.SprintId is null),
                open.SelectMany(l => l.AssigneeNames).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList());
        }
        return result;
    }

    /// <summary>The next and last due dates as a range; From is null when there is only one date to show.</summary>
    public static (DateOnly? From, DateOnly? To) DueRange(DateOnly? nextDue, DateOnly? lastDue) =>
        nextDue is null || nextDue == lastDue ? (null, lastDue) : (nextDue, lastDue);
}
