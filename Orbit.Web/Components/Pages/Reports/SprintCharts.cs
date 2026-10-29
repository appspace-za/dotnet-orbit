using System.Globalization;
using Orbit.Application.Models;
using Orbit.Reporting;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// Sprint status's four summary charts (spec §12), as SVG (<see cref="SvgCharts"/>) for the page and the PDF alike, so the two show
/// the same picture. Outcomes take the status colours (Done green, Left open amber, Cancelled grey); planned and Not Planned work
/// take two categorical colours, indigo and magenta, checked for colour-blind separation and contrast in both themes. app.css
/// gives the screen each theme's shade through the series' classes. The projects and people charts draw at most
/// <see cref="MaxBars"/> bars of their own, so a large sprint's chart still fits on one PDF page, which QuestPDF needs of a picture.
/// </summary>
public static class SprintCharts
{
    /// <summary>
    /// The most bars the projects and people charts give their projects and people: beyond it, the ones with the most keep a bar
    /// each and the rest share one. Fourteen bars at most in all (with No project and Not Planned) stay well within a landscape page.
    /// </summary>
    public const int MaxBars = 12;

    public static readonly SvgCharts.Series Done = new("Done", "oc-done", "#15803d");
    public static readonly SvgCharts.Series LeftOpen = new("Left open", "oc-left-open", "#b45309");
    public static readonly SvgCharts.Series Cancelled = new("Cancelled", "oc-cancelled", "#64748b");
    public static readonly SvgCharts.Series Planned = new("Planned", "oc-planned", "#4338ca");
    public static readonly SvgCharts.Series NotPlanned = new("Not Planned", "oc-not-planned", "#d55181");

    private static readonly SvgCharts.Series[] Outcomes = [Done, LeftOpen, Cancelled];
    private static readonly SvgCharts.Series[] Work = [Planned, NotPlanned];

    public sealed record Chart(string Title, string Svg);

    /// <summary>The four charts, in the order the page and the PDF show them.</summary>
    public static IReadOnlyList<Chart> All(SprintStatusReport r) => [Outcome(r), Projects(r), People(r), Days(r)];

    /// <summary>The planned tasks and the Not Planned ones, each by outcome.</summary>
    public static Chart Outcome(SprintStatusReport r)
    {
        const string title = "Planned work outcome";
        var s = r.Summary;
        SvgCharts.Item Bar(string label, SprintTaskCounts c) =>
            new(label, Counts(c), c.Total == 0 ? "none" : $"{Tasks(c.Total)} · {c.PercentDone}% done");
        return new(title, SvgCharts.HorizontalBars(
            [Bar("Planned", s.Planned), Bar(SprintGroupRow.NotPlannedName, s.NotPlanned)],
            Outcomes, v => Tasks((int)v), "No tasks in this sprint.", title));
    }

    /// <summary>
    /// Each project's planned tasks by outcome - the <see cref="MaxBars"/> with the most tasks, the rest together - then the planned
    /// tasks on no project, then the Not Planned work, set apart.
    /// </summary>
    public static Chart Projects(SprintStatusReport r)
    {
        const string title = "Tasks by project";
        static SvgCharts.Item Bar(SprintGroupRow g) => new(g.Name, Counts(g.Tasks), Total(g.Tasks),
            Title: g.Project is { } p ? $"{p.Number} {p.Name}".Trim() : g.Name, Muted: g.Kind != SprintGroupKind.Project,
            GapBefore: g.Kind == SprintGroupKind.NotPlanned);
        var (shown, rest) = Fold(r.Rows.Where(g => g.Kind == SprintGroupKind.Project).ToList(), g => g.Tasks.Total);
        var items = shown.Select(Bar).ToList();
        if (rest.Count > 0)
        {
            var sum = rest.Aggregate(SprintTaskCounts.None, (all, g) => all.Plus(g.Tasks));
            items.Add(new SvgCharts.Item($"{rest.Count} other projects", Counts(sum), Total(sum), Muted: true));
        }
        items.AddRange(r.Rows.Where(g => g.Kind != SprintGroupKind.Project).Select(Bar));
        return new(title, SvgCharts.HorizontalBars(items, Outcomes, v => Tasks((int)v), "No tasks in this sprint.", title));
    }

    /// <summary>
    /// The time each person logged in the sprint, on planned and on Not Planned work: the <see cref="MaxBars"/> who logged the most,
    /// the rest together.
    /// </summary>
    public static Chart People(SprintStatusReport r)
    {
        const string title = "Time by person";
        static SvgCharts.Item Bar(string name, int planned, int notPlanned, bool muted = false)
        {
            var total = planned + notPlanned;
            var share = total == 0 ? 0 : (int)Math.Round(notPlanned * 100.0 / total);
            return new(name, [planned, notPlanned], TimeFormat.Minutes(total) + (share > 0 ? $" · {share}% Not Planned" : ""), Muted: muted);
        }
        var (shown, rest) = Fold(r.People.Where(p => p.SprintMinutes > 0).ToList(), p => p.SprintMinutes);
        var items = shown.Select(p => Bar(p.Name, p.PlannedMinutes, p.NotPlannedMinutes)).ToList();
        if (rest.Count > 0)
            items.Add(Bar($"{rest.Count} others", rest.Sum(p => p.PlannedMinutes), rest.Sum(p => p.NotPlannedMinutes), muted: true));
        return new(title, SvgCharts.HorizontalBars(items, Work, v => TimeFormat.Minutes((int)Math.Round(v)),
            "No time logged in this sprint.", title));
    }

    /// <summary>The time logged on each day of the sprint, on planned and on Not Planned work; weekends greyed.</summary>
    public static Chart Days(SprintStatusReport r)
    {
        const string title = "Time per day";
        var items = r.Days.Select(d => new SvgCharts.Item(
                d.Date.ToString("ddd", CultureInfo.InvariantCulture) + "\n" + d.Date.Day.ToString(CultureInfo.InvariantCulture),
                [d.PlannedMinutes, d.NotPlannedMinutes], Title: d.Date.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture),
                Muted: d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday))
            .ToList();
        return new(title, SvgCharts.Columns(items, Work, v => TimeFormat.Minutes((int)Math.Round(v)),
            v => (v / 60).ToString("0.#", CultureInfo.InvariantCulture) + "h", 60, "No time logged in this sprint.", title,
            minTickStep: 0.5));
    }

    /// <summary>
    /// All of <paramref name="items"/> when there are at most <see cref="MaxBars"/>; otherwise the <see cref="MaxBars"/> - 1 largest,
    /// in their own order, to show, and the others to share the last bar.
    /// </summary>
    public static (IReadOnlyList<T> Shown, IReadOnlyList<T> Others) Fold<T>(IReadOnlyList<T> items, Func<T, double> size)
    {
        if (items.Count <= MaxBars) return (items, []);
        var kept = items.Select((item, i) => (Size: size(item), Index: i))
            .OrderByDescending(x => x.Size).ThenBy(x => x.Index)
            .Take(MaxBars - 1)
            .Select(x => x.Index)
            .ToHashSet();
        return (items.Where((_, i) => kept.Contains(i)).ToList(), items.Where((_, i) => !kept.Contains(i)).ToList());
    }

    private static double[] Counts(SprintTaskCounts c) => [c.Done, c.LeftOpen, c.Cancelled];

    private static string Total(SprintTaskCounts c) => c.Total.ToString(CultureInfo.InvariantCulture);

    private static string Tasks(int n) => n == 1 ? "1 task" : $"{n} tasks";
}
