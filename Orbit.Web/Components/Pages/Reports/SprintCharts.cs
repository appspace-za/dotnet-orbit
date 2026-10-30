using System.Globalization;
using Orbit.Application.Models;
using Orbit.Reporting;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// Sprint status's seven summary charts (spec §12), as SVG (<see cref="SvgCharts"/>) for the page and the PDF alike, so the two show
/// the same picture: Planned work outcome across the top, then by project, by person and per day, tasks on the left and time on the
/// right. Outcomes take the status colours (Done green, Left open amber, Cancelled grey). The kinds of work take four
/// categorical colours - planned indigo, Not Planned magenta, Backlog cyan and other sprint brown-gold - checked for colour-blind
/// separation (every pair, as a bar may set any two side by side), lightness and contrast in both themes. The brown-gold is close to
/// Left open's amber, but no chart has both: the outcome and the by-project and by-person task counts take the outcome colours, the
/// time charts and Tasks per day the work colours. app.css gives the screen each theme's shade through the series' classes. Each
/// kind's chart name and series come from <see cref="SprintWorkLook"/>. The projects and people charts draw at most
/// <see cref="MaxBars"/> bars of their own, so a large sprint's chart still fits on one PDF page, which QuestPDF needs of a picture;
/// the two charts of a pair keep the same projects or people, the ones with the most time in the sprint.
/// </summary>
public static class SprintCharts
{
    /// <summary>
    /// The most bars the projects and people charts give their projects and people: beyond it, the ones with the most time in the
    /// sprint keep a bar each and the rest share one. Sixteen bars at most in all (with No project, Not Planned, Backlog and Other
    /// sprint) stay well within a landscape page.
    /// </summary>
    public const int MaxBars = 12;

    /// <summary>The width the full-width chart is drawn at: twice a half-width chart's, so its text is the same size beside them.</summary>
    public const double WideWidth = 1280;

    /// <summary>The width a half-width chart is drawn at (<see cref="SvgCharts"/>' default).</summary>
    public const double HalfWidth = 640;

    /// <summary>The Backlog and other-sprint groups' names on the charts.</summary>
    public const string BacklogLabel = "Backlog", OtherSprintLabel = "Other sprint";

    public static readonly SvgCharts.Series Done = new("Done", "oc-done", "#15803d");
    public static readonly SvgCharts.Series LeftOpen = new("Left open", "oc-left-open", "#b45309");
    public static readonly SvgCharts.Series Cancelled = new("Cancelled", "oc-cancelled", "#64748b");
    public static readonly SvgCharts.Series Planned = new("Planned", "oc-planned", "#4338ca");
    public static readonly SvgCharts.Series NotPlanned = new("Not Planned", "oc-not-planned", "#d55181");
    public static readonly SvgCharts.Series Backlog = new(BacklogLabel, "oc-backlog", "#0891b2");
    public static readonly SvgCharts.Series OtherSprint = new(OtherSprintLabel, "oc-other-sprint", "#a16207");

    private static readonly SvgCharts.Series[] Outcomes = [Done, LeftOpen, Cancelled];

    /// <summary>A group's name on the charts: a project's or No project, or an unplanned group's chart name.</summary>
    public static string ChartName(SprintGroupRow g) => g.IsUnplanned ? SprintWorkLook.Of(g.Work).ChartName : g.Name;

    /// <summary>
    /// The kinds of work the time charts show, each a series: planned and the unplanned kinds the report shows
    /// (<see cref="SprintStatusSummary.Unplanned"/>), so a kind with no tasks has no empty legend entry.
    /// </summary>
    private static IReadOnlyList<SprintWorkKind> WorkKinds(SprintStatusReport r) => [SprintWorkKind.Planned, .. r.Summary.Unplanned];

    private static List<SvgCharts.Series> SeriesOf(IEnumerable<SprintWorkKind> kinds) =>
        kinds.Select(k => SprintWorkLook.Of(k).Series).ToList();

