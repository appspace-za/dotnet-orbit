using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>Operational reports (§12). Aggregates are computed in SQL and returned as plain DTOs.</summary>
public sealed class ReportingService(ApplicationDbContext db, IActorProvider actors, CriticalPathService criticalPath)
{
    private const string ClaudeName = "Claude";

    /// <summary>
    /// Tasks set to Done in the period, grouped by assignee. A task several people share (§6.2.3) counts under each of them, and
    /// once in the total.
    /// </summary>
    public async Task<PersonCountReport> ClosedByPersonAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var done = Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc);
        var rows = await done.SelectMany(t => t.Assignments)
            .GroupBy(x => x.UserId)
            .Select(g => new { UserId = (Guid?)g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var unassigned = await done.CountAsync(t => !t.Assignments.Any(), ct);
        if (unassigned > 0) rows.Add(new { UserId = (Guid?)null, Count = unassigned });
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return new PersonCountReport(
            rows.Select(r => new PersonCountRow(r.UserId, Name(r.UserId, names, "Unassigned"), r.Count))
                .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToList(),
            await done.CountAsync(ct));
    }

    /// <summary>Tasks created in the period, grouped by author. API-created tasks roll up under Claude.</summary>
    public async Task<PersonCountReport> CreatedByPersonAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var rows = await Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.CreatedAt >= f.FromUtc && t.CreatedAt < f.ToUtc)
            .GroupBy(t => t.CreatedById)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return new PersonCountReport(
            rows.Select(r => new PersonCountRow(r.UserId, Name(r.UserId, names, "System"), r.Count))
                .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToList(),
            rows.Sum(r => r.Count));
    }

    /// <summary>
    /// Average FirstRespondedAt - CreatedAt for tasks created in the period that have responded. By assignee, a task several
    /// people share (§6.2.3) is in each one's average; the overall figure counts it once.
    /// </summary>
    public async Task<MeanTimeReport> MeanTimeToRespondAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var q = Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.FirstRespondedAt != null && t.CreatedAt >= f.FromUtc && t.CreatedAt < f.ToUtc);
        var rows = await q.SelectMany(t => t.Assignments, (t, x) => new { x.UserId, t.CreatedAt, t.FirstRespondedAt })
            .GroupBy(x => x.UserId)
            .Select(g => new
            {
                UserId = (Guid?)g.Key,
                Count = g.Count(),
                AvgHours = g.Average(x => (x.FirstRespondedAt!.Value - x.CreatedAt).TotalHours)
            }).ToListAsync(ct);
        var unassigned = q.Where(t => !t.Assignments.Any());
        var unassignedCount = await unassigned.CountAsync(ct);
        if (unassignedCount > 0)
            rows.Add(new
            {
                UserId = (Guid?)null,
                Count = unassignedCount,
                AvgHours = await unassigned.AverageAsync(t => (t.FirstRespondedAt!.Value - t.CreatedAt).TotalHours, ct)
            });
        var overallCount = await q.CountAsync(ct);
        double? overall = overallCount == 0 ? null
            : await q.AverageAsync(t => (t.FirstRespondedAt!.Value - t.CreatedAt).TotalHours, ct);
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return new MeanTimeReport(
            rows.Select(r => new PersonAverageRow(r.UserId, Name(r.UserId, names, "Unassigned"), r.Count, r.AvgHours))
                .OrderBy(r => r.AverageHours).ToList(),
            overallCount, overall);
    }

    /// <summary>Average CompletedAt - CreatedAt for tasks completed in the period, by assignee as <see cref="MeanTimeToRespondAsync"/> is.</summary>
    public async Task<MeanTimeReport> MeanTimeToResolveAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var q = Apply(db.Tasks.AsNoTracking(), f, actor)
            .Where(t => t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc);
        var rows = await q.SelectMany(t => t.Assignments, (t, x) => new { x.UserId, t.CreatedAt, t.CompletedAt })
            .GroupBy(x => x.UserId)
            .Select(g => new
            {
                UserId = (Guid?)g.Key,
                Count = g.Count(),
                AvgHours = g.Average(x => (x.CompletedAt!.Value - x.CreatedAt).TotalHours)
            }).ToListAsync(ct);
        var unassigned = q.Where(t => !t.Assignments.Any());
        var unassignedCount = await unassigned.CountAsync(ct);
        if (unassignedCount > 0)
            rows.Add(new
            {
                UserId = (Guid?)null,
                Count = unassignedCount,
                AvgHours = await unassigned.AverageAsync(t => (t.CompletedAt!.Value - t.CreatedAt).TotalHours, ct)
            });
        var overallCount = await q.CountAsync(ct);
        double? overall = overallCount == 0 ? null
            : await q.AverageAsync(t => (t.CompletedAt!.Value - t.CreatedAt).TotalHours, ct);
        var names = await NamesAsync(rows.Select(r => r.UserId), ct);
        return new MeanTimeReport(
            rows.Select(r => new PersonAverageRow(r.UserId, Name(r.UserId, names, "Unassigned"), r.Count, r.AvgHours))
                .OrderBy(r => r.AverageHours).ToList(),
            overallCount, overall);
    }

    /// <summary>
    /// Time logged in the period (by entry date) per person, with each task's estimate against all time logged on it
    /// to date. Scoped by the task's project and department, like time logging itself (§6.2.1). The people the report
    /// covers (<see cref="PeopleAsync"/>) are listed with zero when they logged nothing.
    /// </summary>
    public async Task<TimeByPersonReport> TimeByPersonAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var from = DateOnly.FromDateTime(f.FromUtc);
        var to = DateOnly.FromDateTime(f.ToUtc);
        var logged = await Apply(db.TimeEntries.AsNoTracking(), f, actor)
            .Where(e => e.Date >= from && e.Date < to)
            .GroupBy(e => new { e.UserId, e.TaskId })
            .Select(g => new LoggedTime(g.Key.UserId, g.Key.TaskId, g.Sum(e => e.DurationMinutes)))
            .ToListAsync(ct);
        var taskIds = logged.Select(l => l.TaskId).Distinct().ToList();
        var tasks = taskIds.Count == 0 ? new Dictionary<Guid, TaskTimeFacts>()
            : await Facts(db.Tasks.AsNoTracking().Where(t => taskIds.Contains(t.Id))).ToDictionaryAsync(t => t.Id, ct);
        var people = await PeopleAsync(f, actor, ct);
        var names = await NamesAsync(logged.Select(l => l.UserId).Concat(people).Select(id => (Guid?)id), ct);
        return TimeReportRules.TimeByPerson(logged, tasks, id => Name(id, names, "Unknown"), people);
    }

    /// <summary>
    /// The people Time by person lists even with nothing logged: active people (never the Claude user) whose own
    /// department is the one reported on - the project's when only a project is chosen - or everyone when neither is.
    /// </summary>
    private async Task<List<Guid>> PeopleAsync(ReportFilter f, Actor actor, CancellationToken ct)
    {
        var departmentId = RestrictedDepartment(actor) ?? f.DepartmentId;
        if (departmentId is null && f.ProjectId is Guid p)
            departmentId = await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => (Guid?)x.DepartmentId).FirstOrDefaultAsync(ct)
                ?? Guid.Empty; // an unknown project covers nobody
        var q = db.Users.AsNoTracking().Where(u => u.IsActive && !u.IsSystemAccount);
        if (departmentId is Guid d) q = q.Where(u => u.DepartmentId == d);
        return await q.Select(u => u.Id).ToListAsync(ct);
    }

    /// <summary>Tasks set to Done in the period, grouped by assignee: the estimate against all time logged on them.</summary>
    public async Task<EstimateAccuracyReport> EstimateAccuracyAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var done = await Facts(Apply(db.Tasks.AsNoTracking(), f, actor)
                .Where(t => t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc))
            .ToListAsync(ct);
        var names = await NamesAsync(done.SelectMany(t => t.AssigneeIds).Select(id => (Guid?)id), ct);
        return TimeReportRules.EstimateAccuracy(done, id => Name(id, names, "Unassigned"));
    }

    /// <summary>
    /// Every project in the period - open at some point in it, or with a task created or completed or time logged in it -
    /// closed ones included (§12 Project status). A department selects the projects it owns, whose figures cover the whole
    /// project, other departments' tasks on it included. Status changes come from the project audit trail.
    /// </summary>
    public async Task<ProjectStatusReport> ProjectStatusAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var from = DateOnly.FromDateTime(f.FromUtc);
        var to = DateOnly.FromDateTime(f.ToUtc);
        var projects = await Apply(db.Projects.AsNoTracking(), f, actor)
            .Where(p => p.CreatedAt < f.ToUtc)
            .Select(p => new ProjectFacts(p.Id, p.Number, p.Name, p.Status, p.Department.Name,
                p.Owner.IsSystemAccount ? ClaudeName : p.Owner.DisplayName, p.CreatedAt, p.TargetDate))
            .ToListAsync(ct);
        if (projects.Count == 0)
            return ProjectReportRules.Build([], new Dictionary<Guid, ProjectStatusHistory>(), [], [],
                new Dictionary<Guid, CriticalPathSummary>(), _ => "", f.FromUtc, f.ToUtc, DateOnly.FromDateTime(DateTime.UtcNow));

        // Only changes from the start of the period on are needed to rebuild the status over it.
        var ids = projects.Select(p => p.Id).ToList();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntity.Project && ids.Contains(a.EntityId) && a.Timestamp >= f.FromUtc
                && (a.Action == AuditAction.Updated || a.Action == AuditAction.Archived))
            .Select(a => new { a.EntityId, a.Timestamp, a.Details })
            .ToListAsync(ct);
        var changes = audit
            .Select(a => ProjectReportRules.TryReadStatusChange(a.Details, out var was, out var now)
                ? new ProjectStatusChange(a.EntityId, a.Timestamp, was, now) : null)
            .OfType<ProjectStatusChange>()
            .ToLookup(c => c.ProjectId);
        var histories = projects.ToDictionary(p => p.Id,
            p => ProjectReportRules.History(p.Status, changes[p.Id], f.FromUtc, f.ToUtc));

        // A project closed throughout the period is still in it when work went on under it.
        var closed = histories.Where(h => !h.Value.WasOpen).Select(h => (Guid?)h.Key).ToList();
        var active = new HashSet<Guid>();
        if (closed.Count > 0)
            active.UnionWith((await db.TimeEntries
                    .Where(e => closed.Contains(e.Task.ProjectId) && e.Date >= from && e.Date < to)
                    .Select(e => e.Task.ProjectId)
                    .Union(db.Tasks
                        .Where(t => closed.Contains(t.ProjectId) && ((t.CreatedAt >= f.FromUtc && t.CreatedAt < f.ToUtc)
                            || (t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc)))
                        .Select(t => t.ProjectId))
                    .ToListAsync(ct))
                .OfType<Guid>());
        var included = projects.Where(p => ProjectReportRules.Includes(histories[p.Id], active.Contains(p.Id))).ToList();
        var includedIds = included.Select(p => (Guid?)p.Id).ToList();

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => includedIds.Contains(t.ProjectId))
            .Select(t => new ProjectTaskFacts(
                new TaskTimeFacts(t.Id, t.Number, t.Title, t.Status, t.Assignments.Select(x => x.UserId).ToList(), t.EstimateMinutes,
                    t.TimeEntries.Sum(e => (int?)e.DurationMinutes) ?? 0),
                t.ProjectId!.Value, t.DueDate, t.CreatedAt, t.CompletedAt,
                t.TimeEntries.Where(e => e.Date >= from && e.Date < to).Sum(e => (int?)e.DurationMinutes) ?? 0))
            .ToListAsync(ct);
        var time = await db.TimeEntries.AsNoTracking()
            .Where(e => includedIds.Contains(e.Task.ProjectId))
            .GroupBy(e => new { e.Task.ProjectId, e.UserId })
            .Select(g => new ProjectPersonTime(g.Key.ProjectId!.Value, g.Key.UserId,
                g.Sum(e => e.Date >= from && e.Date < to ? e.DurationMinutes : 0), g.Sum(e => e.DurationMinutes)))
            .ToListAsync(ct);
        var schedules = await criticalPath.LatestSummariesAsync(
            included.Where(p => p.Status.IsOpen()).Select(p => p.Id).ToList(), ct);
        var names = await NamesAsync(tasks.SelectMany(t => t.Task.AssigneeIds).Concat(time.Select(t => t.UserId)).Select(id => (Guid?)id), ct);

        return ProjectReportRules.Build(included, histories, tasks, time, schedules, id => Name(id, names, "Unassigned"),
            f.FromUtc, f.ToUtc, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    /// <summary>
    /// Who the project status report's Owner and Assignee filters offer: the owners of the projects the viewer can report on
    /// (their own department's under a Department-scoped reports.view, §6.5), and the assignees of those projects' tasks that
    /// aren't Cancelled, whatever department they are in. Deactivated people stay, as reports look back.
    /// </summary>
    public async Task<ProjectReportPeople> ProjectReportPeopleAsync(CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var projects = db.Projects.AsNoTracking();
        if (RestrictedDepartment(actor) is Guid d) projects = projects.Where(p => p.DepartmentId == d);
        var ownerIds = await projects.Select(p => p.OwnerId).Distinct().ToListAsync(ct);
        var assigneeIds = await projects.SelectMany(p => p.Tasks)
            .Where(t => t.Status != TaskItemStatus.Cancelled)
            .SelectMany(t => t.Assignments)
            .Select(x => x.UserId).Distinct()
            .ToListAsync(ct);
        var ids = ownerIds.Concat(assigneeIds).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new ReportPerson(u.Id, u.IsSystemAccount ? ClaudeName : u.DisplayName, u.IsActive || u.IsSystemAccount))
            .ToDictionaryAsync(u => u.Id, ct);
        List<ReportPerson> Listed(IEnumerable<Guid> list) => list.Where(people.ContainsKey).Select(id => people[id])
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new ProjectReportPeople(Listed(ownerIds), Listed(assigneeIds));
    }

    /// <summary>
    /// Every asset in the register during the period - not Disposed at some point in it, registered in it, or with a linked task
    /// created, completed or logged against in it - disposed ones included (§12 Asset status). A department selects the assets it
    /// manages, and their linked tasks count whatever department they are filed in. Status changes come from the asset audit
    /// trail; checks are read as at the period's last day, or today when that is sooner.
    /// </summary>
    public async Task<AssetStatusReport> AssetStatusAsync(ReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var from = DateOnly.FromDateTime(f.FromUtc);
        var to = DateOnly.FromDateTime(f.ToUtc);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var asAt = AssetReportRules.AsAt(f.ToUtc, today);

        // An asset disposed of before the period is read only when it was registered or worked on in it.
        var assets = await Apply(db.Assets.AsNoTracking(), f, actor)
            .Where(a => a.CreatedAt < f.ToUtc)
            .Select(a => new
            {
                Asset = a,
                Worked = a.Tasks.Any(t => (t.CreatedAt >= f.FromUtc && t.CreatedAt < f.ToUtc)
                    || (t.Status == TaskItemStatus.Done && t.CompletedAt >= f.FromUtc && t.CompletedAt < f.ToUtc)
                    || t.TimeEntries.Any(e => e.Date >= from && e.Date < to))
            })
            .Where(x => x.Asset.DisposedOn == null || x.Asset.DisposedOn >= from || x.Asset.CreatedAt >= f.FromUtc || x.Worked)
            .Select(x => new AssetFacts(x.Asset.Id, x.Asset.AssetNumber, x.Asset.Name, x.Asset.Status, x.Asset.DisposedOn, x.Asset.CreatedAt,
                x.Asset.DepartmentId, x.Asset.Department.Name, x.Asset.AssetTypeId, x.Asset.AssetType.Name, x.Asset.AssetType.Category,
                x.Asset.AssetType.CheckIntervalDays, x.Asset.AssetLocationId, x.Asset.AssetLocation != null ? x.Asset.AssetLocation.Name : null,
                x.Asset.PurchaseValue,
                x.Asset.Checks.Where(c => c.CheckDate <= asAt).OrderByDescending(c => c.CheckDate).ThenByDescending(c => c.CreatedAt)
                    .Select(c => (DateOnly?)c.CheckDate).FirstOrDefault(),
                x.Asset.Checks.Where(c => c.CheckDate <= asAt).OrderByDescending(c => c.CheckDate).ThenByDescending(c => c.CreatedAt)
                    .Select(c => (AssetCheckOutcome?)c.Outcome).FirstOrDefault(),
                x.Asset.Checks.Count(c => c.CheckDate >= from && c.CheckDate < to),
                x.Worked))
            .ToListAsync(ct);

        // Only changes from the start of the period on are needed to rebuild the status over it.
        var ids = assets.Select(a => a.Id).ToList();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntity.Asset && ids.Contains(a.EntityId) && a.Timestamp >= f.FromUtc
                && a.Action == AuditAction.StatusChanged)
            .Select(a => new { a.EntityId, a.Timestamp, a.Details })
            .ToListAsync(ct);
        var changes = audit
            .Select(a => AssetReportRules.TryReadStatusChange(a.Details, out var was, out var now)
                ? new AssetStatusChange(a.EntityId, a.Timestamp, was, now) : null)
            .OfType<AssetStatusChange>()
            .ToLookup(c => c.AssetId);
        var histories = assets.ToDictionary(a => a.Id,
            a => AssetReportRules.History(a.Status, a.DisposedOn, changes[a.Id], f.FromUtc, f.ToUtc));
        var included = assets
            .Where(a => AssetReportRules.Includes(histories[a.Id], a.CreatedAt >= f.FromUtc, a.HadTaskActivity))
            .ToList();
        var includedIds = included.Select(a => (Guid?)a.Id).ToList();

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => includedIds.Contains(t.AssetId))
            .Select(t => new AssetTaskFacts(t.Id, t.Number, t.Title, t.Status, t.Assignments.Select(x => x.UserId).ToList(), t.AssetId!.Value, t.DueDate,
                t.CreatedAt, t.CompletedAt, t.TimeEntries.Where(e => e.Date >= from && e.Date < to).Sum(e => (int?)e.DurationMinutes) ?? 0))
            .ToListAsync(ct);
        var names = await NamesAsync(tasks.SelectMany(t => t.AssigneeIds).Select(id => (Guid?)id), ct);

        return AssetReportRules.Build(included, histories, tasks, id => Name(id, names, "Unassigned"), f.FromUtc, f.ToUtc, today);
    }

    /// <summary>
    /// One sprint's planned work and the Not Planned work done in its window (§12 Sprint status): the sprint chosen, or the first the
    /// Sprint filter offers - the active one, or the latest completed one (<see cref="SprintReportRules.Offered"/>). Sprints are
    /// company-wide (§6.3), so every viewer is offered them all; the tasks counted are still the viewer's department's. The planned
    /// tasks are those in it now and, once it has ended, the open ones it handed on, which only its Completed audit row records. Each
    /// task's status is read as at the sprint's end from its status changes. A department selects tasks filed in it; the project and
    /// assignee pick tasks after the filter options are worked out (<see cref="SprintReportRules.Build"/>).
    /// </summary>
    public async Task<SprintStatusReport> SprintStatusAsync(SprintReportFilter f, CancellationToken ct = default)
    {
        var actor = await RequireReportsAsync(ct);
        var now = DateTime.UtcNow;
        // A handful of sprints a year: read them all once, for the one reported on, the names and the filter's options.
        var allSprints = await db.Sprints.AsNoTracking()
            .Select(s => new SprintFacts(s.Id, s.Name, s.Goal, s.StartDate, s.EndDate, s.Status, s.CompletedAt))
            .ToListAsync(ct);
        var offered = SprintReportRules.Offered(allSprints);
        var sprint = f.SprintId is Guid id
            ? allSprints.FirstOrDefault(s => s.Id == id) ?? throw new NotFoundException("Sprint not found.")
            : offered.FirstOrDefault();
        if (sprint is null) return SprintStatusReport.Empty(now) with { Sprints = offered };
        var asAt = sprint.CompletedAt ?? now;

        // A handful of rows a year: read them all and parse the details in memory (a text column, not filtered in SQL).
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntity.Sprint && a.Action == AuditAction.Completed)
            .Select(a => new { a.EntityId, a.Timestamp, a.Details })
            .ToListAsync(ct);
        var completions = audit
            .Select(a => SprintReportRules.TryReadCompletion(a.EntityId, a.Timestamp, a.Details, out var c) ? c : null)
            .OfType<SprintCompletion>()
            .ToList();
        var handedOn = completions.Where(c => c.SprintId == sprint.Id).MaxBy(c => c.At)?.OpenTaskIds.ToList() ?? [];

        var sprintId = sprint.Id;
        var from = sprint.StartDate;
        var to = sprint.EndDay;
        var fromUtc = sprint.FromUtc;
        var toUtc = sprint.ToUtc;
        // Set to Done in the window and reopened since: CompletedAt has been cleared, so only the audit trail still says so.
        var doneInWindow = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntity.Task && a.Action == AuditAction.Completed && a.Timestamp >= fromUtc && a.Timestamp < toUtc)
            .Select(a => a.EntityId)
            .Distinct()
            .ToListAsync(ct);
        // Their time is read below, in one query: SprintMinutes and TotalMinutes are filled in by SprintReportRules.WithTime.
        var candidates = await InDepartment(db.Tasks.AsNoTracking(), f.DepartmentId, actor)
            .Where(t => t.SprintId == sprintId || handedOn.Contains(t.Id)
                || (t.SprintId == null && !t.InBacklog
                    && ((t.CreatedAt >= fromUtc && t.CreatedAt < toUtc)
                        || (t.Status == TaskItemStatus.Done && t.CompletedAt >= fromUtc && t.CompletedAt < toUtc)
                        || doneInWindow.Contains(t.Id)
                        || t.TimeEntries.Any(e => e.Date >= from && e.Date < to))))
            .Select(t => new SprintTaskFacts(
                new TaskTimeFacts(t.Id, t.Number, t.Title, t.Status, t.Assignments.Select(x => x.UserId).ToList(), t.EstimateMinutes, 0),
                t.ProjectId, t.SprintId, t.InBacklog, t.CreatedAt, t.CompletedAt, 0))
            .ToListAsync(ct);

        var taskIds = candidates.Select(t => t.Task.Id).ToList();
        List<SprintTimeEntry> entries = [];
        List<SprintStatusChange> changes = [];
        if (taskIds.Count > 0)
        {
            entries = await db.TimeEntries.AsNoTracking()
                .Where(e => taskIds.Contains(e.TaskId))
                .GroupBy(e => new { e.TaskId, e.UserId, e.Date })
                .Select(g => new SprintTimeEntry(g.Key.TaskId, g.Key.UserId, g.Key.Date, g.Sum(e => e.DurationMinutes)))
                .ToListAsync(ct);
            // Status changes from the window's start on: those in it show work set to Done, those after the as-at time undo the later ones.
            var statusRows = await db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityType == AuditEntity.Task && taskIds.Contains(a.EntityId) && a.Timestamp >= fromUtc
                    && (a.Action == AuditAction.StatusChanged || a.Action == AuditAction.Completed))
                .Select(a => new { a.EntityId, a.Timestamp, a.Details })
                .ToListAsync(ct);
            changes = statusRows
                .Select(a => AuditStatusChange.TryRead<TaskItemStatus>(a.Details, out var was, out var became) && was != became
                    ? new SprintStatusChange(a.EntityId, a.Timestamp, was, became) : null)
                .OfType<SprintStatusChange>()
                .ToList();
        }
        var (tasks, time, days) = SprintReportRules.WithTime(candidates, entries, sprint);

        // A task's project is named whichever department owns it: the task itself is the viewer's department's.
        var projectIds = tasks.Select(t => t.ProjectId).OfType<Guid>().Distinct().ToList();
        var projects = await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new SprintProjectFacts(p.Id, p.Number, p.Name, p.Department.Name))
            .ToDictionaryAsync(p => p.Id, ct);
        var sprintNames = allSprints.ToDictionary(s => s.Id, s => s.Name);
        var userIds = tasks.SelectMany(t => t.Task.AssigneeIds).Concat(time.Select(t => t.UserId)).Distinct().ToList();
        var people = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new ReportPerson(u.Id, u.IsSystemAccount ? ClaudeName : u.DisplayName, u.IsActive || u.IsSystemAccount))
            .ToDictionaryAsync(p => p.Id, ct);

        return SprintReportRules.Build(sprint, tasks, time, days, changes, projects, completions, sprintNames, people,
            f.ProjectId, f.AssigneeId, asAt) with { Sprints = offered };
    }

    private static IQueryable<TaskTimeFacts> Facts(IQueryable<TaskItem> q) =>
        q.Select(t => new TaskTimeFacts(t.Id, t.Number, t.Title, t.Status, t.Assignments.Select(x => x.UserId).ToList(), t.EstimateMinutes,
            t.TimeEntries.Sum(e => (int?)e.DurationMinutes) ?? 0));

    private async Task<Actor> RequireReportsAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanViewReports(actor), "You don't have permission to view reports.");
        return actor;
    }

    /// <summary>The caller's own department is the only one they can report on unless reports.view is granted for all (§6.5).</summary>
    public static Guid? RestrictedDepartment(Actor actor) =>
        actor.CanAnywhere(Permission.ReportsView) ? null : actor.DepartmentId ?? Guid.Empty;

    private static IQueryable<TaskItem> Apply(IQueryable<TaskItem> q, ReportFilter f, Actor actor)
    {
        if (f.ProjectId is Guid p) q = q.Where(t => t.ProjectId == p);
        return InDepartment(q, f.DepartmentId, actor);
    }

    /// <summary>The tasks filed in the department chosen, or in the caller's own under a Department-scoped reports.view (§6.5).</summary>
    private static IQueryable<TaskItem> InDepartment(IQueryable<TaskItem> q, Guid? departmentId, Actor actor)
    {
        var d = RestrictedDepartment(actor) ?? departmentId;
        return d is Guid id ? q.Where(t => t.DepartmentId == id) : q;
    }

    /// <summary>
    /// A project belongs to its own department, whichever departments its tasks are filed in (§12 Project status). The owner
    /// and assignee filters pick projects too (<see cref="ProjectReportRules.ForPeople"/>).
    /// </summary>
    private static IQueryable<Project> Apply(IQueryable<Project> q, ReportFilter f, Actor actor)
    {
        var departmentId = RestrictedDepartment(actor) ?? f.DepartmentId;
        if (f.ProjectId is Guid p) q = q.Where(x => x.Id == p);
        if (departmentId is Guid d) q = q.Where(x => x.DepartmentId == d);
        return ProjectReportRules.ForPeople(q, f.OwnerId, f.AssigneeId);
    }

    /// <summary>An asset belongs to its managing department (§6.19) and to no project, so only the department applies.</summary>
    private static IQueryable<Asset> Apply(IQueryable<Asset> q, ReportFilter f, Actor actor)
    {
        var departmentId = RestrictedDepartment(actor) ?? f.DepartmentId;
        return departmentId is Guid d ? q.Where(a => a.DepartmentId == d) : q;
    }

    /// <summary>Time entries take their project and department from their task.</summary>
    private static IQueryable<TimeEntry> Apply(IQueryable<TimeEntry> q, ReportFilter f, Actor actor)
    {
        var departmentId = RestrictedDepartment(actor) ?? f.DepartmentId;
        if (f.ProjectId is Guid p) q = q.Where(e => e.Task.ProjectId == p);
        if (departmentId is Guid d) q = q.Where(e => e.Task.DepartmentId == d);
        return q;
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.IsSystemAccount ? ClaudeName : u.DisplayName, ct);
    }

    private static string Name(Guid? id, IReadOnlyDictionary<Guid, string> names, string fallbackForNull)
    {
        if (id is null) return fallbackForNull;
        if (id == WellKnownIds.ClaudeAgentUserId) return ClaudeName;
        return names.TryGetValue(id.Value, out var n) ? n : "Unknown";
    }
}
