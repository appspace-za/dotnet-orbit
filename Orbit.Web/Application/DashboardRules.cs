using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The dashboard's three views (spec §6.9, decision 134): the viewer's own work, a department's and the company's. Which of them a
/// viewer reaches follows their task grants (<see cref="DashboardRules.Views"/>); the page switches between them.
/// </summary>
public enum DashboardView
{
    Mine,
    Department,
    Company
}

/// <summary>One person's open tasks in one status, as the dashboard's by-assignee query returns them (§6.9).</summary>
public sealed record PersonStatusCount(Guid UserId, string Name, TaskItemStatus Status, int Count);

/// <summary>One department's open tasks in one status, as the company view's by-department query returns them (§6.9).</summary>
public sealed record DepartmentStatusCount(Guid DepartmentId, string Name, TaskItemStatus Status, int Count);

/// <summary>
/// The dashboard's pure rules (spec §6.9, decisions 134 and 135): which views a viewer reaches, and the folding, labelling, bucketing
/// and percentages behind its charts. No database access, so they are unit-tested (DSH-001 to 007). The due figures' windows are
/// the Tasks list's Due filter (<see cref="DueWindowRules"/>).
/// </summary>
public static class DashboardRules
{
    /// <summary>The Last 14 days chart: days ending today.</summary>
    public const int TrendDays = 14;
    /// <summary>The by-assignee chart's bars before the rest fold into one; Unassigned comes on top of these.</summary>
    public const int MaxPeople = 8;
    /// <summary>A bar's label is shortened beyond this many characters, so the axis stays readable.</summary>
    public const int MaxLabel = 14;

    /// <summary>
    /// The views a viewer reaches, narrowest first (DSH-001). <b>My work</b> needs a user who edits tasks, or one whose tasks.view
    /// reaches only their own tasks: at Own, or at Department with no department of their own, where <see cref="Scoping.Tasks"/>
    /// gives them their own tasks and no department's. <b>Department</b> needs tasks.view at Department with a department of
    /// their own, or at All (who pick one). <b>Company</b> needs tasks.view at All. No tasks.view, no views.
    /// </summary>
    public static IReadOnlyList<DashboardView> Views(Actor actor)
    {
        var view = actor.ScopeOf(Permission.TasksView);
        if (view == PermissionScope.None) return [];
        var ownOnly = view == PermissionScope.Own || (view == PermissionScope.Department && actor.DepartmentId is null);
        var views = new List<DashboardView>(3);
        if (actor.UserId is not null && (actor.Has(Permission.TasksEdit) || ownOnly)) views.Add(DashboardView.Mine);
        if (view == PermissionScope.All || (view == PermissionScope.Department && actor.DepartmentId is not null)) views.Add(DashboardView.Department);
        if (view == PermissionScope.All) views.Add(DashboardView.Company);
        return views;
    }

    /// <summary>
    /// The view a viewer starts on before they have picked one: My work for someone who edits tasks, otherwise the widest view
    /// they reach; null when they reach none.
    /// </summary>
    public static DashboardView? DefaultView(Actor actor)
    {
        var views = Views(actor);
        if (views.Count == 0) return null;
        return actor.Has(Permission.TasksEdit) && views.Contains(DashboardView.Mine) ? DashboardView.Mine : views[^1];
    }

    /// <summary>
    /// The view the address names (<see cref="Key"/>, any case), or the default when it names none, names no view, or names one the
    /// viewer doesn't reach - a stale bookmark falls back rather than refusing, since the dashboard never refuses (DSH-002). Only a
    /// view's name counts: a number or a list of names is no view.
    /// </summary>
    public static DashboardView? Resolve(string? requested, Actor actor)
    {
        var name = requested?.Trim();
        foreach (var view in Views(actor))
            if (string.Equals(Key(view), name, StringComparison.OrdinalIgnoreCase)) return view;
        return DefaultView(actor);
    }

    /// <summary>A view's value in the address ("mine", "department", "company").</summary>
    public static string Key(DashboardView view) => view.ToString().ToLowerInvariant();

    /// <summary>Open tasks per priority, Low to Critical, zeros kept (DSH-003).</summary>
    public static IReadOnlyList<EnumCount<TaskPriority>> Priorities(IEnumerable<OpenFacet> facets) => Counts(facets, f => f.Priority);

    /// <summary>Open tasks per type, in the type list's order, zeros kept (DSH-003).</summary>
    public static IReadOnlyList<EnumCount<TaskType>> Types(IEnumerable<OpenFacet> facets) => Counts(facets, f => f.Type);

    /// <summary>The facets' tasks per status (DSH-003).</summary>
    public static StatusCounts Statuses(IEnumerable<OpenFacet> facets) => StatusCounts.From(facets.Select(f => (f.Status, f.Count)));

    private static IReadOnlyList<EnumCount<T>> Counts<T>(IEnumerable<OpenFacet> facets, Func<OpenFacet, T> key) where T : struct, Enum
    {
        var sums = facets.GroupBy(key).ToDictionary(g => g.Key, g => g.Sum(f => f.Count));
        return Enum.GetValues<T>().Select(v => new EnumCount<T>(v, sums.GetValueOrDefault(v))).ToList();
    }