    /// <summary>
    /// A chart: its title and SVG; <see cref="Wide"/> takes the whole width of the page and the PDF, the others half. A wide chart
    /// also has <see cref="NarrowSvg"/>, the same chart at half width, which the page shows when the charts are one to a row, so its
    /// text still matches the others'.
    /// </summary>
    public sealed record Chart(string Title, string Svg, bool Wide = false, string? NarrowSvg = null);

    /// <summary>
    /// The seven charts, in the order the page and the PDF show them: Planned work outcome across the top, then pairs - tasks on the
    /// left, time on the right - by project, by person and per day.
    /// </summary>
    public static IReadOnlyList<Chart> All(SprintStatusReport r) =>
        [Outcome(r), Projects(r), ProjectTime(r), PeopleTasks(r), People(r), DaysTasks(r), Days(r)];

    /// <summary>The charts as the PDF lays them out: each run of wide charts one to a row, the others two to a row.</summary>
    public static IEnumerable<ReportPdfBuilder.Charts> PdfSections(SprintStatusReport r)
    {
        var charts = All(r);
        for (var i = 0; i < charts.Count;)
        {
            var wide = charts[i].Wide;
            var run = charts.Skip(i).TakeWhile(c => c.Wide == wide).ToList();
            yield return new ReportPdfBuilder.Charts(run.Select(c => new ReportPdfBuilder.Chart(c.Title, c.Svg)).ToList(), wide ? 1 : 2);
            i += run.Count;
        }
    }

    /// <summary>
    /// The planned tasks and each unplanned kind's, by outcome: Not Planned always, the Backlog and other sprints' tasks only when
    /// some were worked on. At most four bars, so it runs across the top (<see cref="WideWidth"/>), and is drawn at half width too.
    /// </summary>
    public static Chart Outcome(SprintStatusReport r)
    {
        const string title = "Planned work outcome";
        var s = r.Summary;
        SvgCharts.Item Bar(string label, SprintTaskCounts c) =>
            new(label, Counts(c), c.Total == 0 ? "none" : $"{Tasks(c.Total)} · {c.PercentDone}% done");
        List<SvgCharts.Item> items = [Bar("Planned", s.Planned.Tasks)];
        items.AddRange(s.Unplanned.Select(k => Bar(SprintWorkLook.Of(k).ChartName, s.Of(k).Tasks)));
        string Draw(double width) =>
            SvgCharts.HorizontalBars(items, Outcomes, v => Tasks((int)v), "No tasks in this sprint.", title, width);
        return new(title, Draw(WideWidth), Wide: true, NarrowSvg: Draw(HalfWidth));
    }

    /// <summary>
    /// Each project's planned tasks by outcome, then the planned tasks on no project, then the unplanned groups, set apart. The
    /// projects are Time by project's (<see cref="GroupBars"/>).
    /// </summary>
    public static Chart Projects(SprintStatusReport r)
    {
        const string title = "Tasks by project";
        var items = GroupBars(r, g => Counts(g.Tasks), g => Total(g.Tasks), rest =>
        {
            var sum = rest.Aggregate(SprintTaskCounts.None, (all, g) => all.Plus(g.Tasks));
            return (Counts(sum), Total(sum));
        });
        return new(title, SvgCharts.HorizontalBars(items, Outcomes, v => Tasks((int)v), "No tasks in this sprint.", title));
    }

    /// <summary>
    /// Each row's time in the sprint, in its kind of work's colour - the projects and No project planned, each unplanned group its
    /// own - with the rows as Tasks by project has them (<see cref="GroupBars"/>).
    /// </summary>
    public static Chart ProjectTime(SprintStatusReport r)
    {
        const string title = "Time by project";
        var kinds = WorkKinds(r);
        IReadOnlyList<double> Values(SprintWorkKind kind, int minutes) => kinds.Select(k => k == kind ? minutes : 0d).ToList();
        var items = GroupBars(r, g => Values(g.Work, g.SprintMinutes), g => TimeFormat.Minutes(g.SprintMinutes), rest =>
        {
            var minutes = rest.Sum(g => g.SprintMinutes);
            return (Values(SprintWorkKind.Planned, minutes), TimeFormat.Minutes(minutes));
        });
        return new(title, SvgCharts.HorizontalBars(items, SeriesOf(kinds), v => TimeFormat.Minutes((int)Math.Round(v)),
            "No time logged in this sprint.", title));
    }

