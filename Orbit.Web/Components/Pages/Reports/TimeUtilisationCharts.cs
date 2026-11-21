using System.Globalization;
using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Reporting;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// Time utilisation's five charts (spec §12, decision 150), as SVG (<see cref="SvgCharts"/>) for the page and the PDF alike: logged
/// against available per day, week or month across the top, then utilisation by person beside time by project, and time by task type
/// beside time by task source. The first two stack each person's <see cref="Logged"/> time (up to what they had available), their
/// <see cref="Unlogged"/> gap and anything logged <see cref="Over"/> it, each person's own, so one person's overtime never hides
/// another's gap. The other three have one series, so no legend: each bar's tip reads its time and its share of the whole
/// (<see cref="ReportText.ShareText"/>). Past twelve bars the projects chart keeps the eleven with the most time and the people chart
/// the eleven lowest utilisations, and the rest share one bar (<see cref="SprintCharts.Fold"/>), so a large department's chart still
/// fits on one PDF page, which QuestPDF needs of a picture.
/// </summary>
public static class TimeUtilisationCharts
{
    /// <summary>The single-series charts' series: the planned indigo of Sprint status, whose dark shade app.css already gives <c>oc-time</c>.</summary>
    public static readonly SvgCharts.Series Time = new("Time", "oc-time", "#4338ca");
    public static readonly SvgCharts.Series Logged = new("Logged", "oc-time", "#4338ca");
    public static readonly SvgCharts.Series Unlogged = new("Unlogged", "oc-unlogged", "#a8abb8");
    public static readonly SvgCharts.Series Over = new("Over", "oc-over", "#d97706");

    public const string Empty = "No time logged in the period.";
    public const string EmptyAvailability = "No availability or time in the period.";

    private static readonly SvgCharts.Series[] One = [Time];
    private static readonly SvgCharts.Series[] Stack = [Logged, Unlogged, Over];

    /// <summary>The five charts, in the order the page and the PDF show them: the period across the top, then two pairs.</summary>
    public static IReadOnlyList<SprintCharts.Chart> All(TimeUtilisationReport r) => [Period(r), People(r), Projects(r), Types(r), Sources(r)];

    /// <summary>The charts as the PDF lays them out: the period across the page, then the others two to a row.</summary>
    public static IEnumerable<ReportPdfBuilder.Charts> PdfSections(TimeUtilisationReport r) => SprintCharts.PdfSections(All(r));

    /// <summary>
    /// Logged against available in each day, ISO week or month of the window (<see cref="TimeUtilisationReport.BucketSize"/>), titled to
    /// match; a column's height is the larger of the two. A day's column greys a weekend. Drawn across the top
    /// (<see cref="SprintCharts.WideWidth"/>) and at half width for one column of charts.
    /// </summary>
    public static SprintCharts.Chart Period(TimeUtilisationReport r)
    {
        var title = r.BucketSize switch
        {
            TimeBucketSize.Day => "Logged against available per day",
            TimeBucketSize.Week => "Logged against available per week",
            _ => "Logged against available per month"
        };
        var items = r.Buckets.Select(b => Column(b, r.BucketSize)).ToList();
        string Draw(double width) => SvgCharts.Columns(items, Stack, v => TimeFormat.Minutes((int)Math.Round(v)),
            v => (v / 60).ToString("0.#", CultureInfo.InvariantCulture) + "h", 60, EmptyAvailability, title, minTickStep: 0.5, width: width);
        return new(title, Draw(SprintCharts.WideWidth), Wide: true, NarrowSvg: Draw(SprintCharts.HalfWidth));
    }

    /// <summary>
    /// Each person's logged, unlogged and over time, lowest utilisation first - the people to follow up - and those with no availability
    /// last; past twelve, the eleven lowest keep theirs and the rest share one.
    /// </summary>
    public static SprintCharts.Chart People(TimeUtilisationReport r)
    {
        const string title = "Utilisation by person";
        var (shown, rest) = SprintCharts.Fold(r.People.ToList(), p => p.UtilisationPercent is int u ? -u : double.MinValue);
        var items = shown.Select(p => Person(p.Name, p.Minutes, p.AvailableMinutes, p.UnloggedMinutes, p.OverMinutes)).ToList();
        if (rest.Count > 0)
            items.Add(Person($"{rest.Count} others", rest.Sum(p => p.Minutes), rest.Sum(p => p.AvailableMinutes), rest.Sum(p => p.UnloggedMinutes),
                rest.Sum(p => p.OverMinutes), muted: true));
        return new(title, SvgCharts.HorizontalBars(items, Stack, v => TimeFormat.Minutes((int)Math.Round(v)), EmptyAvailability, title));
    }

