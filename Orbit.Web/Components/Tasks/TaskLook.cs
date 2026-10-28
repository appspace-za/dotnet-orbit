using MudBlazor;
using Orbit.Data.Entities;

namespace Orbit.Components.Tasks;

/// <summary>
/// How a task's status, priority, type and source look as MudBlazor chips: the Blazor twin of the badge classes in
/// <see cref="Orbit.Helpers.Ui"/>, keeping their meaning (red for Blocked and Critical, green for Done, and so on).
/// </summary>
public static class TaskLook
{
    /// <summary>
    /// Whether a task list needs its Department column: always for a viewer who sees every department, otherwise when a row is
    /// filed outside the viewer's department - their own tasks elsewhere, which a department's lists include (§6.5).
    /// </summary>
    public static bool MixesDepartments(Orbit.Application.Actor actor, IEnumerable<TaskItem> tasks) =>
        actor.CanAnywhere(Orbit.Application.Permission.TasksView) || tasks.Any(t => t.DepartmentId != actor.DepartmentId);

    public static Color Status(TaskItemStatus s) => s switch
    {
        TaskItemStatus.InProgress => Color.Primary,
        TaskItemStatus.Waiting => Color.Info,
        TaskItemStatus.Blocked => Color.Error,
        TaskItemStatus.Done => Color.Success,
        TaskItemStatus.Cancelled => Color.Dark,
        _ => Color.Default
    };

    public static Color Priority(TaskPriority p) => p switch
    {
        TaskPriority.Medium => Color.Info,
        TaskPriority.High => Color.Warning,
        TaskPriority.Critical => Color.Error,
        _ => Color.Default
    };

    /// <summary>A task's type (§6.2), shown when it isn't a plain Task. Change has Orbit's own orange (app.css).</summary>
    public static Color Type(TaskType t) => t switch
    {
        TaskType.Meeting => Color.Primary,
        TaskType.Planning => Color.Info,
        TaskType.Training => Color.Success,
        TaskType.Audit => Color.Warning,
        TaskType.Request => Color.Secondary,
        TaskType.Problem => Color.Error,
        _ => Color.Default
    };

    public static string? TypeClass(TaskType t) => t == TaskType.Change ? "orbit-chip-change" : null;

    /// <summary>Where a task came from, when not typed in by a person. Claude's writes have their own purple (app.css).</summary>
    public static Color Source(TaskSource s) => s switch
    {
        TaskSource.Request => Color.Info,
        TaskSource.Nagios => Color.Error,
        _ => Color.Default
    };

    public static string? SourceClass(TaskSource s) => s == TaskSource.Api ? "orbit-chip-claude" : null;
}