    /// <summary>
    /// The rows' bars as both projects charts draw them, so the two keep the same rows: each project's - the <see cref="MaxBars"/>
    /// with the most time in the sprint, in their order, the rest together (<paramref name="others"/>) - then No project and the
    /// unplanned groups, muted, with a gap before the first unplanned one.
    /// </summary>
    private static List<SvgCharts.Item> GroupBars(
        SprintStatusReport r, Func<SprintGroupRow, IReadOnlyList<double>> values, Func<SprintGroupRow, string> tip,
        Func<IReadOnlyList<SprintGroupRow>, (IReadOnlyList<double> Values, string Tip)> others)
    {
        var firstUnplanned = r.Rows.FirstOrDefault(g => g.IsUnplanned);
        SvgCharts.Item Bar(SprintGroupRow g)
        {
            var name = ChartName(g);
            return new(name, values(g), tip(g), Title: g.Project is { } p ? $"{p.Number} {p.Name}".Trim() : name,
                Muted: g.Kind != SprintGroupKind.Project, GapBefore: ReferenceEquals(g, firstUnplanned));
        }
        var (shown, rest) = Fold(r.Rows.Where(g => g.Kind == SprintGroupKind.Project).ToList(), g => g.SprintMinutes);
        var items = shown.Select(Bar).ToList();
        if (rest.Count > 0)
        {
            var (restValues, restTip) = others(rest);
            items.Add(new SvgCharts.Item($"{rest.Count} other projects", restValues, restTip, Muted: true));
        }
        items.AddRange(r.Rows.Where(g => g.Kind != SprintGroupKind.Project).Select(Bar));
        return items;
    }

    /// <summary>
    /// Each person's tasks by outcome (<see cref="SprintStatusReport.PersonTasks"/>), every kind of work, a shared task for each of its
    /// assignees: the people as Time by person has them (<see cref="PeopleRows"/>), then Unassigned.
    /// </summary>
    public static Chart PeopleTasks(SprintStatusReport r)
    {
        const string title = "Tasks by person";
        static SvgCharts.Item Bar(string name, int done, int leftOpen, int cancelled, bool muted) =>
            new(name, [done, leftOpen, cancelled], (done + leftOpen + cancelled).ToString(CultureInfo.InvariantCulture), Muted: muted);
        var (shown, rest) = PeopleRows(r);
        var items = shown.Select(p => Bar(p.Name, p.Done, p.LeftOpen, p.Cancelled, false)).ToList();
        if (rest.Count > 0)
            items.Add(Bar($"{rest.Count} others", rest.Sum(p => p.Done), rest.Sum(p => p.LeftOpen), rest.Sum(p => p.Cancelled), true));
        items.AddRange(r.PersonTasks.Where(p => p.UserId is null && p.Total > 0)
            .Select(p => Bar(p.Name, p.Done, p.LeftOpen, p.Cancelled, true)));
        return new(title, SvgCharts.HorizontalBars(items, Outcomes, v => Tasks((int)v), "No tasks in this sprint.", title));
    }

    /// <summary>
    /// The time each person logged in the sprint, on each kind of work (<see cref="WorkKinds"/>): the people as Tasks by person has
    /// them (<see cref="PeopleRows"/>).
    /// </summary>
    public static Chart People(SprintStatusReport r)
    {
        const string title = "Time by person";
        var kinds = WorkKinds(r);
        SvgCharts.Item Bar(string name, SprintKindCounts minutes, bool muted = false)
        {
            var share = SprintReportShare.Percent(minutes.Unplanned, minutes.Total) ?? 0;
            return new(name, kinds.Select(k => (double)minutes.Of(k)).ToList(),
                TimeFormat.Minutes(minutes.Total) + (share > 0 ? $" · {share}% unplanned" : ""), Muted: muted);
        }
        var (shown, rest) = PeopleRows(r);
        var items = shown.Select(p => Bar(p.Name, p.Minutes)).ToList();
        if (rest.Count > 0)
            items.Add(Bar($"{rest.Count} others", SprintKindCounts.From(k => rest.Sum(p => p.Minutes.Of(k))), muted: true));
        return new(title, SvgCharts.HorizontalBars(items, SeriesOf(kinds), v => TimeFormat.Minutes((int)Math.Round(v)),
            "No time logged in this sprint.", title));
    }

