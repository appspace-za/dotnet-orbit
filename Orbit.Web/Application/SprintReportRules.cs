using System.Text.Json;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The pure part of the Sprint status report (§12): turns what <c>ReportingService</c> reads in SQL into report rows.
/// <list type="bullet">
/// <item>The planned tasks are those in the sprint now, and - once it has ended - the open ones it handed on, which its Completed
/// audit row lists (<see cref="TryReadCompletion"/>): starting the next sprint moves them without a row of their own.</item>
/// <item>The rest are sorted by where they are now, into the three kinds of unplanned work (<see cref="UnplannedKind"/>). Not
/// Planned tasks have no sprint, so they are matched by date: created, set to Done or logged against in the sprint's window.
/// Backlog tasks worked on anyway are matched only by work: set to Done or logged against in the window. So are the tasks of
/// another sprint - a later one, worked on ahead, or an earlier one, a finished task reopened or logged against. All three join
/// their project's row, after its planned tasks, so a project's row shows what was planned for it and what was done on it anyway;
/// the tasks on no project, of every kind, have a row of their own (decision 129). So every task logged against or set to Done in
/// the window is in one row or another, the department and the filters aside.</item>
/// <item>A task that was already Done when the sprint started and hasn't changed since is marked, its counts as they are
/// (<see cref="DoneBeforeSprint"/>): a planned one stayed in the sprint it was planned for, as only open tasks roll forward; an
/// unplanned one only had time logged in the window.</item>
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
    /// The kind of unplanned work a task is in a sprint, by where it is now, or null when it is none. Each kind counts a task set to
    /// Done or logged against in the sprint's window, first and last days included:
    /// <list type="bullet">
    /// <item><b>Not Planned</b> - in no sprint and not on the Backlog - also counts one created in the window.</item>
    /// <item><b>Backlog</b> - on the Backlog, in no sprint. Being created in the window isn't enough: a new Backlog item is work to
    /// plan, not work done.</item>
    /// <item><b>Other sprint</b> - in any sprint but this one: a later sprint's task worked on ahead, or an earlier one's - a
    /// finished task stays in the sprint it was finished in, so one reopened or logged against later is still there.</item>
    /// </list>
    /// A task in this sprint is none: it is planned. So is one the sprint handed on to the next sprint or returned to the Backlog, so
    /// <see cref="Build"/> asks only of the tasks that aren't planned. <c>ReportingService</c> reads the same in SQL.
    /// </summary>
    public static SprintWorkKind? UnplannedKind(SprintTaskFacts t, SprintFacts sprint, IEnumerable<SprintStatusChange>? changes = null)
    {
        var worked = DoneInWindow(t, changes ?? [], sprint) || t.SprintMinutes > 0;
        return t.SprintId switch
        {
            Guid s when s == sprint.Id => null,
            Guid => worked ? SprintWorkKind.OtherSprint : null,
            null when t.InBacklog => worked ? SprintWorkKind.Backlog : null,
            null => worked || (t.CreatedAt >= sprint.FromUtc && t.CreatedAt < sprint.ToUtc) ? SprintWorkKind.NotPlanned : null
        };
    }

    /// <summary>
    /// The sprints after <paramref name="sprint"/>: those that start later, whatever they are now (planned, active or completed). An
    /// other-sprint task in one is *Planned for* it; in any other, *From* it.
    /// </summary>
    public static IReadOnlySet<Guid> LaterSprints(IEnumerable<SprintFacts> all, SprintFacts sprint) =>
        all.Where(s => s.Id != sprint.Id && s.StartDate > sprint.StartDate).Select(s => s.Id).ToHashSet();

    /// <summary>
    /// A task was already Done when the sprint started and hasn't changed since, up to <paramref name="asAt"/>: work finished before
    /// the sprint - a planned task that stayed in it because only open tasks roll forward, or an unplanned one only logged against in
    /// it. <paramref name="changes"/> need only run from the
    /// sprint's start on, as <c>ReportingService</c> reads them: the status at the start is what the first change after it changed
    /// from (<see cref="StatusAt"/>). A <paramref name="completedAt"/> in the window, up to the as-at time, says it was finished in the
    /// sprint even without a change to show it, as <see cref="FinishedOn"/> reads it, so the two never disagree.
    /// </summary>
    public static bool DoneBeforeSprint(
        TaskItemStatus current, IEnumerable<SprintStatusChange> changes, SprintFacts sprint, DateTime asAt, DateTime? completedAt = null)
    {
        var list = changes as IReadOnlyCollection<SprintStatusChange> ?? changes.ToList();
        return StatusAt(current, list, sprint.FromUtc) == TaskItemStatus.Done
               && !list.Any(c => c.At >= sprint.FromUtc && c.At <= asAt)
               && !(completedAt is DateTime done && done >= sprint.FromUtc && done < sprint.ToUtc && done <= asAt);
    }

    /// <summary>
    /// The day a task was finished in the sprint - Tasks per day: the UTC day of the last change that set it to Done in the window,
    /// no later than <paramref name="asAt"/>; failing one, its <c>CompletedAt</c> when that is in the window and no later. Each task
    /// counts once, on its last such day; null when it wasn't finished in the window (before the sprint, say, or only after it).
    /// </summary>
    public static DateOnly? FinishedOn(SprintTaskFacts t, IEnumerable<SprintStatusChange> changes, SprintFacts sprint, DateTime asAt)
    {
        bool InWindow(DateTime at) => at >= sprint.FromUtc && at < sprint.ToUtc && at <= asAt;
        var last = changes.Where(c => c.To == TaskItemStatus.Done && InWindow(c.At)).MaxBy(c => c.At)?.At
                   ?? (t.CompletedAt is DateTime done && InWindow(done) ? done : null);
        return last is DateTime at ? DateOnly.FromDateTime(at) : null;
    }

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
    /// The report. <paramref name="tasks"/> are the sprint's tasks and the Not Planned, Backlog and other-sprint candidates as read in
    /// SQL (department applied), with their status now; <paramref name="time"/> and <paramref name="days"/> their time
    /// (<see cref="WithTime"/>); <paramref name="changes"/> their status changes from the sprint's start on;
    /// <paramref name="completions"/> every sprint's Completed audit row, <paramref name="sprintNames"/> every sprint's name, and
    /// <paramref name="laterSprintIds"/> the sprints after this one (<see cref="LaterSprints"/>), which word an other-sprint task's
    /// note. The filter options are worked out first, then <paramref name="projectId"/> and <paramref name="assigneeId"/> pick the
    /// tasks. A completed sprint whose Completed row can't be read has no tasks handed on: <c>SprintService</c> writes the row in the
    /// same save as the completion, so that only happens to hand-edited data.
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
        IReadOnlySet<Guid> laterSprintIds,
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
        // Every other task in one pass, by its kind of unplanned work; one that is none isn't in the report.
        var unplanned = all.Where(t => !plannedIds.Contains(t.Task.Id))
            .Select(t => (Facts: t, Kind: UnplannedKind(t, sprint, changesByTask[t.Task.Id])))
            .Where(x => x.Kind is not null)
            .ToLookup(x => x.Kind!.Value, x => x.Facts);
        // The work by kind, so every figure below goes by kind rather than by position.
        var work = Kinds.ToDictionary(k => k, k => k == SprintWorkKind.Planned ? planned : unplanned[k].ToList());

        // The options, before the filters apply: the projects with work of any kind, each of which has a row, and the people on the
        // tasks.
        var projectOptions = InProjectOrder(
                Kinds.SelectMany(k => work[k]).Select(t => t.ProjectId).OfType<Guid>().Distinct().Select(id => ProjectOf(projects, id)),
                p => p)
            .ToList();
        var assigneeOptions = Kinds.SelectMany(k => work[k]).SelectMany(t => t.Task.AssigneeIds).Distinct()
            .Where(people.ContainsKey).Select(id => people[id])
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        bool Picked(SprintTaskFacts t) =>
            (projectId is null || t.ProjectId == projectId) && (assigneeId is not Guid a || t.Task.AssigneeIds.Contains(a));
        foreach (var kind in Kinds) work[kind] = work[kind].Where(Picked).ToList();

        // A project's row holds all its work in the sprint, of every kind - as the projects charts count it - and the tasks on no
        // project have a row of their own (decision 129). Taken from the picked work, so the rows, the totals and the people always
        // hold the same tasks.
        var onRows = Kinds.SelectMany(k => work[k].Select(t => (Facts: t, Kind: k))).ToList();

        string Name(Guid? id) => id is Guid g && people.TryGetValue(g, out var p) ? p.Name : id is null ? "Unassigned" : "Unknown";
        var timeByTask = time.ToLookup(t => t.TaskId);
        var ended = sprint.Status == SprintStatus.Completed;

        SprintTaskRow Row(SprintTaskFacts t, SprintWorkKind kind)
        {
            var isHandedOn = handedOn.Contains(t.Task.Id);
            var outcome = Outcome(t.Task.Status, isHandedOn);
            var now = current[t.Task.Id];
            var reopened = ended && t.Task.Status.IsClosed() && !now.IsClosed();
            var otherSprint = kind == SprintWorkKind.OtherSprint && t.SprintId is Guid s ? s : (Guid?)null;
            // Planned or not: a task handed on was Left open, so never done before the sprint.
            var doneBefore = !isHandedOn && DoneBeforeSprint(now, changesByTask[t.Task.Id], sprint, asAt, t.CompletedAt);
            return new SprintTaskRow(t.Task.Id, t.Task.Number, t.Task.Title, now, outcome,
                TimeReportRules.AssigneeNames(t.Task.AssigneeIds, Name), t.Task.EstimateMinutes, t.SprintMinutes,
                t.Task.TotalMinutes, kind == SprintWorkKind.Planned && carriedIn.TryGetValue(t.Task.Id, out var from) ? from : null,
                isHandedOn ? leftOpenTo : null, reopened, kind, otherSprint is Guid o ? SprintName(o) : null,
                otherSprint is Guid l && laterSprintIds.Contains(l), doneBefore);
        }

        // A row's tasks in two parts, the planned ones and those worked on though not planned into the sprint, each by outcome; its
        // time in the sprint by kind of work.
        SprintGroupRow Group(SprintProjectFacts? project, IReadOnlyList<(SprintTaskFacts Facts, SprintWorkKind Kind)> tasks)
        {
            var facts = tasks.Select(x => x.Facts).ToList();
            var rows = tasks.Select(x => Row(x.Facts, x.Kind)).ToList();
            List<SprintTaskRow> Part(bool isPlanned) => rows.Where(r => (r.Work == SprintWorkKind.Planned) == isPlanned)
                .OrderBy(r => r.Outcome)
                .ThenBy(r => r.Status)
                .ThenBy(r => r.Number, StringComparer.Ordinal)
                .ToList();
            var inPlan = Part(true);
            var offPlan = Part(false);
            var byKind = rows.ToLookup(r => r.Work);
            return new SprintGroupRow(project, Count(inPlan), Count(offPlan),
                SprintKindCounts.From(k => byKind[k].Sum(r => r.SprintMinutes)), facts.Sum(t => t.Task.TotalMinutes),
                TimeReportRules.Compare(facts.Select(t => t.Task)), People(rows, facts, timeByTask, Name), inPlan, offPlan);
        }

        var groups = InProjectOrder(
                onRows.Where(x => x.Facts.ProjectId is not null)
                    .GroupBy(x => x.Facts.ProjectId!.Value)
                    .Select(g => (Project: ProjectOf(projects, g.Key), Tasks: g.ToList())),
                g => g.Project)
            .Select(g => Group(g.Project, g.Tasks))
            .ToList();
        var standalone = onRows.Where(x => x.Facts.ProjectId is null).ToList();
        if (standalone.Count > 0) groups.Add(Group(null, standalone));

        var taskRows = groups.SelectMany(g => g.TaskRows).ToList();
        var rowsByKind = taskRows.ToLookup(r => r.Work);
        SprintWorkTotals Totals(SprintWorkKind kind)
        {
            var facts = work[kind];
            return new(Count(rowsByKind[kind].ToList()), facts.Sum(t => t.SprintMinutes), facts.Sum(t => t.Task.TotalMinutes),
                TimeReportRules.Compare(facts.Select(t => t.Task)));
        }
        var summary = new SprintStatusSummary(
            Totals(SprintWorkKind.Planned), Totals(SprintWorkKind.NotPlanned), Totals(SprintWorkKind.Backlog),
            Totals(SprintWorkKind.OtherSprint), TimeReportRules.Compare(Kinds.SelectMany(k => work[k]).Select(t => t.Task)));

        var outcomes = taskRows.ToDictionary(r => r.TaskId, r => r.Outcome);
        var impact = Impact(work, outcomes, timeByTask, Name);

        var ids = Kinds.ToDictionary(k => k, k => work[k].Select(t => t.Task.Id).ToHashSet());
        var byDay = days.Where(d => d.Date >= sprint.StartDate && d.Date <= sprint.EndDate).ToLookup(d => d.Date);
        // Tasks per day: each task once, on the last day it was set to Done in the window.
        var finished = Kinds.ToDictionary(k => k, k => work[k]
            .Select(t => (t.Task.Id, Day: FinishedOn(t, changesByTask[t.Task.Id], sprint, asAt)))
            .Where(x => x.Day is not null)
            .ToLookup(x => x.Day!.Value, x => x.Id));
        var dayRows = new List<SprintDayRow>();
        for (var day = sprint.StartDate; day <= sprint.EndDate; day = day.AddDays(1))
        {
            var logged = byDay[day].ToList();
            var finishedOn = day;
            dayRows.Add(new SprintDayRow(day,
                SprintKindCounts.From(k => logged.Where(d => ids[k].Contains(d.TaskId)).Sum(d => d.Minutes)),
                SprintKindCounts.From(k => finished[k][finishedOn].Count())));
        }

        return new SprintStatusReport(sprint, asAt, summary, impact, groups, dayRows, leftOpenTo, projectOptions, assigneeOptions)
        {
            PersonTasks = PersonTasks(Kinds.SelectMany(k => work[k]), outcomes, impact, Name)
        };
    }

    /// <summary>Projects in the report's order: by department, then name, then number.</summary>
    private static IOrderedEnumerable<T> InProjectOrder<T>(IEnumerable<T> items, Func<T, SprintProjectFacts> project) =>
        items.OrderBy(x => project(x).DepartmentName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => project(x).Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => project(x).Number, StringComparer.Ordinal);

    /// <summary>The kinds of work, in the report's order: planned, then the unplanned kinds.</summary>
    private static readonly SprintWorkKind[] Kinds = Enum.GetValues<SprintWorkKind>();

    /// <summary>
    /// Each person's tasks by outcome - Tasks by person: every kind of work, cancelled ones included, a shared task for each of its
    /// assignees. In the people rows' order (<paramref name="impact"/>, by time in the sprint); anyone not among them - assigned only
    /// to cancelled tasks - after them by name; Unassigned last.
    /// </summary>
    private static IReadOnlyList<SprintPersonTasks> PersonTasks(
        IEnumerable<SprintTaskFacts> tasks, IReadOnlyDictionary<Guid, SprintTaskOutcome> outcomes, IReadOnlyList<SprintImpactRow> impact,
        Func<Guid?, string> name)
    {
        var order = impact.Select((p, i) => (p.UserId, i)).Where(x => x.UserId is not null).ToDictionary(x => x.UserId!.Value, x => x.i);
        return tasks
            .SelectMany(t => TimeReportRules.AssigneesOrNone(t.Task.AssigneeIds).Select(id => (UserId: id, Outcome: outcomes[t.Task.Id])))
            .GroupBy(x => x.UserId)
            .Select(g => new SprintPersonTasks(g.Key, name(g.Key),
                g.Count(x => x.Outcome == SprintTaskOutcome.Done),
                g.Count(x => x.Outcome == SprintTaskOutcome.LeftOpen),
                g.Count(x => x.Outcome == SprintTaskOutcome.Cancelled)))
            .OrderBy(p => p.UserId is null)
            .ThenBy(p => p.UserId is Guid id && order.TryGetValue(id, out var i) ? i : int.MaxValue)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static SprintProjectFacts ProjectOf(IReadOnlyDictionary<Guid, SprintProjectFacts> projects, Guid id) =>
        projects.TryGetValue(id, out var p) ? p : new SprintProjectFacts(id, "", "Unknown project", ProjectStatus.Active, "");

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
            leftOpen.Count(r => r.EstimateMinutes is null),
            rows.Count(r => r.DoneBeforeSprint));
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
    /// Each person's sprint, planned against unplanned work - Not Planned, from the Backlog and from another sprint: their assigned
    /// tasks that weren't Cancelled, and their own time in the sprint on each. Ordered by time in the sprint, then name; Unassigned
    /// (tasks and no time) last.
    /// </summary>
    private static IReadOnlyList<SprintImpactRow> Impact(
        IReadOnlyDictionary<SprintWorkKind, List<SprintTaskFacts>> work, IReadOnlyDictionary<Guid, SprintTaskOutcome> outcomes,
        ILookup<Guid, SprintPersonTime> timeByTask, Func<Guid?, string> name)
    {
        var minutes = work.ToDictionary(w => w.Key, w => w.Value.SelectMany(t => timeByTask[t.Task.Id])
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.SprintMinutes)));
        var assigned = work.ToDictionary(w => w.Key, w => w.Value.Where(t => outcomes[t.Task.Id] != SprintTaskOutcome.Cancelled)
            .SelectMany(t => TimeReportRules.AssigneesOrNone(t.Task.AssigneeIds).Select(id => (UserId: id, Task: t)))
            .ToLookup(x => x.UserId, x => x.Task));

        SprintImpactRow Row(Guid? id)
        {
            var mine = assigned[SprintWorkKind.Planned][id].ToList();
            return new SprintImpactRow(id, name(id),
                mine.Count(t => outcomes[t.Task.Id] == SprintTaskOutcome.Done),
                mine.Count(t => outcomes[t.Task.Id] == SprintTaskOutcome.LeftOpen),
                SprintKindCounts.From(k => assigned[k][id].Count()),
                SprintKindCounts.From(k => id is Guid g ? minutes[k].GetValueOrDefault(g) : 0));
        }

        var rows = minutes.Values.SelectMany(m => m.Keys).Select(id => (Guid?)id)
            .Union(assigned.Values.SelectMany(a => a.Select(g => g.Key)))
            .Where(id => id is not null)
            .Distinct()
            .Select(Row)
            .OrderByDescending(r => r.SprintMinutes)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (assigned.Values.Any(a => a[null].Any())) rows.Add(Row(null));
        return rows;
    }
}
