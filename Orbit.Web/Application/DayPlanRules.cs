using System.Globalization;
using System.Text.Json;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// One person's part of a day plan (spec §6.12): the tasks on it they are an assignee of, with their estimated load.
/// <see cref="AssigneeId"/> null is the tasks nobody is assigned to.
/// </summary>
public sealed record DayPlanGroup(Guid? AssigneeId, string Name, IReadOnlyList<TaskItem> Tasks)
{
    public int Done => Tasks.Count(t => t.Status == TaskItemStatus.Done);
    /// <summary>Estimated minutes (§6.10) over every task on the plan, and over the ones still open - the load left in the day.</summary>
    public int EstimatedMinutes => Tasks.Sum(t => t.EstimateMinutes ?? 0);
    public int OpenEstimatedMinutes => Tasks.Where(t => t.IsOpen).Sum(t => t.EstimateMinutes ?? 0);
    /// <summary>Open tasks with no estimate, so the sum isn't read as the whole load.</summary>
    public int OpenUnestimated => Tasks.Count(t => t.IsOpen && t.EstimateMinutes is null);
    /// <summary>Tasks other people are assigned to as well (§6.2.3): each counts here in full, so the load is an upper bound.</summary>
    public int Shared => Tasks.Count(t => t.AssigneeIds.Count > 1);

    /// <summary>The estimate drawn against a working day. The Unassigned group is never overloaded: it isn't anyone's day.</summary>
    public DayLoad Load(int workingDayMinutes) =>
        DayLoad.Of(EstimatedMinutes - OpenEstimatedMinutes, OpenEstimatedMinutes, workingDayMinutes, AssigneeId is not null);
}

/// <summary>
/// A day's estimate drawn as a bar against a working day (spec §6.12): the closed and open parts of the estimate and where the
/// working day ends, each a percentage of the bar, which is a working day long or the whole estimate when that is longer.
/// Overloaded is more still open than a working day, the signal to move something off the plan.
/// </summary>
public sealed record DayLoad(int ClosedPercent, int OpenPercent, int DayPercent, bool Overloaded)
{
    public static DayLoad Of(int closedMinutes, int openMinutes, int workingDayMinutes, bool person = true)
    {
        var closed = Math.Max(0, closedMinutes);
        var open = Math.Max(0, openMinutes);
        var day = Math.Max(1, workingDayMinutes);
        var scale = Math.Max(closed + open, day);
        var closedPercent = Percentage.Of(closed, scale);
        // The open part is what is left of the whole, so the two parts never round to more than the bar.
        var openPercent = Percentage.Of(closed + open, scale) - closedPercent;
        return new DayLoad(closedPercent, openPercent, Percentage.Of(day, scale), person && open > day);
    }
}

/// <summary>One of the Today page's views (spec §6.12): the Summary, the last plan's outcome, or one person's day.</summary>
public enum DayPlanViewKind { Summary, Yesterday, Person }

/// <summary>A Today page view; for <see cref="DayPlanViewKind.Person"/>, <see cref="AssigneeId"/> null is the unassigned tasks.</summary>
public sealed record DayPlanView(DayPlanViewKind Kind, Guid? AssigneeId = null)
{
    public static readonly DayPlanView Summary = new(DayPlanViewKind.Summary);
    public static readonly DayPlanView Yesterday = new(DayPlanViewKind.Yesterday);

    public static DayPlanView Person(Guid? assigneeId) => new(DayPlanViewKind.Person, assigneeId);

    /// <summary>The view's value in the page's address (<c>?View=</c>).</summary>
    public string Key => Kind switch
    {
        DayPlanViewKind.Summary => DayPlanRules.SummaryKey,
        DayPlanViewKind.Yesterday => DayPlanRules.YesterdayKey,
        _ => AssigneeId?.ToString() ?? DayPlanRules.UnassignedKey
    };
}

/// <summary>
/// A flagged task on someone's day (spec §6.12), with why: overdue, blocked or waiting, gated by a dependency (§6.15), or
/// carried over again and again.
/// </summary>
public sealed record DayPlanFlag(TaskItem Task, IReadOnlyList<string> Reasons);

