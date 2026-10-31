using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Builds one view of the dashboard (spec §6.9, decision 134): My work, a department's or the company's. Each view counts over
/// its own scope - the tasks the viewer is an assignee of, a department's (<see cref="Scoping.Tasks"/>, narrowed to the picked
/// department for a viewer at All) or everything - and the same aggregate queries fill every view, so the figures are the same
/// kind everywhere and each matches the Tasks list its link opens (<see cref="DashboardLinks"/>). Which views a viewer reaches
/// is <see cref="DashboardRules.Views"/>; the page picks one of those, and this refuses any other.
/// </summary>
public sealed class DashboardService(ApplicationDbContext db, IActorProvider actors)
{
    /// <summary>
    /// The dashboard in <paramref name="requested"/> view, or the viewer's default view when null. A viewer who reaches no view
    /// (no tasks.view) gets the page's frame only, with no figures; asking for a view the viewer doesn't reach is refused.
    /// </summary>
    public async Task<DashboardModel> BuildAsync(DashboardView? requested, Guid? departmentId = null, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var views = DashboardRules.Views(actor);
        var canEdit = actor.Has(Permission.TasksEdit);
        var seesAll = actor.CanAnywhere(Permission.TasksView);
        var me = actor.UserId;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var homeName = actor.DepartmentId is Guid home
            ? await db.Departments.AsNoTracking().Where(d => d.Id == home).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;

        if (requested is null && DashboardRules.DefaultView(actor) is null)
            return new DashboardModel
            {
                Actor = actor, View = null, Views = views, Today = today, Links = new DashboardLinks(null, null),
                ScopeLabel = "Your role can't see any tasks", HomeDepartmentName = homeName
            };
        var view = requested ?? DashboardRules.DefaultView(actor)!.Value;
        if (!views.Contains(view))
            throw new ForbiddenException(view switch
            {
                DashboardView.Company => "Your role can't see the whole company's tasks.",
                DashboardView.Department => "Your role can't see a department's tasks.",
                _ => "Your role has no tasks of its own to show."
            });

        // The Department view's department: the viewer's own, or for someone who sees every department the one they picked (else
        // their own, else the first by name). Unlike the Today page's filter it has no "All": the view is always one department's.
        List<DepartmentOption> departments = [];
        Guid? viewDept = null;
        string? viewDeptName = null;
        if (view == DashboardView.Department)
        {
            if (seesAll)
            {
                departments = await db.Departments.AsNoTracking().Where(d => !d.IsArchived).OrderBy(d => d.Name)
                    .Select(d => new DepartmentOption(d.Id, d.Name)).ToListAsync(ct);
                var picked = departments.FirstOrDefault(d => d.Id == departmentId)
                    ?? departments.FirstOrDefault(d => d.Id == actor.DepartmentId)
                    ?? departments.FirstOrDefault();
                viewDept = picked?.Id;
                viewDeptName = picked?.Name;
            }
            else
            {
                viewDept = actor.DepartmentId;
                viewDeptName = homeName;
            }
        }

        // My work is the tasks the viewer is an assignee of (§6.2.3); a view-only role at Own has none of its own to do, so it sees what
        // Own reaches - the tasks it raised, requested or is assigned. The Department view of someone at All is the picked department;
        // anyone else's is their department and their own tasks elsewhere (decision 97), as every department list is.
        var myWork = view == DashboardView.Mine && canEdit;
        var pickedDept = view == DashboardView.Department && seesAll;
        IQueryable<TaskItem> InView(IQueryable<TaskItem> q) =>
            myWork ? q.Where(t => t.Assignments.Any(x => x.UserId == me))
            : pickedDept ? Scoping.Tasks(q, actor).Where(t => t.DepartmentId == viewDept)
            : Scoping.Tasks(q, actor);

        var scopeLabel = view switch
        {
            DashboardView.Company => "Open tasks across the company",
            DashboardView.Department when pickedDept => $"Open tasks in {viewDeptName ?? "the department"}",
            DashboardView.Department => "Open tasks in your department, and yours elsewhere",
            _ => myWork ? "Your open tasks" : "Open tasks you raised, requested or are assigned"
        };

        var all = InView(db.Tasks.AsNoTracking());
        var open = all.Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled);

        // The tiles and both donuts, from one grouping.
        var facetRows = await open.GroupBy(t => new { t.Status, t.Priority, t.Type })
            .Select(g => new { g.Key.Status, g.Key.Priority, g.Key.Type, Count = g.Count() })
            .ToListAsync(ct);
        var facets = facetRows.Select(f => new OpenFacet(f.Status, f.Priority, f.Type, f.Count)).ToList();

        // The due figures over the Tasks list's own Due windows, so each matches the list its link opens.
        var overdue = await DueWindowRules.Within(open, DueWindow.Overdue, today).CountAsync(ct);
        var dueSoon = await DueWindowRules.Within(open, DueWindow.Soon, today).CountAsync(ct);

        // Work nobody has picked up yet in the viewer's department, which they may take (§6.5), of what the Tasks list shows them.
        // Its link names the department only for someone who sees every department, whose list would otherwise be the company's.
        UpForGrabsLine? upForGrabs = null;
        if (myWork && actor.DepartmentId is Guid ownDept && actor.CanInDepartment(Permission.TasksTake, ownDept))
        {
            var grabs = await Scoping.Tasks(db.Tasks.AsNoTracking(), actor).CountAsync(t => t.DepartmentId == ownDept
                && t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled && !t.Assignments.Any(), ct);
            upForGrabs = new UpForGrabsLine(grabs, DashboardLinks.UpForGrabs(seesAll ? ownDept : null));
        }