    /// <summary>A person on the people charts: their tasks by outcome, every kind and cancelled ones too, and their time by kind.</summary>
    private sealed record ChartPerson(string Name, int Done, int LeftOpen, int Cancelled, SprintKindCounts Minutes)
    {
        public int Tasks => Done + LeftOpen + Cancelled;
    }

    /// <summary>
    /// The people both people charts show, so their rows are the same people in the same order: anyone with tasks or time in the
    /// sprint, in the people rows' order (by time in the sprint), then those assigned only to cancelled tasks, by name. Past
    /// <see cref="MaxBars"/>, the ones with the most time in the sprint keep a bar and the rest share one. Unassigned isn't among
    /// them: it has tasks and no time, so only Tasks by person shows it, last.
    /// </summary>
    private static (IReadOnlyList<ChartPerson> Shown, IReadOnlyList<ChartPerson> Others) PeopleRows(SprintStatusReport r)
    {
        var tasks = r.PersonTasks.Where(p => p.UserId is not null).ToDictionary(p => p.UserId!.Value);
        var time = r.People.Where(p => p.UserId is not null).ToDictionary(p => p.UserId!.Value);
        var people = r.People.Select(p => p.UserId).OfType<Guid>()
            .Concat(r.PersonTasks.Select(p => p.UserId).OfType<Guid>().Where(id => !time.ContainsKey(id)))
            .Select(id =>
            {
                var t = tasks.GetValueOrDefault(id);
                var m = time.GetValueOrDefault(id);
                return new ChartPerson(m?.Name ?? t!.Name, t?.Done ?? 0, t?.LeftOpen ?? 0, t?.Cancelled ?? 0, m?.Minutes ?? SprintKindCounts.None);
            })
            .Where(p => p.Tasks > 0 || p.Minutes.Total > 0)
            .ToList();
        return Fold(people, p => p.Minutes.Total);
    }

    /// <summary>
    /// The tasks finished on each day of the sprint (<c>SprintReportRules.FinishedOn</c>: each once, on the last day it was set to
    /// Done in the window), by kind of work (<see cref="WorkKinds"/>); weekends greyed.
    /// </summary>
    public static Chart DaysTasks(SprintStatusReport r)
    {
        const string title = "Tasks per day";
        var kinds = WorkKinds(r);
        var items = r.Days.Select(d => DayItem(d, kinds.Select(k => (double)d.Finished.Of(k)).ToList())).ToList();
        return new(title, SvgCharts.Columns(items, SeriesOf(kinds), v => Tasks((int)Math.Round(v)),
            v => v.ToString("0", CultureInfo.InvariantCulture), 1, "No tasks finished in this sprint.", title, minTickStep: 1));
    }

    /// <summary>The time logged on each day of the sprint, on each kind of work (<see cref="WorkKinds"/>); weekends greyed.</summary>
    public static Chart Days(SprintStatusReport r)
    {
        const string title = "Time per day";
        var kinds = WorkKinds(r);
        var items = r.Days.Select(d => DayItem(d, kinds.Select(k => (double)d.Minutes.Of(k)).ToList())).ToList();
        return new(title, SvgCharts.Columns(items, SeriesOf(kinds), v => TimeFormat.Minutes((int)Math.Round(v)),
            v => (v / 60).ToString("0.#", CultureInfo.InvariantCulture) + "h", 60, "No time logged in this sprint.", title,
            minTickStep: 0.5));
    }

    /// <summary>A day's column: its weekday and date for a label, its values, weekends greyed.</summary>
    private static SvgCharts.Item DayItem(SprintDayRow d, IReadOnlyList<double> values) => new(
        d.Date.ToString("ddd", CultureInfo.InvariantCulture) + "\n" + d.Date.Day.ToString(CultureInfo.InvariantCulture), values,
        Title: d.Date.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture),
        Muted: d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);

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
