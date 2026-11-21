using Orbit.Application.Models;

namespace Orbit.Application;

/// <summary>
/// The pure part of the Time utilisation report (§12): turns the time <c>ReportingService</c> reads in SQL into the report.
/// <list type="bullet">
/// <item>The window is <c>from</c> to <c>to</c>, both days included; an entry counts in it by its own date.</item>
/// <item>It is about people (decision 150): the people of the department reported on - by their home department - who were employed
/// in the window or logged time in it, and all of their time, on any department's task.</item>
/// <item>The Person options are those people, worked out before the Person filter applies; the filter then keeps only that person's
/// entries, and narrows the people who logged nothing to them.</item>
/// <item>A task the viewer can't open is listed by number and department only (<see cref="TimeUtilisationTaskFacts.Restricted"/>).</item>
/// <item><i>To date</i> on a task is everyone's time on it, ever, whatever the filter: the estimate is for the whole task, so the
/// comparison is <see cref="TimeReportRules.Compare"/>'s, over each task once.</item>
/// <item>Every task stands alone: time on a subtask isn't rolled into its parent (§6.10).</item>
/// </list>
/// </summary>
public static class TimeUtilisationRules
{
    private const string Unknown = "Unknown";

    /// <summary>
    /// The report. <paramref name="people"/> are everyone the department scope covers (§6.3), whatever their dates;
    /// <paramref name="employed"/> those of them employed on some day of the window, listed even when they logged nothing;
    /// <paramref name="entries"/> the window's time of those people, summed by task, person and day; <paramref name="tasks"/> the facts
    /// of their tasks; <paramref name="toDate"/> each person's time on those tasks to date. An entry outside the window, on a task not
    /// in <paramref name="tasks"/> or by someone not in <paramref name="people"/>, is dropped.
    /// </summary>
    public static TimeUtilisationReport Build(
        DateOnly from, DateOnly to, IEnumerable<TimeUtilisationEntry> entries, IReadOnlyDictionary<Guid, TimeUtilisationTaskFacts> tasks,
        IEnumerable<TimeUtilisationPersonTime> toDate, IReadOnlyDictionary<Guid, TimeUtilisationProjectFacts> projects,
        IReadOnlyDictionary<Guid, ReportPerson> people, IReadOnlySet<Guid> employed, Guid? personId)
    {
        var inWindow = entries.Where(e => e.Date >= from && e.Date <= to && tasks.ContainsKey(e.TaskId) && people.ContainsKey(e.UserId)).ToList();
        string Name(Guid id) => people.TryGetValue(id, out var p) ? p.Name : Unknown;

        var inReport = inWindow.Select(e => e.UserId).Concat(employed.Where(people.ContainsKey)).ToHashSet();
        var options = inReport.Select(id => people[id])
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var picked = personId is Guid only ? inWindow.Where(e => e.UserId == only).ToList() : inWindow;
        var listed = employed.Where(people.ContainsKey).Where(id => personId is not Guid only || id == only).ToList();

        var byPerson = picked.GroupBy(e => e.UserId)
            .Select(g => new TimeUtilisationPersonRow(g.Key, Name(g.Key), g.Sum(e => e.Minutes), g.Select(e => e.TaskId).Distinct().Count()))
            .OrderByDescending(r => r.Minutes)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var withTime = byPerson.Select(r => r.UserId).ToHashSet();
        var nothing = listed.Where(id => !withTime.Contains(id))
            .Select(id => new TimeUtilisationPersonRow(id, Name(id), 0, 0))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var personToDate = toDate.GroupBy(t => (t.TaskId, t.UserId)).ToDictionary(g => g.Key, g => g.Sum(t => t.Minutes));
        var taskRows = picked.GroupBy(e => e.TaskId)
            .Select(g =>
            {
                var t = tasks[g.Key];
                var taskPeople = g.GroupBy(e => e.UserId)
                    .Select(p =>
                    {
                        var minutes = p.Sum(e => e.Minutes);
                        return new TimeUtilisationTaskPerson(p.Key, Name(p.Key), minutes,
                            personToDate.TryGetValue((g.Key, p.Key), out var all) ? all : minutes, t.AssigneeIds.Contains(p.Key));
                    })
                    .OrderByDescending(p => p.Minutes)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                return new TimeUtilisationTaskRow(t.Id, t.Number, t.Restricted ? "" : t.Title, t.Status, t.Type, ProjectOf(projects, t.ProjectId),
                    t.EstimateMinutes, g.Sum(e => e.Minutes), t.TotalMinutes, taskPeople, t.DepartmentName, t.Restricted);
            })
            .OrderByDescending(r => r.Minutes)
            .ThenBy(r => r.Number, StringComparer.Ordinal)
            .ToList();

        var worked = taskRows.Select(r => tasks[r.TaskId]).ToList();
        var summary = new TimeUtilisationSummary(picked.Sum(e => e.Minutes), byPerson.Count, nothing.Count, worked.Count,
            worked.Count(t => t.EstimateMinutes is null), TimeReportRules.Compare(worked.Select(t => t.AsTimeFacts())));

        var (size, buckets) = Buckets(from, to);
        return new TimeUtilisationReport(from, to, summary, [.. byPerson, .. nothing], taskRows, options)
        {
            Projects = ByProject(taskRows),
            Types = taskRows.GroupBy(r => r.Type)
                .Select(g => new TimeUtilisationTypeRow(g.Key, g.Sum(r => r.Minutes)))
                .OrderByDescending(r => r.Minutes).ThenBy(r => r.Type)
                .ToList(),
            Sources = taskRows.GroupBy(r => tasks[r.TaskId].Source)
                .Select(g => new TimeUtilisationSourceRow(g.Key, g.Sum(r => r.Minutes)))
                .OrderByDescending(r => r.Minutes).ThenBy(r => r.Source)
                .ToList(),
            BucketSize = size,
            Buckets = buckets.Select(b => b with { Minutes = picked.Where(e => e.Date >= b.From && e.Date <= b.To).Sum(e => e.Minutes) }).ToList(),
            // Named only when they are in the report: employed in the window or with time in it.
            Person = personId is Guid chosen && inReport.Contains(chosen) ? people[chosen] : null
        };
    }