    /// <summary>Each project's time, most first, past twelve the rest together; then the tasks on no project, muted, last.</summary>
    public static SprintCharts.Chart Projects(TimeUtilisationReport r)
    {
        const string title = "Time by project";
        var (shown, rest) = SprintCharts.Fold(r.Projects.Where(p => p.Project is not null).ToList(), p => p.Minutes);
        var items = shown.Select(p => Bar(r, p.Project!.Name, p.Minutes, $"{p.Project.Number} {p.Project.Name}".Trim())).ToList();
        if (rest.Count > 0) items.Add(Bar(r, $"{rest.Count} other projects", rest.Sum(p => p.Minutes), muted: true));
        items.AddRange(r.Projects.Where(p => p.Project is null).Select(p => Bar(r, "No project", p.Minutes, muted: true)));
        return new(title, Draw(items, title));
    }

    /// <summary>Each task type's time, most first. Eight types at most, so it never folds.</summary>
    public static SprintCharts.Chart Types(TimeUtilisationReport r)
    {
        const string title = "Time by task type";
        return new(title, Draw(r.Types.Select(t => Bar(r, t.Type.ToString(), t.Minutes)).ToList(), title));
    }

    /// <summary>Each task source's time, most first, named as the app names them (<c>Api</c> is Claude). Five at most.</summary>
    public static SprintCharts.Chart Sources(TimeUtilisationReport r)
    {
        const string title = "Time by task source";
        return new(title, Draw(r.Sources.Select(s => Bar(r, Ui.SourceLabel(s.Source), s.Minutes)).ToList(), title));
    }

    /// <summary>"32h of 40h · 80%", or "1h · no availability" with nothing to measure against.</summary>
    public static string Of(int minutes, int available) =>
        UtilisationRules.Percent(minutes, available) is int p
            ? $"{TimeFormat.Minutes(minutes)} of {TimeFormat.Minutes(available)} · {p}%"
            : $"{TimeFormat.Minutes(minutes)} · no availability";

    private static string Draw(IReadOnlyList<SvgCharts.Item> items, string title) =>
        SvgCharts.HorizontalBars(items, One, v => TimeFormat.Minutes((int)Math.Round(v)), Empty, title);

    /// <summary>A person's bar: logged up to available, the gap, the excess; its tip their logged of available.</summary>
    private static SvgCharts.Item Person(string name, int minutes, int available, int unlogged, int over, bool muted = false) =>
        new(name, [minutes - over, unlogged, over], Of(minutes, available), Muted: muted);

    /// <summary>A bar: its time, and at its tip the time and its share of the whole (<c>2h · 67%</c>).</summary>
    private static SvgCharts.Item Bar(TimeUtilisationReport r, string label, int minutes, string? title = null, bool muted = false) =>
        new(label, [minutes], $"{TimeFormat.Minutes(minutes)} · {ReportText.ShareText(minutes, r.Summary.Minutes)}", title, muted);

    /// <summary>
    /// A column: a day by weekday and date (weekends greyed), a week by its first day in the window, a month by name and year; its
    /// tooltip names the dates it covers and reads its logged of available.
    /// </summary>
    private static SvgCharts.Item Column(TimeBucket b, TimeBucketSize size)
    {
        var c = CultureInfo.InvariantCulture;
        double[] values = [b.Minutes - b.OverMinutes, b.UnloggedMinutes, b.OverMinutes];
        var of = Of(b.Minutes, b.AvailableMinutes);
        return size switch
        {
            TimeBucketSize.Day => new(b.From.ToString("ddd", c) + "\n" + b.From.Day.ToString(c), values,
                Title: $"{b.From.ToString("ddd yyyy-MM-dd", c)} · {of}", Muted: b.From.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday),
            TimeBucketSize.Week => new(b.From.Day.ToString(c) + "\n" + b.From.ToString("MMM", c), values, Title: $"{Dates(b)} · {of}"),
            _ => new(b.From.ToString("MMM", c) + "\n" + b.From.ToString("yyyy", c), values, Title: $"{Dates(b)} · {of}")
        };
    }

    private static string Dates(TimeBucket b) =>
        $"{b.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {b.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