/// <summary>
/// One person's stand-up on the Today page (spec §6.12): today's tasks, and what became of their part of the last plan -
/// closed, carried over to today, or still sitting on it - with the time they logged that day and today's flagged tasks.
/// </summary>
public sealed record DayPlanPerson(DayPlanGroup Today, IReadOnlyList<TaskItem> Closed, IReadOnlyList<TaskItem> CarriedOver,
    IReadOnlyList<TaskItem> NotFinished, IReadOnlyList<LoggedTask> Logged, IReadOnlyList<DayPlanFlag> Flags)
{
    public Guid? AssigneeId => Today.AssigneeId;
    public string Name => Today.Name;
    public int LoggedMinutes => Logged.Sum(l => l.Minutes);
    public DayPlanView View => DayPlanView.Person(AssigneeId);
}

/// <summary>What became of one person's part of the last plan (spec §6.12), for the Yesterday view; AssigneeId null is the unassigned tasks.</summary>
public sealed record DayPlanOutcome(Guid? AssigneeId, string Name, int Closed, int CarriedOver, int NotFinished, int LoggedMinutes);

/// <summary>
/// What became of the last plan (spec §6.12), the one rule every Today view counts it by (<see cref="DayPlanRules.LastPlan"/>).
/// <see cref="Closed"/>, <see cref="CarriedOver"/> and <see cref="NotFinished"/> are that day's tasks only: closed since, whenever
/// that was (a task done this morning is still the plan's, <see cref="DayPlanRules.DoneAfter"/>), carried over from it to today, and
/// still on it. A task carried over to today from an older plan, or left on one, belongs to its own day: it is in
/// <see cref="CarriedFromEarlier"/> or <see cref="LeftEarlier"/>, so a view can still show it under that day rather than lose it.
/// </summary>
public sealed record LastPlanOutcome(DateOnly? Day, IReadOnlyList<TaskItem> Closed, IReadOnlyList<TaskItem> CarriedOver,
    IReadOnlyList<TaskItem> NotFinished, IReadOnlyList<TaskItem> CarriedFromEarlier, IReadOnlyList<TaskItem> LeftEarlier)
{
    public int Done => Closed.Count(t => t.Status == TaskItemStatus.Done);
}

/// <summary>
/// The day plan (spec §6.12) by person, its carry-overs, and the Today page's views, read from a task's <c>Planned</c> audit
/// rows: <c>{"plannedFor":{"from":"2026-09-25","to":"2026-09-28"}}</c>, as <see cref="ChangeSet"/> writes it for the Today tick
/// and <c>TaskService.CarryOverAsync</c> writes it (with <c>carriedOver: true</c>) for the carry-over buttons.
/// </summary>
public static class DayPlanRules
{
    public const string SummaryKey = "summary";
    public const string YesterdayKey = "yesterday";
    public const string UnassignedKey = "unassigned";