    /// <summary>
    /// The by-assignee chart's bars (DSH-004): one per person, merged by id so two people of one name stay apart, most open work
    /// first, then by name, each labelled apart from the others (<see cref="Labels"/>). With more than <paramref name="max"/> people
    /// the last bar is "N others", the rest summed, so there are never more than <paramref name="max"/> bars of people. Unassigned
    /// work comes last, whatever its size, and only when there is some. A task several people share counts under each of them (§6.2.3).
    /// </summary>
    public static IReadOnlyList<LoadBar> People(IEnumerable<PersonStatusCount> rows, StatusCounts unassigned, int max = MaxPeople)
    {
        var people = rows.GroupBy(r => r.UserId)
            .Select(g => (Name: g.First().Name, Counts: StatusCounts.From(g.Select(r => (r.Status, r.Count)))))
            .OrderByDescending(p => p.Counts.Open).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var shown = people.Count > max ? Math.Max(0, max - 1) : people.Count;
        var kept = people.Take(shown).ToList();
        var labels = Labels(kept.Select(p => p.Name).ToList());
        var bars = kept.Select((p, i) => new LoadBar(labels[i], p.Counts)).ToList();
        if (people.Count > shown)
        {
            var rest = people.Skip(shown).ToList();
            bars.Add(new LoadBar($"{rest.Count} others", rest.Aggregate(StatusCounts.None, (sum, p) => sum.Plus(p.Counts))));
        }
        if (unassigned.Open > 0) bars.Add(new LoadBar("Unassigned", unassigned));
        return bars;
    }

    /// <summary>The company view's by-department bars (DSH-005): one per department with open work, by name, labelled apart.</summary>
    public static IReadOnlyList<LoadBar> Departments(IEnumerable<DepartmentStatusCount> rows)
    {
        var departments = ByDepartment(rows);
        var labels = Labels(departments.Select(d => d.Name).ToList());
        return departments.Select((d, i) => new LoadBar(labels[i], d.Counts)).ToList();
    }

    /// <summary>
    /// The Company view's sprint by department (DSH-005): each department's done tasks of its sprint tasks, by name. Cancelled
    /// tasks are left out, as from the sprint's Work done (decision 135), and so is a department whose sprint tasks were all cancelled.
    /// </summary>
    public static IReadOnlyList<DepartmentSprintProgress> SprintDepartments(IEnumerable<DepartmentStatusCount> rows) =>
        ByDepartment(rows).Where(d => d.Counts.NotCancelled > 0)
            .Select(d => new DepartmentSprintProgress(d.Name, d.Counts.NotCancelled, d.Counts.Done))
            .ToList();

    private static List<(string Name, StatusCounts Counts)> ByDepartment(IEnumerable<DepartmentStatusCount> rows) =>
        rows.GroupBy(r => r.DepartmentId)
            .Select(g => (Name: g.First().Name, Counts: StatusCounts.From(g.Select(r => (r.Status, r.Count)))))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Bar labels that tell the bars apart (DSH-004): each name shortened (<see cref="ShortName"/>), and when two come out the same
    /// - two people of one name, or names alike in their first letters - the later ones numbered in bar order, "Sam", "Sam (2)",
    /// still within <paramref name="max"/> characters.
    /// </summary>
    public static IReadOnlyList<string> Labels(IReadOnlyList<string> names, int max = MaxLabel)
    {
        var seen = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var labels = new List<string>(names.Count);
        foreach (var name in names)
        {
            var label = ShortName(name, max);
            var n = seen[label] = seen.GetValueOrDefault(label) + 1;
            if (n == 1)
            {
                labels.Add(label);
                continue;
            }
            var suffix = $" ({n})";
            labels.Add(ShortName(name, Math.Max(2, max - suffix.Length)) + suffix);
        }
        return labels;
    }

    /// <summary>The first instant of the Last 14 days chart, midnight UTC (dates in Orbit are UTC, §6.12).</summary>
    public static DateTime TrendStartUtc(DateOnly today, int days = TrendDays) =>
        today.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// Tasks created and tasks done per day (DSH-006): <paramref name="days"/> days ending today, oldest first, every day present,
    /// each timestamp counted on its UTC date and ignored outside the window.
    /// </summary>
    public static IReadOnlyList<DayCount> Daily(IEnumerable<DateTime> createdUtc, IEnumerable<DateTime> doneUtc, DateOnly today, int days = TrendDays)
    {
        var first = today.AddDays(-(days - 1));
        var created = Bucket(createdUtc, first, today);
        var done = Bucket(doneUtc, first, today);
        return Enumerable.Range(0, days).Select(first.AddDays)
            .Select(d => new DayCount(d, created.GetValueOrDefault(d), done.GetValueOrDefault(d)))
            .ToList();
    }

    private static Dictionary<DateOnly, int> Bucket(IEnumerable<DateTime> utc, DateOnly first, DateOnly last) =>
        utc.Select(t => DateOnly.FromDateTime(t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t))
            .Where(d => d >= first && d <= last)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// How far through a sprint today is (DSH-007): day n of m, both ends counted, so the start date is day 1 and the end date
    /// is the last day; before the start it is day 0, after the end the last day.
    /// </summary>
    public static SprintDay SprintDayOf(DateOnly start, DateOnly end, DateOnly today)
    {
        var days = Math.Max(1, end.DayNumber - start.DayNumber + 1);
        return new SprintDay(Math.Clamp(today.DayNumber - start.DayNumber + 1, 0, days), days);
    }

    /// <summary>A bar's label: the name, shortened with "…" beyond <paramref name="max"/> characters.</summary>
    public static string ShortName(string name, int max = MaxLabel)
    {
        var s = name.Trim();
        return s.Length <= max ? s : s[..Math.Max(1, max - 1)].TrimEnd() + "…";
    }
}
