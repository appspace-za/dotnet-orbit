using Orbit.Application.Models;
using Orbit.Application.Scheduling;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of the Planning page (spec §6.3, decision 140), used at sprint planning meetings:
/// <list type="bullet">
/// <item><b>Which tasks are shown</b> (<see cref="Shown"/>): every open task on a project, wherever it is placed. Standalone tasks
/// are shown only when they are on the Backlog or in a sprint, so support work (standalone and Not Planned) stays out.</item>
/// <item><b>The sprint being planned</b> (<see cref="DefaultTarget"/>), and what each row's one button does against it
/// (<see cref="ActionFor"/>).</item>
/// <item><b>Each person's capacity</b> (<see cref="WorkingDays"/>): the working days left in the sprint, from the working
/// calendar, times a working day.</item>
/// <item><b>The panels and totals</b> (<see cref="Build"/>).</item>
/// </list>
/// No database access, so all of it is unit-tested (PLN-001 to PLN-008).
/// </summary>
public static class SprintPlanningRules
{
    /// <summary>
    /// PLN-002: the tasks the page lists. They are open, and either on a project (any placement) or on the Backlog or in a sprint
    /// (any project or none). Written as a query filter so SQL and the tests use the same definition.
    /// </summary>
    public static IQueryable<TaskItem> Shown(IQueryable<TaskItem> q) =>
        q.Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled
                     && (t.ProjectId != null || t.SprintId != null || t.InBacklog));

    /// <summary>
    /// PLN-001: the sprint being planned. The requested one if it is still open; otherwise the planned sprint that starts first
    /// (the next sprint); otherwise the active one; otherwise none.
    /// </summary>
    public static Sprint? DefaultTarget(IReadOnlyList<Sprint> sprints, Guid? requested)
    {
        var open = sprints.Where(s => s.Status != SprintStatus.Completed).ToList();
        if (requested is Guid id && open.FirstOrDefault(s => s.Id == id) is { } asked) return asked;
        return open.Where(s => s.Status == SprintStatus.Planned)
                   .OrderBy(s => s.StartDate)
                   .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                   .FirstOrDefault()
               ?? open.FirstOrDefault(s => s.Status == SprintStatus.Active);
    }

    /// <summary>
    /// PLN-003: a row's button. A task in the sprint being planned can be removed; a task in another sprint (a completed one
    /// included, when a closed task was reopened) can be moved here; a task on the Backlog or Not Planned can be planned.
    /// </summary>
    public static PlanningAction ActionFor(TaskItem task, Guid targetId) =>
        task.SprintId == targetId ? PlanningAction.Remove
        : task.SprintId is not null ? PlanningAction.MoveHere
        : PlanningAction.Plan;

    /// <summary>PLN-003: where the button puts the task. Removing returns it to the Backlog, as moving out of a sprint does elsewhere.</summary>
    public static SprintPlacement PlacementFor(PlanningAction action, Guid targetId) =>
        action == PlanningAction.Remove ? SprintPlacement.Backlog : SprintPlacement.In(targetId);

    /// <summary>
    /// PLN-004: the sprint's working days still to come, both ends included: from the later of today and its start to its end. A
    /// planned sprint counts its whole window, an active one only what is left of it, and one past its end none. Holidays and
    /// other exceptions come from the working calendar.
    /// </summary>
    public static int WorkingDays(WorkDayCalendar calendar, Sprint sprint, DateOnly today)
    {
        var from = today > sprint.StartDate ? today : sprint.StartDate;
        return calendar.CountWorkingDays(from, sprint.EndDate);
    }

    /// <summary>What is left of a task's estimate after all the time logged on it; nothing for a task with no estimate.</summary>
    public static int Remaining(TaskItem task, int loggedMinutes) =>
        task.EstimateMinutes is int estimate ? SprintReportRules.Remaining(estimate, loggedMinutes) : 0;

    /// <summary>PLN-006: the size of a set of tasks. A task with no estimate counts as a task, and in Unestimated, but adds no time.</summary>
    public static PlanningLoad Load(IEnumerable<TaskItem> tasks, IReadOnlyDictionary<Guid, int> logged)
    {
        var list = tasks as IReadOnlyCollection<TaskItem> ?? tasks.ToList();
        return new PlanningLoad(
            list.Count,
            list.Count(t => t.EstimateMinutes is null),
            list.Sum(t => t.EstimateMinutes ?? 0),
            list.Sum(t => Remaining(t, logged.GetValueOrDefault(t.Id))));
    }

    /// <summary>
    /// The page. <paramref name="tasks"/> are the shown tasks (<see cref="Shown"/>) after the viewer's scope and the filters, with
    /// their project, project department and sprint loaded. <paramref name="sprintTasks"/> are the shown tasks in the sprint being
    /// planned after the viewer's scope only, with their assignees: what each person's load is made of. <paramref name="logged"/> is
    /// all the time logged on each of either so far.
    /// <list type="bullet">
    /// <item>PLN-005: one group per project, ordered by department, then name, then number; the standalone tasks come last. Inside
    /// a group the tasks are ordered by priority (highest first), then due date (none last), then number. The placement isn't part
    /// of the order, so a row stays where it is when it is planned.</item>
    /// <item>PLN-006: each group's counts, and the totals for the sprint being planned and the Backlog.</item>
    /// <item>PLN-007: each person's share of the sprint being planned. A shared task counts in full for each of its assignees, as
    /// on the Today page (§6.2.3). A person is overloaded when what is left of their estimates is more than one person's capacity;
    /// the Unassigned row never is. Ordered by what is left, largest first, then name; Unassigned last.</item>
    /// <item>PLN-008: a person's share is all of their work in the sprint the viewer may see, from <paramref name="sprintTasks"/>,
    /// whatever the filters: capacity is a person's, so a filter on someone else, a search or a department mustn't hide the rest of
    /// their load. The totals and the panels follow the filters (decision 141).</item>
    /// </list>
    /// </summary>
    public static SprintPlanning Build(
        IReadOnlyList<Sprint> openSprints, Sprint? target, IEnumerable<TaskItem> tasks, IEnumerable<TaskItem> sprintTasks,
        IReadOnlyDictionary<Guid, int> logged, DateOnly today, int workingDays, int workingDayMinutes)
    {
        var all = tasks.ToList();
        bool InTarget(TaskItem t) => target is not null && t.SprintId == target.Id;
        static bool OnBacklog(TaskItem t) => t.SprintId is null && t.InBacklog;

        PlanningGroup Group(Project? project, IEnumerable<TaskItem> members)
        {
            var ordered = members
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.DueDate is null)
                .ThenBy(t => t.DueDate)
                .ThenBy(t => t.Number, StringComparer.Ordinal)
                .ToList();
            var counts = new PlanningCounts(
                ordered.Count,
                Load(ordered.Where(InTarget), logged),
                Load(ordered.Where(OnBacklog), logged),
                ordered.Count(t => t.SprintId is not null && !InTarget(t)),
                ordered.Count(t => t.SprintId is null && !t.InBacklog));
            return new PlanningGroup(project, ordered, counts);
        }

        var groups = all.Where(t => t.ProjectId is not null)
            .GroupBy(t => t.ProjectId!.Value)
            .Select(g => Group(g.Select(t => t.Project).FirstOrDefault(p => p is not null), g))
            .OrderBy(g => g.Project?.Department?.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Project?.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Project?.Number ?? "", StringComparer.Ordinal)
            .ToList();
        var standalone = all.Where(t => t.ProjectId is null).ToList();
        if (standalone.Count > 0) groups.Add(Group(null, standalone));

        var inTarget = all.Where(InTarget).ToList();
        var wholeSprint = sprintTasks.Where(InTarget).ToList();
        var capacity = workingDays * workingDayMinutes;
        var names = wholeSprint.SelectMany(t => t.Assignments).GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.First().User?.DisplayName ?? "Unknown");
        var people = wholeSprint
            .SelectMany(t => TimeReportRules.AssigneesOrNone(t.AssigneeIds).Select(id => (UserId: id, Task: t)))
            .GroupBy(x => x.UserId, x => x.Task)
            .Select(g =>
            {
                var load = Load(g, logged);
                return new PlanningPerson(g.Key, g.Key is Guid id ? names.GetValueOrDefault(id, "Unknown") : "Unassigned", load,
                    g.Key is not null && load.RemainingMinutes > capacity);
            })
            .OrderBy(p => p.UserId is null)
            .ThenByDescending(p => p.Load.RemainingMinutes)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new SprintPlanning(openSprints, target, today, workingDays, workingDayMinutes,
            Load(inTarget, logged), Load(all.Where(OnBacklog), logged), people, groups, logged);
    }
}
