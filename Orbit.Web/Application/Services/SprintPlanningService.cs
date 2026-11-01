using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// The Planning page's reads (spec §6.3, decisions 140 and 141): the sprint being planned, the open tasks by project with their
/// placement, the sprint's own tasks for each person's load, the time logged on them, and each person's capacity in the sprint. It
/// only reads. Planning a task goes through <see cref="TaskService.MoveToSprintAsync"/>, which checks <c>tasks.plan</c> and audits
/// the move.
/// </summary>
public sealed class SprintPlanningService(
    ApplicationDbContext db,
    IActorProvider actors,
    SprintService sprints,
    WorkingCalendarService calendar,
    IOptions<CriticalPathOptions> options)
{
    public async Task<SprintPlanning> GetAsync(SprintPlanningFilter f, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var open = await sprints.ListOpenAsync(ct);
        var target = SprintPlanningRules.DefaultTarget(open, f.SprintId);

        // The tasks the viewer may see (tasks.view, §6.5), as on the Backlog, narrowed by the page's filters.
        var scoped = SprintPlanningRules.Shown(Scoping.Tasks(db.Tasks.AsNoTracking(), actor));
        var q = scoped;
        if (f.DepartmentId is Guid departmentId) q = q.Where(t => t.DepartmentId == departmentId);
        if (f.Unassigned) q = q.Where(t => !t.Assignments.Any());
        else if (f.AssigneeId is Guid assigneeId) q = q.Where(t => t.Assignments.Any(x => x.UserId == assigneeId));
        q = TaskService.Matching(q, f.Search);

        // Not paged: only open tasks are read, and the page needs them all for its totals.
        var tasks = await q
            .Include(t => t.Department)
            .Include(t => t.Project).ThenInclude(p => p!.Department)
            .Include(t => t.Sprint)
            .Include(t => t.ParentTask)
            .Include(t => t.Asset)
            .ToListAsync(ct);

        // Each person's load is all their work in the sprint the viewer may see, whatever the filters (PLN-008). Unfiltered, that is
        // among the tasks just read; the assignees come with every task (auto-included).
        List<TaskItem> sprintTasks = target is not { } sprint ? []
            : f.Narrows ? await scoped.Where(t => t.SprintId == sprint.Id).ToListAsync(ct)
            : tasks.Where(t => t.SprintId == sprint.Id).ToList();

        // All the time logged on those tasks so far, by anyone, for what is left of each estimate.
        var ids = tasks.Select(t => t.Id).Union(sprintTasks.Select(t => t.Id)).ToList();
        var logged = await db.TimeEntries.AsNoTracking()
            .Where(e => ids.Contains(e.TaskId))
            .GroupBy(e => e.TaskId)
            .Select(g => new { TaskId = g.Key, Minutes = g.Sum(e => e.DurationMinutes) })
            .ToDictionaryAsync(x => x.TaskId, x => x.Minutes, ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var workingDays = target is null ? 0 : SprintPlanningRules.WorkingDays(await calendar.BuildAsync(ct), target, today);
        var workingDayMinutes = Math.Max(1, options.Value.HoursPerWorkingDay) * 60;

        return SprintPlanningRules.Build(open, target, tasks, sprintTasks, logged, today, workingDays, workingDayMinutes);
    }
}
