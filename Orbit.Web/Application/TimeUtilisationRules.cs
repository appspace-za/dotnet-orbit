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
    /// <paramref name="availability"/> each one's availability over the window, day by day (missing: no pattern and never employed) -
    /// those employed on some day of it are listed even when they logged nothing; <paramref name="entries"/> the window's time of those
    /// people, summed by task, person and day; <paramref name="tasks"/> the facts of their tasks; <paramref name="toDate"/> each person's
    /// time on those tasks to date. An entry outside the window, on a task not in <paramref name="tasks"/> or by someone not in
    /// <paramref name="people"/>, is dropped.
    /// </summary>
    public static TimeUtilisationReport Build(
        DateOnly from, DateOnly to, IEnumerable<TimeUtilisationEntry> entries, IReadOnlyDictionary<Guid, TimeUtilisationTaskFacts> tasks,
        IEnumerable<TimeUtilisationPersonTime> toDate, IReadOnlyDictionary<Guid, TimeUtilisationProjectFacts> projects,
        IReadOnlyDictionary<Guid, ReportPerson> people, IReadOnlyDictionary<Guid, ReportAvailability> availability, int targetPercent,
        Guid? personId)
    {
        var inWindow = entries.Where(e => e.Date >= from && e.Date <= to && tasks.ContainsKey(e.TaskId) && people.ContainsKey(e.UserId)).ToList();
        string Name(Guid id) => people.TryGetValue(id, out var p) ? p.Name : Unknown;
        IReadOnlyList<AvailabilityDay> DaysOf(Guid id) =>
            availability.TryGetValue(id, out var a) ? a.Days.Where(d => d.Date >= from && d.Date <= to).ToList() : [];

        var employed = availability.Where(a => people.ContainsKey(a.Key) && a.Value.Days.Any(d => d.Employed && d.Date >= from && d.Date <= to))
            .Select(a => a.Key);
        var inReport = inWindow.Select(e => e.UserId).Concat(employed).ToHashSet();
        var options = inReport.Select(id => people[id])
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var picked = personId is Guid only ? inWindow.Where(e => e.UserId == only).ToList() : inWindow;
        var shown = inReport.Where(id => personId is not Guid only || id == only).ToList();
        var byUser = picked.ToLookup(e => e.UserId);
        var days = shown.ToDictionary(id => id, DaysOf);

        // Lowest utilisation first, the people to follow up; those with no availability to measure against last.
        var personRows = shown.Select(id =>
            {
                var mine = byUser[id].ToList();
                var available = days[id].Sum(d => d.AvailableMinutes);
                var minutes = mine.Sum(e => e.Minutes);
                return new TimeUtilisationPersonRow(id, Name(id), people[id].IsActive, availability.TryGetValue(id, out var a) && a.HasPattern,
                    available, days[id].Sum(d => d.UnavailableMinutes), minutes, mine.Select(e => e.TaskId).Distinct().Count(),
                    Availability.UtilisationRules.UnderTarget(minutes, available, targetPercent));
            })
            .OrderBy(r => r.UtilisationPercent is null)
            .ThenBy(r => r.UtilisationPercent ?? 0)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
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
        var logged = picked.Sum(e => e.Minutes);
        var availableTotal = personRows.Sum(r => r.AvailableMinutes);
        var summary = new TimeUtilisationSummary(logged, availableTotal, personRows.Sum(r => r.UnavailableMinutes),
            days.Values.SelectMany(d => d).Where(d => d.Holiday is not null).Select(d => d.Date).Distinct().Count(),
            Availability.UtilisationRules.Percent(logged, availableTotal), personRows.Count(r => r.UnderTarget),
            personRows.Count(r => r.Minutes > 0), personRows.Count(r => r.LoggedNothing), personRows.Sum(r => r.UnloggedMinutes),
            personRows.Count(r => r.UnloggedMinutes > 0), worked.Count, worked.Count(t => t.EstimateMinutes is null),
            TimeReportRules.Compare(worked.Select(t => t.AsTimeFacts())));

        var byName = personRows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        List<TimeUtilisationGridRow> Grid(IReadOnlyList<TimeBucket> buckets) => byName.Select(r =>
        {
            var cells = buckets.Select(b =>
            {
                var inBucket = days[r.UserId].Where(d => d.Date >= b.From && d.Date <= b.To).ToList();
                var minutes = byUser[r.UserId].Where(e => e.Date >= b.From && e.Date <= b.To).Sum(e => e.Minutes);
                var available = inBucket.Sum(d => d.AvailableMinutes);
                return new TimeUtilisationCell(b.From, b.To, minutes, available, inBucket.Sum(d => d.UnavailableMinutes),
                    inBucket.Count > 0 && inBucket.All(d => d.Holiday is not null),
                    Availability.UtilisationRules.UnderTarget(minutes, available, targetPercent));
            }).ToList();
            return new TimeUtilisationGridRow(r.UserId, r.Name, cells, cells.Sum(c => c.Minutes), cells.Sum(c => c.AvailableMinutes));
        }).ToList();

        var (size, buckets) = Buckets(from, to);
        var grid = Grid(buckets);
        var (pdfSize, pdfBuckets) = Buckets(from, to, PdfMaxDailyDays);
        return new TimeUtilisationReport(from, to, summary, personRows, taskRows, options)
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
            // Each bucket's gaps and excess are each person's own in it, added up, as the grid's cells hold them.
            Buckets = buckets.Select((b, i) => b with
            {
                Minutes = picked.Where(e => e.Date >= b.From && e.Date <= b.To).Sum(e => e.Minutes),
                AvailableMinutes = grid.Sum(g => g.Cells[i].AvailableMinutes),
                UnloggedMinutes = grid.Sum(g => Math.Max(0, g.Cells[i].AvailableMinutes - g.Cells[i].Minutes)),
                OverMinutes = grid.Sum(g => Math.Max(0, g.Cells[i].Minutes - g.Cells[i].AvailableMinutes))
            }).ToList(),
            TargetPercent = targetPercent,
            GridBucketSize = size,
            Grid = grid,
            PdfGridBucketSize = pdfSize,
            PdfGrid = pdfSize == size ? grid : Grid(pdfBuckets),
            // Named only when they are in the report: employed in the window or with time in it.
            Person = personId is Guid chosen && inReport.Contains(chosen) ? people[chosen] : null
        };
    }

    /// <summary>A column per day for a window up to this many days long, both ends included.</summary>
    public const int MaxDailyDays = 62;

    /// <summary>The PDF's timesheet grid fits about this many day columns on a landscape page, so it goes weekly beyond it.</summary>
    public const int PdfMaxDailyDays = 31;

    /// <summary>A column per ISO week for a window up to this many days long; a column per month beyond it.</summary>
    public const int MaxWeeklyDays = 366;

    /// <summary>
    /// The per-period chart's columns for a window, with no time in them yet: a day each up to <see cref="MaxDailyDays"/> days, an
    /// ISO week each (Monday to Sunday) up to <see cref="MaxWeeklyDays"/>, a calendar month each beyond. The first and last are
    /// clipped to the window, so every day of it is in exactly one.
    /// </summary>
    public static (TimeBucketSize Size, IReadOnlyList<TimeBucket> Buckets) Buckets(DateOnly from, DateOnly to, int maxDailyDays = MaxDailyDays)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        var size = days <= maxDailyDays ? TimeBucketSize.Day : days <= MaxWeeklyDays ? TimeBucketSize.Week : TimeBucketSize.Month;
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
