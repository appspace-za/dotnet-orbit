using System.Text.Json;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of the Sprint status report (§12): turns what <c>ReportingService</c> reads in SQL into report rows.
/// <list type="bullet">
/// <item>The planned tasks are those in the sprint now, and - once it has ended - the open ones it handed on, which its Completed
/// audit row lists (<see cref="TryReadCompletion"/>): starting the next sprint moves them without a row of their own.</item>
/// <item>Not Planned tasks have no sprint, so they are matched by date: Not Planned now, and created, set to Done or logged against
/// in the sprint's window (<see cref="CountsAsNotPlanned"/>).</item>
/// <item>A task's status is read as at the sprint's completion, or now while it runs, from its recorded status changes
/// (<see cref="StatusAt"/>), and every figure reads that one status - the outcomes, the people's tasks and the estimates - so later
/// changes to a task don't rewrite a completed sprint (<see cref="Outcome"/>).</item>
/// <item>Estimates are compared over tasks that have one and weren't Cancelled, against all time logged on them to date, as in
/// the other reports (<see cref="TimeReportRules.Compare"/>).</item>
/// </list>
/// </summary>
public static class SprintReportRules
{
    public const string Backlog = "Backlog";

    /// <summary>
    /// Reads a sprint's Completed audit row, as <c>SprintService</c> writes it (<see cref="SprintCompletedDetails"/>): the tasks rolled
    /// forward to the sprint started after it, or returned to the Backlog. False for any other shape, an id that isn't a Guid, and bad
    /// JSON.
    /// </summary>
    public static bool TryReadCompletion(Guid sprintId, DateTime at, string? details, out SprintCompletion completion)
    {
        completion = null!;
        if (string.IsNullOrWhiteSpace(details)) return false;
        SprintCompletedDetails? read;
        try
        {
            read = JsonSerializer.Deserialize<SprintCompletedDetails>(details, OrbitJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }
        switch (read)
        {
            case { RolledForwardTaskIds: { } rolled, RolledForwardTo: Guid next }:
                completion = new SprintCompletion(sprintId, at, next, rolled);
                return true;
            case { RolledForwardTaskIds: null, ReturnedToBacklogTaskIds: { } returned }:
                completion = new SprintCompletion(sprintId, at, null, returned);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The sprints the report offers, its default first: the active one, then the completed ones, latest completed first - by when
    /// each was completed, or by the end of its window where that wasn't recorded - then by end date and name. A planned sprint has
    /// nothing to review yet.
    /// </summary>
    public static IReadOnlyList<SprintFacts> Offered(IEnumerable<SprintFacts> sprints) =>
        sprints.Where(s => s.Status != SprintStatus.Planned)
            .OrderBy(s => s.Status == SprintStatus.Active ? 0 : 1)
            .ThenByDescending(s => s.CompletedAt ?? s.ToUtc)
            .ThenByDescending(s => s.EndDate)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// A task's status at <paramref name="asAt"/>: what the first change after it changed from, or its status now when nothing has
    /// changed since - as <see cref="ProjectReportRules.History"/> reads a project's.
    /// </summary>
    public static TaskItemStatus StatusAt(TaskItemStatus current, IEnumerable<SprintStatusChange> changes, DateTime asAt) =>
        changes.Where(c => c.At > asAt).MinBy(c => c.At)?.From ?? current;

    /// <summary>
    /// Splits the time on the tasks - one entry per task, person and day, all to date - into each task's time in the sprint's window
    /// and to date, each person's time on each task, and everyone's time on each task on each day of the window.
    /// </summary>
    public static (IReadOnlyList<SprintTaskFacts> Tasks, IReadOnlyList<SprintPersonTime> Time, IReadOnlyList<SprintDayTime> Days) WithTime(
        IEnumerable<SprintTaskFacts> tasks, IEnumerable<SprintTimeEntry> entries, SprintFacts sprint)
    {
        var all = entries.ToList();
        bool InWindow(SprintTimeEntry e) => e.Date >= sprint.StartDate && e.Date <= sprint.EndDate;
        var time = all.GroupBy(e => (e.TaskId, e.UserId))
            .Select(g => new SprintPersonTime(g.Key.TaskId, g.Key.UserId, g.Where(InWindow).Sum(e => e.Minutes), g.Sum(e => e.Minutes)))
            .ToList();
        var days = all.Where(InWindow).GroupBy(e => (e.TaskId, e.Date))
            .Select(g => new SprintDayTime(g.Key.TaskId, g.Key.Date, g.Sum(e => e.Minutes)))
            .ToList();
        var byTask = time.GroupBy(t => t.TaskId)
            .ToDictionary(g => g.Key, g => (Sprint: g.Sum(t => t.SprintMinutes), Total: g.Sum(t => t.TotalMinutes)));
        var withTime = tasks.Select(t =>
        {
            var (sprintMinutes, total) = byTask.GetValueOrDefault(t.Task.Id);
            return t with { SprintMinutes = sprintMinutes, Task = t.Task with { TotalMinutes = total } };
        }).ToList();
        return (withTime, time, days);
    }

    /// <summary>
    /// A Not Planned task counts in a sprint when it is Not Planned now (in no sprint and not on the Backlog) and was created, set to
    /// Done or logged against in the sprint's window, first and last days included. <c>ReportingService</c> reads the same in SQL.
    /// </summary>
    public static bool CountsAsNotPlanned(SprintTaskFacts t, SprintFacts sprint, IEnumerable<SprintStatusChange>? changes = null) =>
        t.SprintId is null && !t.InBacklog
        && ((t.CreatedAt >= sprint.FromUtc && t.CreatedAt < sprint.ToUtc) || DoneInWindow(t, changes ?? [], sprint) || t.SprintMinutes > 0);

    /// <summary>
    /// The task was set to Done in the sprint's window: it was completed in it and is still Done (<c>CompletedAt</c> is kept only
    /// while a task is Done), or a change in the window made it Done and it has been reopened since.
    /// </summary>
    public static bool DoneInWindow(SprintTaskFacts t, IEnumerable<SprintStatusChange> changes, SprintFacts sprint) =>
        (t.CompletedAt is DateTime done && done >= sprint.FromUtc && done < sprint.ToUtc)
        || changes.Any(c => c.To == TaskItemStatus.Done && c.At >= sprint.FromUtc && c.At < sprint.ToUtc);

    /// <summary>
    /// Where a task ended the sprint, from its status when the sprint ended, or now while it runs (<see cref="StatusAt"/>): a task the
    /// sprint handed on was Left open, whatever has become of it since; otherwise Done, Cancelled, or Left open for any open status.
    /// </summary>
    public static SprintTaskOutcome Outcome(TaskItemStatus statusAtEnd, bool handedOn) =>
        handedOn ? SprintTaskOutcome.LeftOpen
        : statusAtEnd == TaskItemStatus.Done ? SprintTaskOutcome.Done
        : statusAtEnd == TaskItemStatus.Cancelled ? SprintTaskOutcome.Cancelled
        : SprintTaskOutcome.LeftOpen;

    /// <summary>What is left of a task's estimate: the estimate less all time logged on it to date, never below nothing.</summary>
    public static int Remaining(int estimate, int loggedMinutes) => Math.Max(estimate - loggedMinutes, 0);

    /// <summary>
    /// The report. <paramref name="tasks"/> are the sprint's tasks and the Not Planned candidates as read in SQL (department applied),
    /// with their status now; <paramref name="time"/> and <paramref name="days"/> their time (<see cref="WithTime"/>);
    /// <paramref name="changes"/> their status changes from the sprint's start on; <paramref name="completions"/> every sprint's
    /// Completed audit row, and <paramref name="sprintNames"/> every sprint's name. The filter options are worked out first, then
    /// <paramref name="projectId"/> and <paramref name="assigneeId"/> pick the tasks.
    /// </summary>
    public static SprintStatusReport Build(
        SprintFacts sprint,
        IEnumerable<SprintTaskFacts> tasks,
        IEnumerable<SprintPersonTime> time,
        IEnumerable<SprintDayTime> days,
        IEnumerable<SprintStatusChange> changes,
        IReadOnlyDictionary<Guid, SprintProjectFacts> projects,
        IEnumerable<SprintCompletion> completions,
        IReadOnlyDictionary<Guid, string> sprintNames,
        IReadOnlyDictionary<Guid, ReportPerson> people,
        Guid? projectId, Guid? assigneeId, DateTime asAt)
    {
        var allCompletions = completions.ToList();
        var own = allCompletions.Where(c => c.SprintId == sprint.Id).MaxBy(c => c.At);
        var handedOn = own?.OpenTaskIds.ToHashSet() ?? [];
        string SprintName(Guid id) => sprintNames.TryGetValue(id, out var n) ? n : "another sprint";
        var leftOpenTo = own is null ? null : own.RolledForwardTo is Guid next ? SprintName(next) : Backlog;
        var carriedIn = allCompletions.Where(c => c.RolledForwardTo == sprint.Id)
            .OrderBy(c => c.At)
            .SelectMany(c => c.OpenTaskIds.Select(id => (Id: id, From: SprintName(c.SprintId))))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.Last().From);

        var changesByTask = changes.ToLookup(c => c.TaskId);
        var asNow = tasks.DistinctBy(t => t.Task.Id).ToList();
        var current = asNow.ToDictionary(t => t.Task.Id, t => t.Task.Status);
        // From here on a task's status is the one it had when the sprint ended, or has now while it runs: the outcomes, the people's
        // tasks and the estimates all read it, so they agree with each other whatever has happened to the task since.
        var all = asNow.Select(t => t with { Task = t.Task with { Status = StatusAt(t.Task.Status, changesByTask[t.Task.Id], asAt) } })
            .ToList();
        var planned = all.Where(t => t.SprintId == sprint.Id || handedOn.Contains(t.Task.Id)).ToList();
        var plannedIds = planned.Select(t => t.Task.Id).ToHashSet();
        var notPlanned = all.Where(t => !plannedIds.Contains(t.Task.Id) && CountsAsNotPlanned(t, sprint, changesByTask[t.Task.Id]))
            .ToList();

        // The options, before the filters apply: only the projects with planned work are the sprint's.
        var projectOptions = planned.Select(t => t.ProjectId).OfType<Guid>().Distinct()
            .Select(id => ProjectOf(projects, id))
            .OrderBy(p => p.DepartmentName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Number, StringComparer.Ordinal)
            .ToList();
        var assigneeOptions = planned.Concat(notPlanned).SelectMany(t => t.Task.AssigneeIds).Distinct()
            .Where(people.ContainsKey).Select(id => people[id])
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        bool Picked(SprintTaskFacts t) =>
            (projectId is null || t.ProjectId == projectId) && (assigneeId is not Guid a || t.Task.AssigneeIds.Contains(a));
        planned = planned.Where(Picked).ToList();
        notPlanned = notPlanned.Where(Picked).ToList();

        string Name(Guid? id) => id is Guid g && people.TryGetValue(g, out var p) ? p.Name : id is null ? "Unassigned" : "Unknown";
        var timeByTask = time.ToLookup(t => t.TaskId);
        var ended = sprint.Status == SprintStatus.Completed;

        SprintTaskRow Row(SprintTaskFacts t, bool isPlanned)
        {
            var isHandedOn = handedOn.Contains(t.Task.Id);
            var outcome = Outcome(t.Task.Status, isHandedOn);
            var now = current[t.Task.Id];
            var reopened = ended && t.Task.Status.IsClosed() && !now.IsClosed();
            string? label = null;
            if (!isPlanned && t.ProjectId is Guid p)
            {
                var project = ProjectOf(projects, p);
                label = $"{project.Number} {project.Name}".Trim();
            }
            return new SprintTaskRow(t.Task.Id, t.Task.Number, t.Task.Title, now, outcome,
                TimeReportRules.AssigneeNames(t.Task.AssigneeIds, Name), label, t.Task.EstimateMinutes, t.SprintMinutes,
                t.Task.TotalMinutes, isPlanned && carriedIn.TryGetValue(t.Task.Id, out var from) ? from : null,
                isHandedOn ? leftOpenTo : null, reopened);
        }

        SprintGroupRow Group(SprintGroupKind kind, SprintProjectFacts? project, IReadOnlyList<SprintTaskFacts> facts, bool isPlanned)
        {
            var rows = facts.Select(t => Row(t, isPlanned))
                .OrderBy(r => r.Outcome)
                .ThenBy(r => r.Status)
                .ThenBy(r => r.Number, StringComparer.Ordinal)
                .ToList();
            return new SprintGroupRow(kind, project, Count(rows), facts.Sum(t => t.SprintMinutes), facts.Sum(t => t.Task.TotalMinutes),
                TimeReportRules.Compare(facts.Select(t => t.Task)), People(rows, facts, timeByTask, Name), rows);
        }

        var groups = planned.Where(t => t.ProjectId is not null)
            .GroupBy(t => t.ProjectId!.Value)
            .Select(g => (Project: ProjectOf(projects, g.Key), Tasks: g.ToList()))
            .OrderBy(g => g.Project.DepartmentName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Project.Number, StringComparer.Ordinal)
            .Select(g => Group(SprintGroupKind.Project, g.Project, g.Tasks, true))
            .ToList();
        var standalone = planned.Where(t => t.ProjectId is null).ToList();
        if (standalone.Count > 0) groups.Add(Group(SprintGroupKind.NoProject, null, standalone, true));
        if (notPlanned.Count > 0) groups.Add(Group(SprintGroupKind.NotPlanned, null, notPlanned, false));

        var plannedRows = groups.Where(g => g.Kind != SprintGroupKind.NotPlanned).ToList();
        var notPlannedRow = groups.FirstOrDefault(g => g.Kind == SprintGroupKind.NotPlanned);
        var summary = new SprintStatusSummary(
            plannedRows.Aggregate(SprintTaskCounts.None, (sum, g) => sum.Plus(g.Tasks)),
            notPlannedRow?.Tasks ?? SprintTaskCounts.None,
            planned.Sum(t => t.SprintMinutes), notPlanned.Sum(t => t.SprintMinutes),
            planned.Sum(t => t.Task.TotalMinutes), notPlanned.Sum(t => t.Task.TotalMinutes),
            TimeReportRules.Compare(planned.Select(t => t.Task)), TimeReportRules.Compare(notPlanned.Select(t => t.Task)),
            TimeReportRules.Compare(planned.Concat(notPlanned).Select(t => t.Task)));

        var outcomes = groups.SelectMany(g => g.TaskRows).ToDictionary(r => r.TaskId, r => r.Outcome);
        var impact = Impact(planned, notPlanned, outcomes, timeByTask, Name);

        var plannedSet = planned.Select(t => t.Task.Id).ToHashSet();
        var notPlannedSet = notPlanned.Select(t => t.Task.Id).ToHashSet();
        var byDay = days.Where(d => d.Date >= sprint.StartDate && d.Date <= sprint.EndDate).ToLookup(d => d.Date);
        var dayRows = new List<SprintDayRow>();
        for (var day = sprint.StartDate; day <= sprint.EndDate; day = day.AddDays(1))
            dayRows.Add(new SprintDayRow(day,
                byDay[day].Where(d => plannedSet.Contains(d.TaskId)).Sum(d => d.Minutes),
                byDay[day].Where(d => notPlannedSet.Contains(d.TaskId)).Sum(d => d.Minutes)));

        return new SprintStatusReport(sprint, asAt, summary, impact, groups, dayRows, leftOpenTo, projectOptions, assigneeOptions);
    }

    private static SprintProjectFacts ProjectOf(IReadOnlyDictionary<Guid, SprintProjectFacts> projects, Guid id) =>
        projects.TryGetValue(id, out var p) ? p : new SprintProjectFacts(id, "", "Unknown project", "");

    private static SprintTaskCounts Count(IReadOnlyList<SprintTaskRow> rows)
    {
        var leftOpen = rows.Where(r => r.Outcome == SprintTaskOutcome.LeftOpen).ToList();
        return new SprintTaskCounts(
            rows.Count,
            rows.Count(r => r.Outcome == SprintTaskOutcome.Done),
            leftOpen.Count,
            rows.Count(r => r.Outcome == SprintTaskOutcome.Cancelled),
            rows.Count(r => r.CarriedInFrom is not null),
            leftOpen.Where(r => r.EstimateMinutes is not null).Sum(r => Remaining(r.EstimateMinutes!.Value, r.TotalMinutes)),
            leftOpen.Count(r => r.EstimateMinutes is null));
    }

    /// <summary>
    /// One row per person with time logged on the group's tasks or a task in it that wasn't Cancelled, and an Unassigned row for such
    /// tasks with nobody on them. A task several people share is in each one's row, whole; logged time is each person's own. Ordered
    /// by time in the sprint, then time to date, then name; Unassigned last.
    /// </summary>
    private static IReadOnlyList<SprintPersonRow> People(
        IReadOnlyList<SprintTaskRow> rows, IReadOnlyList<SprintTaskFacts> facts, ILookup<Guid, SprintPersonTime> timeByTask,
        Func<Guid?, string> name)
    {
        var logged = facts.SelectMany(t => timeByTask[t.Task.Id])
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => (Sprint: g.Sum(t => t.SprintMinutes), Total: g.Sum(t => t.TotalMinutes)));
        var rowById = rows.ToDictionary(r => r.TaskId);
        var assigned = facts.Where(t => !rowById[t.Task.Id].IsCancelled)
            .SelectMany(t => TimeReportRules.AssigneesOrNone(t.Task.AssigneeIds).Select(id => (UserId: id, Row: rowById[t.Task.Id])))
            .ToLookup(x => x.UserId, x => x.Row);

        SprintPersonRow Person(Guid? id, int sprintMinutes, int totalMinutes)
        {
            var mine = assigned[id].ToList();
            var estimated = mine.Where(r => r.EstimateMinutes is not null).ToList();
            return new SprintPersonRow(id, name(id), mine.Count, mine.Count(r => r.Outcome == SprintTaskOutcome.Done),
                mine.Count(r => r.Outcome == SprintTaskOutcome.LeftOpen), estimated.Count, estimated.Sum(r => r.EstimateMinutes!.Value),
                sprintMinutes, totalMinutes);
        }

        var people = logged.Keys.Select(id => (Guid?)id)
            .Union(assigned.Select(g => g.Key).Where(id => id is not null))
            .Select(id => logged.TryGetValue(id!.Value, out var l) ? Person(id, l.Sprint, l.Total) : Person(id, 0, 0))
            .OrderByDescending(r => r.SprintMinutes)
            .ThenByDescending(r => r.ToDateMinutes)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (assigned[null].Any()) people.Add(Person(null, 0, 0));
        return people;
    }

    /// <summary>
    /// Each person's sprint, planned against Not Planned: their assigned tasks that weren't Cancelled, and their own time in the sprint
    /// on each. Ordered by time in the sprint, then name; Unassigned (tasks and no time) last.
    /// </summary>
    private static IReadOnlyList<SprintImpactRow> Impact(
        IReadOnlyList<SprintTaskFacts> planned, IReadOnlyList<SprintTaskFacts> notPlanned,
        IReadOnlyDictionary<Guid, SprintTaskOutcome> outcomes, ILookup<Guid, SprintPersonTime> timeByTask, Func<Guid?, string> name)
    {
        static Dictionary<Guid, int> Minutes(IEnumerable<SprintTaskFacts> tasks, ILookup<Guid, SprintPersonTime> timeByTask) =>
            tasks.SelectMany(t => timeByTask[t.Task.Id]).GroupBy(t => t.UserId).ToDictionary(g => g.Key, g => g.Sum(t => t.SprintMinutes));
        ILookup<Guid?, SprintTaskFacts> Assigned(IEnumerable<SprintTaskFacts> tasks) =>
            tasks.Where(t => outcomes[t.Task.Id] != SprintTaskOutcome.Cancelled)
                .SelectMany(t => TimeReportRules.AssigneesOrNone(t.Task.AssigneeIds).Select(id => (UserId: id, Task: t)))
                .ToLookup(x => x.UserId, x => x.Task);

        var plannedMinutes = Minutes(planned, timeByTask);
        var notPlannedMinutes = Minutes(notPlanned, timeByTask);
        var plannedAssigned = Assigned(planned);
        var notPlannedAssigned = Assigned(notPlanned);

        SprintImpactRow Row(Guid? id)
        {
            var mine = plannedAssigned[id].ToList();
            return new SprintImpactRow(id, name(id), mine.Count,
                mine.Count(t => outcomes[t.Task.Id] == SprintTaskOutcome.Done),
                mine.Count(t => outcomes[t.Task.Id] == SprintTaskOutcome.LeftOpen),
                notPlannedAssigned[id].Count(),
                id is Guid g ? plannedMinutes.GetValueOrDefault(g) : 0,
                id is Guid h ? notPlannedMinutes.GetValueOrDefault(h) : 0);
        }

        var rows = plannedMinutes.Keys.Concat(notPlannedMinutes.Keys).Select(id => (Guid?)id)
            .Union(plannedAssigned.Select(g => g.Key)).Union(notPlannedAssigned.Select(g => g.Key))
            .Where(id => id is not null)
            .Distinct()
            .Select(Row)
            .OrderByDescending(r => r.SprintMinutes)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (plannedAssigned[null].Any() || notPlannedAssigned[null].Any()) rows.Add(Row(null));
        return rows;
    }
}
