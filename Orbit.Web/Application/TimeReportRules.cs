using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of the time reports (§12) shared by the others: the estimate comparison every report makes, and Estimate
/// accuracy. Time spent has rules of its own (<see cref="TimeSpentRules"/>).
/// <list type="bullet">
/// <item>Estimates are compared over tasks that have one and aren't Cancelled, against all time logged on them to date.</item>
/// <item>Every task stands alone: subtask time isn't rolled into the parent (§6.10).</item>
/// <item>A task several people are assigned to (§6.2.3) is under each of them, whole - an estimate is never split between them -
/// and totals count it once.</item>
/// </list>
/// </summary>
public static class TimeReportRules
{
    /// <summary>Whom a task is listed under in a report grouped by assignee: each of its assignees, or null - the Unassigned row - when it has none.</summary>
    public static IEnumerable<Guid?> AssigneesOrNone(IReadOnlyList<Guid> assigneeIds) =>
        assigneeIds.Count == 0 ? [null] : assigneeIds.Distinct().Select(id => (Guid?)id);

    /// <summary>A task's assignees as a report's task list names them: by name, comma-separated, or the name given for nobody.</summary>
    public static string AssigneeNames(IReadOnlyList<Guid> assigneeIds, Func<Guid?, string> name) =>
        string.Join(", ", AssigneesOrNone(assigneeIds).Select(name).Order(StringComparer.CurrentCultureIgnoreCase));

    /// <summary>Estimated against actual over <paramref name="tasks"/>; each task should appear once.</summary>
    public static EstimateComparison Compare(IEnumerable<TaskTimeFacts> tasks)
    {
        var estimated = tasks.Where(t => t.EstimateMinutes is not null && t.Status != TaskItemStatus.Cancelled).ToList();
        return new EstimateComparison(
            estimated.Count,
            estimated.Sum(t => t.EstimateMinutes!.Value),
            estimated.Sum(t => t.TotalMinutes),
            estimated.Count(t => t.TotalMinutes > t.EstimateMinutes!.Value));
    }

    /// <summary>
    /// Estimate accuracy: completed tasks grouped by assignee (no assignee = "Unassigned"), ordered by count then
    /// name; each row's tasks with the largest overrun first and unestimated ones last. A task with several assignees is
    /// under each of them, and counted once in the report's own totals.
    /// </summary>
    public static EstimateAccuracyReport EstimateAccuracy(IEnumerable<TaskTimeFacts> doneTasks, Func<Guid?, string> name)
    {
        var tasks = doneTasks.DistinctBy(t => t.Id).ToList();
        var rows = tasks.SelectMany(t => AssigneesOrNone(t.AssigneeIds).Select(id => (UserId: id, Task: t)))
            .GroupBy(x => x.UserId, x => x.Task)
            .Select(g => new AssigneeAccuracyRow(g.Key, name(g.Key), g.Count(), Compare(g),
                g.Select(t => new TaskEstimateRow(t.Id, t.Number, t.Title, t.EstimateMinutes, t.TotalMinutes))
                    .OrderBy(r => r.VarianceMinutes is null)
                    .ThenByDescending(r => r.VarianceMinutes)
                    .ThenBy(r => r.Number, StringComparer.Ordinal)
                    .ToList()))
            .OrderByDescending(r => r.DoneCount).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new EstimateAccuracyReport(rows, tasks.Count, Compare(tasks));
    }
}
