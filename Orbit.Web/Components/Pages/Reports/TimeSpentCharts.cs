using System.Globalization;
using Orbit.Application.Models;
using Orbit.Reporting;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// Time spent's five charts (spec §12), as SVG (<see cref="SvgCharts"/>) for the page and the PDF alike: the time per day, week or
/// month across the top, then by project beside by person, and by task type beside by task source. One series, so no legend: each
/// bar's tip reads its time and its share of the whole (<see cref="ReportText.ShareText"/>). Every chart puts the most time first;
/// the projects and people charts keep the <see cref="SprintCharts.MaxBars"/> - 1 with the most and give the rest one bar
/// (<see cref="SprintCharts.Fold"/>), so a large department's chart still fits on one PDF page, which QuestPDF needs of a picture.
/// </summary>
public static class TimeSpentCharts
{
    /// <summary>The one series: the planned indigo of Sprint status, whose dark shade app.css already gives <c>oc-time</c>.</summary>
    public static readonly SvgCharts.Series Time = new("Time", "oc-time", "#4338ca");

    public const string Empty = "No time logged in the period.";

    private static readonly SvgCharts.Series[] One = [Time];

    /// <summary>The five charts, in the order the page and the PDF show them: the period across the top, then two pairs.</summary>
    public static IReadOnlyList<SprintCharts.Chart> All(TimeSpentReport r) => [Period(r), Projects(r), People(r), Types(r), Sources(r)];

    /// <summary>The charts as the PDF lays them out: the period across the page, then the others two to a row.</summary>
    public static IEnumerable<ReportPdfBuilder.Charts> PdfSections(TimeSpentReport r) => SprintCharts.PdfSections(All(r));

    /// <summary>
    /// The time in each day, ISO week or month of the window (<see cref="TimeSpentReport.BucketSize"/>), titled to match; a day's
    /// column greys a weekend. Drawn across the top (<see cref="SprintCharts.WideWidth"/>) and at half width for one column of charts.
    /// </summary>
    public static SprintCharts.Chart Period(TimeSpentReport r)
    {
        var title = r.BucketSize switch
        {
            TimeBucketSize.Day => "Time per day",
            TimeBucketSize.Week => "Time per week",
            _ => "Time per month"
        };
        var items = r.Buckets.Select(b => Column(b, r.BucketSize)).ToList();
        string Draw(double width) => SvgCharts.Columns(items, One, v => TimeFormat.Minutes((int)Math.Round(v)),
            v => (v / 60).ToString("0.#", CultureInfo.InvariantCulture) + "h", 60, Empty, title, minTickStep: 0.5, width: width);
        return new(title, Draw(SprintCharts.WideWidth), Wide: true, NarrowSvg: Draw(SprintCharts.HalfWidth));
    }

    /// <summary>Each project's time, most first, past twelve the rest together; then the tasks on no project, muted, last.</summary>
    public static SprintCharts.Chart Projects(TimeSpentReport r)
    {
        const string title = "Time by project";
        var (shown, rest) = SprintCharts.Fold(r.Projects.Where(p => p.Project is not null).ToList(), p => p.Minutes);
        var items = shown.Select(p => Bar(r, p.Project!.Name, p.Minutes, $"{p.Project.Number} {p.Project.Name}".Trim())).ToList();
        if (rest.Count > 0) items.Add(Bar(r, $"{rest.Count} other projects", rest.Sum(p => p.Minutes), muted: true));
        items.AddRange(r.Projects.Where(p => p.Project is null).Select(p => Bar(r, "No project", p.Minutes, muted: true)));
        return new(title, Draw(items, title));
    }

    /// <summary>Each person's time, most first, past twelve the rest together. The people who logged nothing aren't on it.</summary>
    public static SprintCharts.Chart People(TimeSpentReport r)
    {
        const string title = "Time by person";
        var (shown, rest) = SprintCharts.Fold(r.People.Where(p => p.Minutes > 0).ToList(), p => p.Minutes);
        var items = shown.Select(p => Bar(r, p.Name, p.Minutes)).ToList();
        if (rest.Count > 0) items.Add(Bar(r, $"{rest.Count} others", rest.Sum(p => p.Minutes), muted: true));
        return new(title, Draw(items, title));
    }

    /// <summary>Each task type's time, most first. Eight types at most, so it never folds.</summary>
    public static SprintCharts.Chart Types(TimeSpentReport r)
    {
        const string title = "Time by task type";
        return new(title, Draw(r.Types.Select(t => Bar(r, t.Type.ToString(), t.Minutes)).ToList(), title));
    }

    /// <summary>Each task source's time, most first, named as the app names them (<c>Api</c> is Claude). Five at most.</summary>
    public static SprintCharts.Chart Sources(TimeSpentReport r)
    {
        const string title = "Time by task source";
        return new(title, Draw(r.Sources.Select(s => Bar(r, Ui.SourceLabel(s.Source), s.Minutes)).ToList(), title));
    }

    private static string Draw(IReadOnlyList<SvgCharts.Item> items, string title) =>
        SvgCharts.HorizontalBars(items, One, v => TimeFormat.Minutes((int)Math.Round(v)), Empty, title);

    /// <summary>A bar: its time, and at its tip the time and its share of the whole (<c>2h · 67%</c>).</summary>
    private static SvgCharts.Item Bar(TimeSpentReport r, string label, int minutes, string? title = null, bool muted = false) =>
        new(label, [minutes], $"{TimeFormat.Minutes(minutes)} · {ReportText.ShareText(minutes, r.Summary.Minutes)}", title, muted);

    /// <summary>
    /// A column: a day by weekday and date (weekends greyed), a week by its first day in the window, a month by name and year; a
    /// week's or month's tooltip names the dates it covers.
    /// </summary>
    private static SvgCharts.Item Column(TimeBucket b, TimeBucketSize size)
    {
        var c = CultureInfo.InvariantCulture;
        return size switch
        {
            TimeBucketSize.Day => new(b.From.ToString("ddd", c) + "\n" + b.From.Day.ToString(c), [b.Minutes],
                Title: b.From.ToString("ddd yyyy-MM-dd", c), Muted: b.From.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday),
            TimeBucketSize.Week => new(b.From.Day.ToString(c) + "\n" + b.From.ToString("MMM", c), [b.Minutes], Title: Dates(b)),
            _ => new(b.From.ToString("MMM", c) + "\n" + b.From.ToString("yyyy", c), [b.Minutes], Title: Dates(b))
        };
    }

    private static string Dates(TimeBucket b) =>
        $"{b.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {b.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
