using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>
/// The confirmations the task controls show (spec §6.2): the inline controls (Components/Tasks) and the task form
/// (Components/Pages/Tasks/TaskEditor), so both say the same thing.
/// </summary>
public static class TaskMessages
{
    private static string Name(TaskItem task) => $"\"{Ui.Truncate(task.Title, 40)}\"";

    public static string StatusChanged(TaskItem task, TaskItemStatus status) => $"{Name(task)} is now {status.Label()}.";

    public static string Taken(TaskItem task) => $"{Name(task)} is now yours.";

    public static string AssigneeSet(TaskItem task) => task.AssigneeNames is not string names
        ? $"{Name(task)} is now unassigned."
        : $"{Name(task)} assigned to {names}.";

    public static string PlannedToday(TaskItem task, bool planned) => planned
        ? $"{Name(task)} is on today's plan."
        : $"{Name(task)} was taken off the day plan.";

    public static string DueDateSet(TaskItem task) => task.DueDate is null
        ? $"{Name(task)} no longer has a due date."
        : $"{Name(task)} is now due {Ui.Day(task.DueDate)}.";

    public static string SprintSet(TaskItem task) => task.Sprint is null
        ? $"{Name(task)} is back in the backlog."
        : $"{Name(task)} is planned into {task.Sprint.Name}.";

    /// <summary>
    /// A stopped clock (§6.10): what was logged, or that nothing was. A stale clock - its page stopped checking in - says
    /// when its time ended. <paramref name="includeTask"/> names the task, for a clock on another task than the page's.
    /// </summary>
    public static string ClockStopped(Orbit.Application.Models.ClockStopResult stopped, bool includeTask)
    {
        var on = includeTask ? $" on \"{Ui.Truncate(stopped.Task.Title, 60)}\"" : string.Empty;
        var at = stopped.PageLostAt is DateTime lost ? $" at {Ui.When(lost)}, when its page stopped responding," : string.Empty;
        return stopped.Entry is null
            ? $"Stopped the clock{on}{at} after less than a minute; nothing was logged."
            : $"Stopped the clock{on}{at} and logged {Orbit.Application.Models.TimeFormat.Minutes(stopped.Minutes)}.";
    }

    /// <summary>The question asked before setting your own task Done with no time logged on it (§6.10).</summary>
    public const string DoneWithoutTime = "You haven't logged any time on this task. Mark it Done anyway?";
}
