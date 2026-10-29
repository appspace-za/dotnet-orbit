using System.Globalization;
using System.Xml.Linq;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Components.Pages.Reports;
using Orbit.Data.Entities;
using Orbit.Reporting;
using QuestPDF.Infrastructure;

namespace Orbit.Tests.Reports;

/// <summary>The report charts drawn as SVG for the page and the PDF alike (spec §12 Sprint status).</summary>
public class SvgChartsTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly SvgCharts.Series A = new("Done", "oc-done", "#15803d");
    private static readonly SvgCharts.Series B = new("Left open", "oc-left-open", "#b45309");
    private static readonly SvgCharts.Series C = new("Cancelled", "oc-cancelled", "#64748b");

    private static string Count(double v) => v.ToString("0");

    /// <summary>The drawn marks: rects and paths with a tooltip (the legend's swatches have none).</summary>
    private static List<XElement> Marks(XDocument doc) =>
        doc.Descendants().Where(e => (e.Name == Svg + "rect" || e.Name == Svg + "path") && e.Element(Svg + "title") is not null).ToList();

    private static List<string> Texts(XDocument doc) => doc.Descendants(Svg + "text").Select(t => t.Nodes().OfType<XText>().First().Value).ToList();

    /// <summary>RPT-032: well-formed SVG, names escaped, a mark per segment that has a value, a legend only for two series or more.</summary>
    [Fact]
    public void Horizontal_bars_are_well_formed_and_escape_names()
    {
        var svg = SvgCharts.HorizontalBars(
            [new("R&D <ops> \"x\"", [3, 0, 1]), new("Support", [2, 2, 0])], [A, B, C], Count, "Nothing", "Tasks & more");
        var doc = XDocument.Parse(svg);
        Assert.Equal(Svg + "svg", doc.Root!.Name);
        Assert.Equal("Tasks & more", doc.Root.Attribute("aria-label")!.Value);
        Assert.Contains("R&D <ops> \"x\"", Texts(doc));

        var marks = Marks(doc);
        Assert.Equal(4, marks.Count); // the zero is left out
        Assert.Equal(["R&D <ops> \"x\" · Done: 3", "R&D <ops> \"x\" · Cancelled: 1", "Support · Done: 2", "Support · Left open: 2"],
            marks.Select(m => m.Element(Svg + "title")!.Value));
        Assert.All(marks, m => Assert.NotNull(m.Attribute("fill")));
        Assert.All(marks, m => Assert.StartsWith("oc-", m.Attribute("class")!.Value));
        Assert.Contains("Left open", Texts(doc)); // the legend

        var single = XDocument.Parse(SvgCharts.HorizontalBars([new("Only", [5])], [A], Count, "Nothing", "One"));
        Assert.DoesNotContain("Done", Texts(single));
        Assert.Contains("5", Texts(single)); // the total at the tip
    }

    /// <summary>RPT-032: segments run on from each other in proportion to their values, on one scale for all the bars.</summary>
    [Fact]
    public void Bar_segments_are_in_proportion_to_their_values()
    {
        SvgCharts.Item[] items = [new("One", [3, 0, 1]), new("Two", [2, 0, 0])];
        var layout = SvgCharts.LayoutBars(items, [A, B, C], ["4", "2"], 640);
        var one = layout.Rows[0].Segments;
        Assert.Equal([0, 2], one.Select(s => s.Series));
        Assert.Equal(layout.LabelWidth, one[0].Start);
        Assert.Equal(one[0].Start + one[0].Length, one[1].Start, 6);
        Assert.Equal(3.0, one[0].Length / one[1].Length, 6);
        Assert.Equal(2.0, (one[0].Length + one[1].Length) / layout.Rows[1].Segments[0].Length, 6);
        Assert.True(one[1].Start + one[1].Length <= layout.Width);
    }

    /// <summary>RPT-032: a value axis's ticks are clean numbers from zero up to the first at or above the largest value.</summary>
    [Fact]
    public void Ticks_are_clean_numbers()
    {
        Assert.Equal([0], SvgCharts.Ticks(0));
        Assert.Equal([0, 2, 4, 6], SvgCharts.Ticks(5));
        Assert.Equal([0, 50, 100], SvgCharts.Ticks(100));
        Assert.Equal([0, 120, 240, 360, 480], SvgCharts.Ticks(400, 60)); // minutes, in steps of 2 hours
        Assert.Equal([0, 12, 24, 36, 48], SvgCharts.Ticks(45, 60)); // a fifth of an hour...
        Assert.Equal([0, 30, 60], SvgCharts.Ticks(45, 60, 0.5)); // ...unless the step is at least half an hour
    }

    /// <summary>RPT-032: columns are well-formed, one stack per item; nothing to draw is a line of text and no marks.</summary>
    [Fact]
    public void Columns_draw_a_stack_per_item_and_empty_charts_say_so()
    {
        var svg = SvgCharts.Columns([new("Mon\n19", [60, 30]), new("Tue\n20", [0, 0], Muted: true), new("Wed\n21", [0, 90])],
            [A, B], v => $"{v}m", v => $"{v / 60}h", 60, "Nothing logged", "Days");
        var doc = XDocument.Parse(svg);
        Assert.Equal(3, Marks(doc).Count);
        Assert.Contains("Mon", Texts(doc));
        Assert.Contains("19", Texts(doc));

        var empty = XDocument.Parse(SvgCharts.Columns([new("Mon", [0, 0])], [A, B], v => "", v => "", 60, "Nothing logged", "Days"));
        Assert.Empty(Marks(empty));
        Assert.Equal(["Nothing logged"], Texts(empty));
        Assert.Equal(["Nothing logged"], Texts(XDocument.Parse(SvgCharts.HorizontalBars([], [A], Count, "Nothing logged", "None"))));
    }

    /// <summary>RPT-032: the sprint report's four charts are well-formed whatever the report holds, an empty one included.</summary>
    [Fact]
    public void Sprint_charts_are_well_formed()
    {
        var sprint = new SprintFacts(Guid.NewGuid(), "Sprint <14>", null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30),
            SprintStatus.Active, null);
        var alice = Guid.NewGuid();
        var task = new SprintTaskFacts(new TaskTimeFacts(Guid.NewGuid(), "T-26-00001", "Fix & ship", TaskItemStatus.Done, [alice], 60, 90),
            null, sprint.Id, false, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), 90);
        var report = SprintReportRules.Build(sprint, [task], [new(task.Task.Id, alice, 90, 90)], [new(task.Task.Id, sprint.StartDate, 90)], [],
            new Dictionary<Guid, SprintProjectFacts>(), [], new Dictionary<Guid, string>(),
            new Dictionary<Guid, ReportPerson> { [alice] = new(alice, "Al & Co", true) }, null, null, DateTime.UtcNow);

        foreach (var r in new[] { report, SprintStatusReport.Empty(DateTime.UtcNow) })
        {
            var charts = SprintCharts.All(r);
            Assert.Equal(["Planned work outcome", "Tasks by project", "Time by person", "Time per day"], charts.Select(c => c.Title));
            Assert.All(charts, c => XDocument.Parse(c.Svg));
        }
        Assert.Contains("Al & Co", Texts(XDocument.Parse(SprintCharts.People(report).Svg)));
    }

    /// <summary>
    /// RPT-036: past <see cref="SprintCharts.MaxBars"/>, the projects and people charts keep the ones with the most, in their own
    /// order, and give the rest one bar, so a large sprint's chart stays short enough for one PDF page - and its PDF builds.
    /// </summary>
    [Fact]
    public void A_large_sprint_folds_its_charts_and_its_pdf_builds()
    {
        int[] sizes = [5, 1, 9, 3, 7, 2, 8, 4, 6, 10, 11, 12, 13, 0, 14];
        var (shown, others) = SprintCharts.Fold(sizes, x => x);
        Assert.Equal([5, 9, 7, 8, 4, 6, 10, 11, 12, 13, 14], shown);
        Assert.Equal([1, 3, 2, 0], others);
        var (all, none) = SprintCharts.Fold(sizes[..SprintCharts.MaxBars], x => x);
        Assert.Equal(SprintCharts.MaxBars, all.Count);
        Assert.Empty(none);

        var sprint = new SprintFacts(Guid.NewGuid(), "Sprint 14", null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30),
            SprintStatus.Active, null);
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var projects = Enumerable.Range(1, 30).Select(i => new SprintProjectFacts(Guid.NewGuid(), $"P-26-{i:00000}", $"Project {i}", "Ops")).ToList();
        var people = Enumerable.Range(1, 40).Select(i => new ReportPerson(Guid.NewGuid(), $"Person {i}", true)).ToList();
        var tasks = projects.Select((p, i) => new SprintTaskFacts(
            new TaskTimeFacts(Guid.NewGuid(), $"T-26-{i:00000}", "Task", TaskItemStatus.InProgress, [people[i].Id], null, 0),
            p.Id, sprint.Id, false, created, null, 0)).ToList();
        var time = people.Select((p, i) => new SprintPersonTime(tasks[i % tasks.Count].Task.Id, p.Id, 60 + i, 60 + i)).ToList();
        var report = SprintReportRules.Build(sprint, tasks, time, [], [], projects.ToDictionary(p => p.Id), [],
            new Dictionary<Guid, string>(), people.ToDictionary(p => p.Id), null, null, DateTime.UtcNow);
        Assert.Equal(30, report.Rows.Count);
        Assert.Equal(40, report.People.Count);

        var byProject = XDocument.Parse(SprintCharts.Projects(report).Svg);
        Assert.Contains("19 other projects", Texts(byProject));
        var byPerson = XDocument.Parse(SprintCharts.People(report).Svg);
        Assert.Contains("29 others", Texts(byPerson));
        Assert.Contains("Person 40", Texts(byPerson)); // the most time keeps its bar
        Assert.DoesNotContain("Person 1", Texts(byPerson));
        foreach (var chart in new[] { byProject, byPerson })
            Assert.True(double.Parse(chart.Root!.Attribute("height")!.Value, CultureInfo.InvariantCulture) <= 450);

        // A picture QuestPDF can't fit on one page fails the whole document, so the PDF is the proof.
        QuestPDF.Settings.License = LicenseType.Community;
        var pdf = ReportPdfBuilder.Build("Sprint status", "Sprint 14",
            [new ReportPdfBuilder.Charts(SprintCharts.All(report).Select(c => new ReportPdfBuilder.Chart(c.Title, c.Svg)).ToList())],
            landscape: true);
        Assert.NotEmpty(pdf);
    }
}