    /// <summary>A column per day for a window up to this many days long, both ends included.</summary>
    public const int MaxDailyDays = 62;

    /// <summary>A column per ISO week for a window up to this many days long; a column per month beyond it.</summary>
    public const int MaxWeeklyDays = 366;

    /// <summary>
    /// The per-period chart's columns for a window, with no time in them yet: a day each up to <see cref="MaxDailyDays"/> days, an
    /// ISO week each (Monday to Sunday) up to <see cref="MaxWeeklyDays"/>, a calendar month each beyond. The first and last are
    /// clipped to the window, so every day of it is in exactly one.
    /// </summary>
    public static (TimeBucketSize Size, IReadOnlyList<TimeBucket> Buckets) Buckets(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        var size = days <= MaxDailyDays ? TimeBucketSize.Day : days <= MaxWeeklyDays ? TimeBucketSize.Week : TimeBucketSize.Month;
        var buckets = new List<TimeBucket>();
        for (var start = from; start <= to;)
        {
            var end = size switch
            {
                TimeBucketSize.Day => start,
                TimeBucketSize.Week => start.AddDays(6 - ((int)start.DayOfWeek + 6) % 7), // the Sunday of its ISO week
                _ => new DateOnly(start.Year, start.Month, 1).AddMonths(1).AddDays(-1)
            };
            if (end > to) end = to;
            buckets.Add(new TimeBucket(start, end));
            start = end.AddDays(1);
        }
        return (size, buckets);
    }

    /// <summary>The time by project: the projects with time, most first, then by name; the tasks on no project last, when they have any.</summary>
    private static IReadOnlyList<TimeUtilisationProjectRow> ByProject(IReadOnlyList<TimeUtilisationTaskRow> rows)
    {
        var projects = rows.Where(r => r.Project is not null)
            .GroupBy(r => r.Project!.Id)
            .Select(g => new TimeUtilisationProjectRow(g.First().Project, g.Sum(r => r.Minutes)))
            .OrderByDescending(r => r.Minutes)
            .ThenBy(r => r.Project!.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var none = rows.Where(r => r.Project is null).Sum(r => r.Minutes);
        if (none > 0) projects.Add(new TimeUtilisationProjectRow(null, none));
        return projects;
    }

    /// <summary>A task's project: null for none, and a stand-in for an id that isn't found.</summary>
    private static TimeUtilisationProjectFacts? ProjectOf(IReadOnlyDictionary<Guid, TimeUtilisationProjectFacts> projects, Guid? id) =>
        id is not Guid p ? null : projects.TryGetValue(p, out var found) ? found : new TimeUtilisationProjectFacts(p, "", "Unknown project");
}