    /// <summary>
    /// A day plan by person: one group per assignee, by name, then the tasks nobody is assigned to. A task several people share
    /// (§6.2.3) is in each of their groups with its whole estimate - there is no rule for splitting one. Tasks keep their order.
    /// </summary>
    public static IReadOnlyList<DayPlanGroup> GroupByAssignee(IEnumerable<TaskItem> planned)
    {
        var tasks = planned.ToList();
        var groups = tasks.SelectMany(t => t.Assignees.Select(u => (User: u, Task: t)))
            .GroupBy(x => x.User.Id)
            .Select(g => new DayPlanGroup(g.Key, g.First().User.DisplayName, g.Select(x => x.Task).ToList()))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.AssigneeId)
            .ToList();
        var unassigned = tasks.Where(t => t.IsUnassigned).ToList();
        if (unassigned.Count > 0) groups.Add(new DayPlanGroup(null, "Unassigned", unassigned));
        return groups;
    }

    /// <summary>From this many carry-overs a task's badge is shown in red, and the task flagged: it keeps slipping.</summary>
    public const int RepeatedCarryOvers = 3;

    /// <summary>
    /// True when the entry moved the task from one day's plan to a later day's. A first planning (no <c>from</c>, which the
    /// serializer omits when null), a move to an earlier or the same day, any other change and bad JSON are not carry-overs.
    /// </summary>
    public static bool IsCarryOver(string? details) => TryReadMove(details, out var from, out var to) && from < to;

    /// <summary>The number of carry-overs per task in <paramref name="plannedEntries"/>; tasks never carried over are absent.</summary>
    public static IReadOnlyDictionary<Guid, int> CountCarryOvers(IEnumerable<(Guid TaskId, string? Details)> plannedEntries) =>
        plannedEntries.Where(e => IsCarryOver(e.Details))
            .GroupBy(e => e.TaskId)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// The tasks carried over onto <paramref name="date"/>'s plan from an earlier day's, with the day each came from (the latest,
    /// should one have come more than once). Read from the same <c>Planned</c> entries as <see cref="CountCarryOvers"/>.
    /// </summary>
    public static IReadOnlyDictionary<Guid, DateOnly> CarriedInto(IEnumerable<(Guid TaskId, string? Details)> plannedEntries, DateOnly date)
    {
        var carried = new Dictionary<Guid, DateOnly>();
        foreach (var (taskId, details) in plannedEntries)
        {
            if (!TryReadMove(details, out var from, out var to) || to != date || from >= to) continue;
            if (!carried.TryGetValue(taskId, out var seen) || from > seen) carried[taskId] = from;
        }
        return carried;
    }

    /// <summary>
    /// The last plan before today: the latest earlier day with tasks still dated it, or the latest day tasks were carried over
    /// from, whichever is later - so a day whose open tasks were all carried over is still the last plan. Null when neither.
    /// </summary>
    public static DateOnly? LastPlanDate(DateOnly? latestEarlierPlan, IEnumerable<DateOnly> carriedFrom)
    {
        var latest = latestEarlierPlan;
        foreach (var day in carriedFrom)
            if (latest is null || day > latest) latest = day;
        return latest;
    }

    /// <summary>A button's name for an earlier plan: "Yesterday", the weekday within the last week ("Friday"), else "29 Sep".</summary>
    public static string WhenLabel(DateOnly day, DateOnly today) => (today.DayNumber - day.DayNumber) switch
    {
        1 => "Yesterday",
        > 1 and < 7 => day.ToString("dddd", CultureInfo.InvariantCulture),
        _ => day.ToString("d MMM", CultureInfo.InvariantCulture)
    };

    /// <summary>The same in a sentence: "Not finished yesterday", "... on Friday", "... on 29 Sep", and "done today".</summary>
    public static string WhenPhrase(DateOnly day, DateOnly today) => (today.DayNumber - day.DayNumber) switch
    {
        0 => "today",
        1 => "yesterday",
        _ => $"on {WhenLabel(day, today)}"
    };

    /// <summary>An earlier plan in a sentence: "yesterday's plan", "Friday's plan" within the last week, else "the plan for 29 Sep".</summary>
    public static string PlanName(DateOnly day, DateOnly today) => (today.DayNumber - day.DayNumber) switch
    {
        1 => "yesterday's plan",
        > 1 and < 7 => $"{WhenLabel(day, today)}'s plan",
        _ => $"the plan for {WhenLabel(day, today)}"
    };

    /// <summary>
    /// The last plan's outcome (<see cref="LastPlanOutcome"/>): its closed tasks, today's tasks carried over from it, and the
    /// unfinished ones dated it. The unfinished tasks are all of them when the last plan is also the latest with open tasks left on
    /// it (<see cref="DayPlan.PreviousDate"/>), and none when that is an older day, the last plan's own having all been carried over
    /// or closed.
    /// </summary>
    public static LastPlanOutcome LastPlan(DayPlan plan)
    {
        var day = plan.LastPlanDate;
        bool FromThatDay(TaskItem t) => plan.CarriedIn.TryGetValue(t.Id, out var from) && from == day;
        var carried = plan.Planned.Where(t => plan.CarriedIn.ContainsKey(t.Id)).ToList();
        return new LastPlanOutcome(day, plan.LastPlanClosed,
            carried.Where(FromThatDay).ToList(),
            plan.Unfinished.Where(t => t.PlannedFor == day).ToList(),
            carried.Where(t => !FromThatDay(t)).ToList(),
            plan.Unfinished.Where(t => t.PlannedFor != day).ToList());
    }

    /// <summary>
    /// The day a task on <paramref name="planDay"/>'s plan was done, when that was after the plan's day: a task finished this
    /// morning is still the last plan's, and the page says when it was done. Null for one done that day, a cancelled one (cancelling
    /// records no time) and an open one. A UTC date, as "today" is.
    /// </summary>
    public static DateOnly? DoneAfter(TaskItem task, DateOnly planDay) =>
        task.Status == TaskItemStatus.Done && task.CompletedAt is DateTime at && DateOnly.FromDateTime(at) > planDay
            ? DateOnly.FromDateTime(at)
            : null;

    /// <summary>
    /// The view the address asks for (<c>?View=</c>), resolved leniently: the Yesterday view when there is a last plan, a
    /// person's (by id, in any case) or the unassigned tasks' when they are on today's plan, and otherwise the Summary - a bad
    /// value, or someone with nothing planned today, is no reason to refuse the page.
    /// </summary>
    public static DayPlanView ResolveView(string? requested, IReadOnlyList<DayPlanGroup> groups, bool hasYesterday)
    {
        var key = requested?.Trim();
        if (string.IsNullOrEmpty(key)) return DayPlanView.Summary;
        if (key.Equals(YesterdayKey, StringComparison.OrdinalIgnoreCase))
            return hasYesterday ? DayPlanView.Yesterday : DayPlanView.Summary;
        if (key.Equals(UnassignedKey, StringComparison.OrdinalIgnoreCase))
            return groups.Any(g => g.AssigneeId is null) ? DayPlanView.Person(null) : DayPlanView.Summary;
        return Guid.TryParse(key, out var id) && groups.Any(g => g.AssigneeId == id) ? DayPlanView.Person(id) : DayPlanView.Summary;
    }

    /// <summary>
    /// Today's open tasks worth raising in the meeting, in their order, each with its reasons in a fixed order: overdue,
    /// blocked or waiting, gated by a dependency (§6.15, <paramref name="waiting"/>), carried over <see cref="RepeatedCarryOvers"/>
    /// times or more. Closed tasks are never flagged.
    /// </summary>
    public static IReadOnlyList<DayPlanFlag> Flags(IEnumerable<TaskItem> tasks, DateOnly today,
        IReadOnlyDictionary<Guid, int> carryOvers, IReadOnlyDictionary<Guid, WaitingSummary> waiting)
    {
        var flags = new List<DayPlanFlag>();
        foreach (var t in tasks.Where(t => t.IsOpen))
        {
            var reasons = new List<string>();
            if (t.IsOverdue(today)) reasons.Add($"Overdue (due {t.DueDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})");
            if (t.Status is TaskItemStatus.Blocked or TaskItemStatus.Waiting) reasons.Add(t.Status.Label());
            if (waiting.TryGetValue(t.Id, out var gate)) reasons.Add($"Gated: {gate.Reason}");
            if (carryOvers.TryGetValue(t.Id, out var carried) && carried >= RepeatedCarryOvers) reasons.Add($"Carried over {carried}×");
            if (reasons.Count > 0) flags.Add(new DayPlanFlag(t, reasons));
        }
        return flags;
    }

    /// <summary>
    /// Everyone on today's plan as a stand-up (<see cref="Person"/>), in <see cref="GroupByAssignee"/>'s order: one per Today
    /// page button.
    /// </summary>
    public static IReadOnlyList<DayPlanPerson> People(DayPlan plan, IReadOnlyDictionary<Guid, WaitingSummary> waiting)
    {
        var last = LastPlan(plan);
        return GroupByAssignee(plan.Planned).Select(g => Person(plan, last, g, waiting)).ToList();
    }

    /// <summary>
    /// One person's stand-up: their part of today's plan and of the last one (<see cref="LastPlan"/>), then their tasks carried over
    /// to today from, or left on, an older plan, which the stand-up labels with their day. A shared task (§6.2.3) is each
    /// assignee's; the Unassigned group takes the tasks nobody is assigned to and, being nobody, no logged time. Logged time is
    /// biggest first.
    /// </summary>
    private static DayPlanPerson Person(DayPlan plan, LastPlanOutcome last, DayPlanGroup group, IReadOnlyDictionary<Guid, WaitingSummary> waiting)
    {
        bool Theirs(TaskItem t) => group.AssigneeId is Guid id ? t.IsAssignedTo(id) : t.IsUnassigned;
        IReadOnlyList<LoggedTask> logged = group.AssigneeId is Guid me
            ? plan.Logged.Where(l => l.UserId == me).OrderByDescending(l => l.Minutes).ThenBy(l => l.Number, StringComparer.Ordinal).ToList()
            : [];
        return new DayPlanPerson(group,
            last.Closed.Where(Theirs).ToList(),
            last.CarriedOver.Concat(last.CarriedFromEarlier).Where(Theirs).ToList(),
            last.NotFinished.Concat(last.LeftEarlier).Where(Theirs).ToList(),
            logged,
            Flags(group.Tasks, plan.Date, plan.CarryOvers, waiting));
    }

    /// <summary>
    /// The last plan's outcome by person, for the Yesterday view: what each assignee closed, carried over to today and left
    /// unfinished of that day's tasks (<see cref="LastPlan"/>, as the Summary's donut counts them), and the time they logged that
    /// day (on any task, planned or not). A shared task counts for each assignee. By name, then the unassigned tasks; someone with
    /// nothing to show isn't listed.
    /// </summary>
    public static IReadOnlyList<DayPlanOutcome> Outcomes(DayPlan plan)
    {
        var last = LastPlan(plan);
        var people = new Dictionary<Guid, Tally>();
        var nobody = new Tally("Unassigned");

        Tally For(Guid id, string name) => people.TryGetValue(id, out var known) ? known : (people[id] = new Tally(name));
        void Count(IEnumerable<TaskItem> tasks, Action<Tally> add)
        {
            foreach (var task in tasks)
            {
                if (task.IsUnassigned) add(nobody);
                foreach (var u in task.Assignees) add(For(u.Id, u.DisplayName));
            }
        }

        Count(last.Closed, t => t.Closed++);
        Count(last.CarriedOver, t => t.CarriedOver++);
        Count(last.NotFinished, t => t.NotFinished++);
        foreach (var l in plan.Logged) For(l.UserId, l.UserName).Logged += l.Minutes;

        var rows = people.OrderBy(p => p.Value.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Key)
            .Select(p => p.Value.Row(p.Key)).ToList();
        if (nobody.Closed + nobody.CarriedOver + nobody.NotFinished > 0) rows.Add(nobody.Row(null));
        return rows;
    }

    private sealed class Tally(string name)
    {
        public string Name { get; } = name;
        public int Closed { get; set; }
        public int CarriedOver { get; set; }
        public int NotFinished { get; set; }
        public int Logged { get; set; }
        public DayPlanOutcome Row(Guid? id) => new(id, Name, Closed, CarriedOver, NotFinished, Logged);
    }

    /// <summary>Reads a <c>Planned</c> entry's move; false for a first planning (no <c>from</c>), a removal, any other change and bad JSON.</summary>
    private static bool TryReadMove(string? details, out DateOnly from, out DateOnly to)
    {
        from = to = default;
        if (string.IsNullOrWhiteSpace(details)) return false;
        try
        {
            using var doc = JsonDocument.Parse(details);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("plannedFor", out var planned)
                && planned.ValueKind == JsonValueKind.Object
                && TryReadDate(planned, "from", out from)
                && TryReadDate(planned, "to", out to);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDate(JsonElement parent, string name, out DateOnly date)
    {
        date = default;
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
