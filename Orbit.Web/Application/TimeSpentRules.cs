using Orbit.Application.Models;

namespace Orbit.Application;

/// <summary>
/// The pure part of the Time spent report (§12): turns the time <c>ReportingService</c> reads in SQL into the report.
/// <list type="bullet">
/// <item>The window is <c>from</c> to <c>to</c>, both days included; an entry counts in it by its own date. Its project and
/// department are its task's, which the service applies in SQL.</item>
/// <item>The Person options are the people with time in the window, worked out before the Person filter applies; the filter then
/// keeps only that person's entries, and narrows the people who logged nothing to them.</item>
/// <item><i>To date</i> on a task is everyone's time on it, ever, whatever the filter: the estimate is for the whole task, so the
/// comparison is <see cref="TimeReportRules.Compare"/>'s, over each task once.</item>
/// <item>Every task stands alone: time on a subtask isn't rolled into its parent (§6.10).</item>
/// </list>
/// </summary>
public static class TimeSpentRules
{
    private const string Unknown = "Unknown";

    /// <summary>
    /// The report. <paramref name="entries"/> are the window's time, summed by task, person and day, every person's (the department
    /// and project applied); <paramref name="tasks"/> the facts of their tasks; <paramref name="toDate"/> each person's time on those
    /// tasks to date; <paramref name="people"/> the names of everyone in the entries and of <paramref name="covered"/>, the people
    /// listed when they logged nothing (decision 44). An entry outside the window, or on a task not in <paramref name="tasks"/>, is
    /// dropped.
    /// </summary>
    public static TimeSpentReport Build(
        DateOnly from, DateOnly to, IEnumerable<TimeSpentEntry> entries, IReadOnlyDictionary<Guid, TimeSpentTaskFacts> tasks,
        IEnumerable<TimeSpentPersonTime> toDate, IReadOnlyDictionary<Guid, TimeSpentProjectFacts> projects,
        IReadOnlyDictionary<Guid, ReportPerson> people, IEnumerable<Guid> covered, Guid? personId)
    {
        var inWindow = entries.Where(e => e.Date >= from && e.Date <= to && tasks.ContainsKey(e.TaskId)).ToList();
        string Name(Guid id) => people.TryGetValue(id, out var p) ? p.Name : Unknown;

        var options = inWindow.Select(e => e.UserId).Distinct()
            .Select(id => people.TryGetValue(id, out var p) ? p : new ReportPerson(id, Unknown, true))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var picked = personId is Guid only ? inWindow.Where(e => e.UserId == only).ToList() : inWindow;
        var listed = covered.Distinct().Where(id => personId is not Guid only || id == only).ToList();

        var byPerson = picked.GroupBy(e => e.UserId)
            .Select(g => new TimeSpentPersonRow(g.Key, Name(g.Key), g.Sum(e => e.Minutes), g.Select(e => e.TaskId).Distinct().Count()))
            .OrderByDescending(r => r.Minutes)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var withTime = byPerson.Select(r => r.UserId).ToHashSet();
        var nothing = listed.Where(id => !withTime.Contains(id))
            .Select(id => new TimeSpentPersonRow(id, Name(id), 0, 0))
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
                        return new TimeSpentTaskPerson(p.Key, Name(p.Key), minutes,
                            personToDate.TryGetValue((g.Key, p.Key), out var all) ? all : minutes, t.AssigneeIds.Contains(p.Key));
                    })
                    .OrderByDescending(p => p.Minutes)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                return new TimeSpentTaskRow(t.Id, t.Number, t.Title, t.Status, t.Type, ProjectOf(projects, t.ProjectId), t.EstimateMinutes,
                    g.Sum(e => e.Minutes), t.TotalMinutes, taskPeople);
            })
            .OrderByDescending(r => r.Minutes)
            .ThenBy(r => r.Number, StringComparer.Ordinal)
            .ToList();

        var worked = taskRows.Select(r => tasks[r.TaskId]).ToList();
        var summary = new TimeSpentSummary(picked.Sum(e => e.Minutes), byPerson.Count, nothing.Count, worked.Count,
            worked.Count(t => t.EstimateMinutes is null), TimeReportRules.Compare(worked.Select(t => t.AsTimeFacts())));

        return new TimeSpentReport(from, to, summary, [.. byPerson, .. nothing], taskRows, options);
    }

    /// <summary>A task's project: null for none, and a stand-in for an id that isn't found.</summary>
    private static TimeSpentProjectFacts? ProjectOf(IReadOnlyDictionary<Guid, TimeSpentProjectFacts> projects, Guid? id) =>
        id is not Guid p ? null : projects.TryGetValue(p, out var found) ? found : new TimeSpentProjectFacts(p, "", "Unknown project");
}