        // Today's day plan (§6.12): over all the view's tasks, not the open ones, so what was closed today counts.
        StatusCounts? todayPlan = canEdit ? await StatusesAsync(all.Where(t => t.PlannedFor == today), ct) : null;

        IReadOnlyList<LoadBar> byAssignee = [];
        if (view == DashboardView.Department)
        {
            // A task several people share counts under each of them (§6.2.3), so the bars can add up to more than the open tasks.
            var rows = await open.SelectMany(t => t.Assignments, (t, x) => new { x.UserId, x.User.DisplayName, t.Status })
                .GroupBy(r => new { r.UserId, r.DisplayName, r.Status })
                .Select(g => new { g.Key.UserId, g.Key.DisplayName, g.Key.Status, Count = g.Count() })
                .ToListAsync(ct);
            var unassigned = await StatusesAsync(open.Where(t => !t.Assignments.Any()), ct);
            byAssignee = DashboardRules.People(rows.Select(r => new PersonStatusCount(r.UserId, r.DisplayName, r.Status, r.Count)), unassigned);
        }

        IReadOnlyList<LoadBar> byDepartment = [];
        if (view == DashboardView.Company)
        {
            var rows = await open.GroupBy(t => new { t.DepartmentId, t.Department.Name, t.Status })
                .Select(g => new { g.Key.DepartmentId, g.Key.Name, g.Key.Status, Count = g.Count() })
                .ToListAsync(ct);
            byDepartment = DashboardRules.Departments(rows.Select(r => new DepartmentStatusCount(r.DepartmentId, r.Name, r.Status, r.Count)));
        }

        // The Last 14 days chart, bucketed by UTC day in memory rather than relying on a date translation in SQL. CompletedAt is set
        // only when a task is done (TaskService.ApplyStatus), so the done series is tasks done.
        var from = DashboardRules.TrendStartUtc(today);
        var created = await all.Where(t => t.CreatedAt >= from).Select(t => t.CreatedAt).ToListAsync(ct);
        var done = await all.Where(t => t.Status == TaskItemStatus.Done && t.CompletedAt != null && t.CompletedAt >= from)
            .Select(t => t.CompletedAt!.Value).ToListAsync(ct);

        // The active sprint: shared company-wide (§6.3), so its progress is every department's; the view's part beside it.
        var sprint = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Status == SprintStatus.Active, ct);
        var sprintWhole = StatusCounts.None;
        var sprintInScope = StatusCounts.None;
        SprintDay? sprintDay = null;
        IReadOnlyList<DepartmentSprintProgress> sprintByDept = [];
        if (sprint is not null)
        {
            var inSprint = db.Tasks.AsNoTracking().Where(t => t.SprintId == sprint.Id);
            sprintDay = DashboardRules.SprintDayOf(sprint.StartDate, sprint.EndDate, today);
            if (view == DashboardView.Company)
            {
                // One grouping for the Company view: the whole sprint, which is also the view's part, and each department's.
                var rows = (await inSprint.GroupBy(t => new { t.DepartmentId, t.Department.Name, t.Status })
                        .Select(g => new { g.Key.DepartmentId, g.Key.Name, g.Key.Status, Count = g.Count() })
                        .ToListAsync(ct))
                    .Select(r => new DepartmentStatusCount(r.DepartmentId, r.Name, r.Status, r.Count)).ToList();
                sprintWhole = sprintInScope = StatusCounts.From(rows.Select(r => (r.Status, r.Count)));
                sprintByDept = DashboardRules.SprintDepartments(rows);
            }
            else
            {
                sprintWhole = await StatusesAsync(inSprint, ct);
                sprintInScope = await StatusesAsync(InView(inSprint), ct);
            }
        }

        // Projects at a glance: the projects the viewer may see - on My work only those with their tasks, on a picked department
        // only that department's.
        var projects = Scoping.Projects(db.Projects.AsNoTracking().Include(p => p.Department).Include(p => p.Owner)
            .Where(p => p.Status == ProjectStatus.Active), actor);
        if (myWork) projects = projects.Where(p => p.Tasks.Any(t => t.Assignments.Any(x => x.UserId == me)));
        if (pickedDept) projects = projects.Where(p => p.DepartmentId == viewDept);
        var projectRows = await projects.OrderBy(p => p.Name).Take(12).Select(p => new
        {
            Project = p,
            Total = p.Tasks.Count(),
            Open = p.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled),
            Done = p.Tasks.Count(t => t.Status == TaskItemStatus.Done)
        }).ToListAsync(ct);

        return new DashboardModel
        {
            Actor = actor,
            View = view,
            Views = views,
            Today = today,
            Links = new DashboardLinks(myWork ? me : null, pickedDept ? viewDept : null),
            ScopeLabel = scopeLabel,
            HomeDepartmentName = homeName,
            DepartmentId = viewDept,
            Departments = departments,
            Open = DashboardRules.Statuses(facets),
            OverdueCount = overdue,
            DueSoonCount = dueSoon,
            ByPriority = DashboardRules.Priorities(facets),
            ByType = DashboardRules.Types(facets),
            UpForGrabs = upForGrabs,
            TodayPlan = todayPlan,
            OpenByAssignee = byAssignee,
            OpenByDepartment = byDepartment,
            Trend = DashboardRules.Daily(created, done, today),
            ActiveSprint = sprint,
            Sprint = sprintWhole,
            SprintInScope = sprintInScope,
            SprintDay = sprintDay,
            SprintByDepartment = sprintByDept,
            Projects = projectRows.Select(r => new ProjectListItem(r.Project, r.Total, r.Open, r.Done)).ToList()
        };
    }

    private static async Task<StatusCounts> StatusesAsync(IQueryable<TaskItem> q, CancellationToken ct)
    {
        var rows = await q.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return StatusCounts.From(rows.Select(r => (r.Key, r.Count)));
    }
}
