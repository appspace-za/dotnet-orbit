using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>Query filters over tasks that more than one service shares.</summary>
public static class TaskQueries
{
    /// <summary>
    /// §6.15, SUB-019: the tasks that are work to do. A task with subtasks is only the container its subtasks roll up into, so a
    /// count or an estimate sum that included it would count the same work twice. Time sums do not use this: a time entry counts
    /// once, on the task it was logged on. Where a nested projection (<c>p.Tasks.Count(...)</c>) can't call this, it writes
    /// <c>!t.HasSubtasks</c> inline under a <c>// WorkOnly (§6.15)</c> comment.
    /// </summary>
    public static IQueryable<TaskItem> WorkOnly(this IQueryable<TaskItem> q) => q.Where(t => !t.HasSubtasks);
}
