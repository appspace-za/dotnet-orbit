using Orbit.Application.Models;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Application;

/// <summary>
/// Where each of the dashboard's figures leads (spec §6.9, decisions 134 and 135): the Tasks list filtered to exactly what the figure
/// counts, built from the list's own keys (<see cref="TaskListKeys"/>). Every view counts over the same <see cref="Scoping.Tasks"/>
/// the list applies, so only the view's own narrowing is added: <paramref name="Assignee"/> for My work (the tasks the viewer is an
/// assignee of), and <paramref name="DepartmentId"/> for a viewer at All on the Department view. Nobody else gets a department in a
/// link: the list shows its Department filter only to those who see every department, so anyone else couldn't see or clear it once
/// the list remembered it (DSH-008). No link carries a date: the due figures use the list's Due filter, counted from today when the
/// list loads (<see cref="DueWindowRules"/>), so a link followed tomorrow, or remembered by the list, still means what it counted.
/// </summary>
public sealed record DashboardLinks(Guid? Assignee, Guid? DepartmentId)
{
    /// <summary>The Today page's address; its department filter has the Tasks list's key.</summary>
    public const string TodayPath = "/Today";

    /// <summary>
    /// The open tasks nobody is assigned to - never narrowed to the viewer, who would then see none - in <paramref name="departmentId"/>,
    /// which is given only for a viewer who sees every department (the list's Department filter is theirs alone). Anyone else's list
    /// is already their department's, plus any unassigned task they raised elsewhere: a rare difference accepted (decision 134).
    /// </summary>
    public static string UpForGrabs(Guid? departmentId) =>
        new FilterQuery().Add(TaskListKeys.Department, departmentId).Add(TaskListKeys.OpenOnly, true)
            .Add(TaskListKeys.Assignee, FilterChoice.None).At(TaskListKeys.Path);

    public string Status(TaskItemStatus status) => Tasks().Add(TaskListKeys.Status, status.ToString()).At(TaskListKeys.Path);

    /// <summary>Open and due before today, as the Overdue figure counts.</summary>
    public string Overdue => Due(DueWindow.Overdue);

    /// <summary>Open and due from today to <see cref="DueWindowRules.SoonDays"/> days ahead, as the Due soon figure counts.</summary>
    public string DueSoon => Due(DueWindow.Soon);

    public string Priority(TaskPriority priority) => Open().Add(TaskListKeys.Priority, priority.ToString()).At(TaskListKeys.Path);

    public string Type(TaskType type) => Open().Add(TaskListKeys.Type, type.ToString()).At(TaskListKeys.Path);

    /// <summary>Today's plan in one status - never Open only, so the done ones list too (§6.12).</summary>
    public string Planned(TaskItemStatus status) =>
        Tasks().Add(TaskListKeys.PlannedToday, true).Add(TaskListKeys.Status, status.ToString()).At(TaskListKeys.Path);

    /// <summary>The view's open tasks: on My work the Tasks list's My tasks filter (<see cref="Navigation.MyTasks(Guid)"/>).</summary>
    public string AllOpen => Assignee is Guid me && DepartmentId is null ? Navigation.MyTasks(me) : Open().At(TaskListKeys.Path);

    /// <summary>The active sprint's tasks within the view in one status (the list's Sprint filter, §6.3).</summary>
    public string InSprint(Guid sprintId, TaskItemStatus status) =>
        Tasks().Add(TaskListKeys.Sprint, sprintId).Add(TaskListKeys.Status, status.ToString()).At(TaskListKeys.Path);

    /// <summary>
    /// Today's plan as a whole: on My work the viewer's own day (the Tasks list's Today filter, decision 133), otherwise the Today
    /// page, narrowed to the department for a viewer at All.
    /// </summary>
    public string TodayPlan => Assignee is Guid me
        ? Navigation.MyTasksToday(me)
        : new FilterQuery().Add(TaskListKeys.Department, DepartmentId).At(TodayPath);

    private string Due(DueWindow window) => Open().Add(TaskListKeys.Due, window.ToString()).At(TaskListKeys.Path);

    private FilterQuery Open() => Tasks().Add(TaskListKeys.OpenOnly, true);

    /// <summary>The view's own narrowing, first in every address.</summary>
    private FilterQuery Tasks() => new FilterQuery().Add(TaskListKeys.Assignee, Assignee).Add(TaskListKeys.Department, DepartmentId);
}
