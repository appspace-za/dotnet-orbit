using System.Xml.Linq;
using Orbit.Application;
using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Components.Pages.Reports;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>Time utilisation's charts, drawn as SVG for the page and the PDF alike (spec §12).</summary>
public class TimeUtilisationChartsTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>The texts drawn, without the tooltips some carry (a label's full name in a child title).</summary>
    private static List<string> Texts(string svg) => XDocument.Parse(svg).Descendants(Svg + "text")
        .Select(t => t.Nodes().OfType<XText>().FirstOrDefault()?.Value ?? t.Value).ToList();

    private static string Width(string svg) => XDocument.Parse(svg).Root!.Attribute("width")!.Value;

    /// <summary>
    /// A report over <paramref name="from"/> to <paramref name="to"/>: <paramref name="projects"/> projects and
    /// <paramref name="people"/> people, each person logging on their own task, the n-th one n hours, every tenth on no project.
    /// With <paramref name="available"/>, everyone works the full-time week throughout, so the n-th person's utilisation is the n-th
    /// lowest.
    /// </summary>
    internal static TimeUtilisationReport Report(DateOnly from, DateOnly to, int people = 2, int projects = 1, int tasksEach = 1, bool available = true)
    {
        var projectFacts = Enumerable.Range(1, projects)
            .Select(i => new TimeUtilisationProjectFacts(Guid.NewGuid(), $"P-26-{i:00000}", $"Project {i}"))
            .ToDictionary(p => p.Id);
        var projectIds = projectFacts.Keys.ToList();
        var names = new Dictionary<Guid, ReportPerson>();
        var tasks = new Dictionary<Guid, TimeUtilisationTaskFacts>();
        var entries = new List<TimeUtilisationEntry>();
        var number = 0;
        var days = to.DayNumber - from.DayNumber + 1;
        for (var i = 1; i <= people; i++)
        {
            var person = Guid.NewGuid();
            names[person] = new ReportPerson(person, $"Person {i}", true);
            for (var j = 0; j < tasksEach; j++)
            {
                number++;
                var task = new TimeUtilisationTaskFacts(Guid.NewGuid(), $"T-26-{number:00000}", $"Task {number}", TaskItemStatus.InProgress,
                    (TaskType)(number % 8), (TaskSource)(number % 5), number % 10 == 0 ? null : projectIds[number % projects], [person],
                    number % 3 == 0 ? null : 120, i * 60, "IT", false);
                tasks[task.Id] = task;
                entries.Add(new TimeUtilisationEntry(task.Id, person, from.AddDays(number % days), i * 60));
            }
        }
        var fullTime = new WeekMinutes(480, 480, 480, 480, 480, 0, 0);
        var availability = available
            ? names.Keys.ToDictionary(id => id, id => new ReportAvailability(true, AvailabilityRules.Days(
                new PersonAvailabilityFacts(id, false, [new PatternFacts(Guid.NewGuid(), new DateOnly(2020, 1, 1), fullTime)], [],
                    [new EmploymentPeriod(new DateOnly(2020, 1, 1), null)]),
                new Dictionary<DateOnly, string>(), from, to)))
            : new Dictionary<Guid, ReportAvailability>();
        return TimeUtilisationRules.Build(from, to, entries, tasks, [], projectFacts, names, availability, 80, null);
    }

    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);

    /// <summary>RPT-051: five charts in order, the period chart across the top at 1280 with a 640 copy, the rest in pairs in the PDF.</summary>
    [Fact]
    public void Five_charts_in_order_with_the_period_chart_across_the_top()
    {
        var r = Report(From, To, people: 3, projects: 2);
        var charts = TimeUtilisationCharts.All(r);

        Assert.Equal(["Logged against available per day", "Utilisation by person", "Time by project", "Time by task type", "Time by task source"],
            charts.Select(c => c.Title));
        Assert.Equal([true, false, false, false, false], charts.Select(c => c.Wide));
        Assert.Equal(("1280", "640"), (Width(charts[0].Svg), Width(charts[0].NarrowSvg!)));
        Assert.All(charts.Skip(1), c => Assert.Null(c.NarrowSvg));
        Assert.All(charts, c => XDocument.Parse(c.Svg));
        Assert.Equal([1, 2], TimeUtilisationCharts.PdfSections(r).Select(s => s.PerRow));
        Assert.Equal([1, 4], TimeUtilisationCharts.PdfSections(r).Select(s => s.Items.Count));
    }

    /// <summary>RPT-051: the period chart's title follows the bucket.</summary>
    [Theory]
    [InlineData(31, "Logged against available per day")]
    [InlineData(100, "Logged against available per week")]
    [InlineData(400, "Logged against available per month")]
    public void The_period_chart_title_follows_the_bucket(int days, string title)
    {
        var from = new DateOnly(2026, 1, 1);
        var chart = TimeUtilisationCharts.Period(Report(from, from.AddDays(days - 1)));
        Assert.Equal(title, chart.Title);
        XDocument.Parse(chart.Svg);
    }

    /// <summary>
    /// RPT-051: past twelve, the projects chart keeps the eleven with the most time and gives the rest one bar; No project is last; a
    /// bar's tip reads its time and its share of the total.
    /// </summary>
    [Fact]
    public void Projects_fold_past_twelve_with_no_project_last()
    {
        var r = Report(From, To, people: 30, projects: 14);
        Assert.Equal(14, r.Projects.Count(p => p.Project is not null));

        var projects = Texts(TimeUtilisationCharts.Projects(r).Svg);
        Assert.Contains("3 other projects", projects);
        Assert.True(projects.IndexOf("No project") > projects.IndexOf("3 other projects"));
        Assert.Contains("19 others", Texts(TimeUtilisationCharts.People(r).Svg));
    }

    /// <summary>
    /// RPT-060: the period chart stacks logged, unlogged and over with a legend; Utilisation by person puts the lowest first, keeps
    /// the eleven lowest past twelve and gives the rest one bar, each tip reading logged of available and the percentage.
    /// </summary>
    [Fact]
    public void The_period_and_person_charts_stack_logged_unlogged_and_over()
    {
        var r = Report(From, To, people: 15);
        var period = Texts(TimeUtilisationCharts.Period(r).Svg);
        Assert.Contains("Logged", period);
        Assert.Contains("Unlogged", period);
        Assert.Contains("Over", period);

        var people = Texts(TimeUtilisationCharts.People(r).Svg);
        Assert.True(people.IndexOf("Person 1") < people.IndexOf("Person 2"));
        Assert.Contains("Person 11", people);
        Assert.DoesNotContain("Person 12", people);
        Assert.Contains("4 others", people);

        // Person 2 logged 2h of October's 22 working days of 8h (176h): 1%.
        Assert.Contains("2h of 176h · 1%", Texts(TimeUtilisationCharts.People(Report(From, To, people: 2)).Svg));
        Assert.Contains("1h · no availability", Texts(TimeUtilisationCharts.People(Report(From, To, people: 2, available: false)).Svg));
    }

    /// <summary>RPT-051: types and sources are named as the app names them, and never fold.</summary>
    [Fact]
    public void Types_and_sources_are_named_and_never_fold()
    {
        var r = Report(From, To, people: 40);
        var types = Texts(TimeUtilisationCharts.Types(r).Svg);
        Assert.Contains("Meeting", types);
        Assert.Contains("Problem", types);
        var sources = Texts(TimeUtilisationCharts.Sources(r).Svg);
        Assert.Contains("Claude", sources);
        Assert.Contains("Nagios", sources);
        Assert.DoesNotContain(sources, s => s == "Api");
    }

    /// <summary>
    /// RPT-051: a large report's PDF builds - 40 people, 200 tasks and 30 projects over 400 days (monthly) - its charts folded to fit
    /// a page, which QuestPDF needs of a picture. So does an empty one.
    /// </summary>
    [Fact]
    public void A_large_reports_pdf_builds()
    {
        var from = new DateOnly(2025, 1, 1);
        var report = Report(from, from.AddDays(399), people: 40, projects: 30, tasksEach: 5);
        Assert.Equal((200, TimeBucketSize.Month), (report.Tasks.Count, report.BucketSize));

        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var pdf = Orbit.Reporting.ReportPdfBuilder.Build("Time utilisation", "2025-01-01 to 2026-02-04",
            [.. ReportPdfEndpoint.TimeUtilisationSections(report)], landscape: true);
        Assert.NotEmpty(pdf);
        Assert.NotEmpty(Orbit.Reporting.ReportPdfBuilder.Build("Time utilisation", "Nothing", [.. ReportPdfEndpoint.TimeUtilisationSections(Report(From, To, people: 0))],
            landscape: true));
    }

    /// <summary>RPT-051: the PDF's sections: the charts, then the summary, By person and By task, each task naming its people.</summary>
    [Fact]
    public void The_pdf_has_the_charts_then_the_summary_people_and_tasks()
    {
        var report = Report(From, To, people: 2);
        var sections = ReportPdfEndpoint.TimeUtilisationSections(report).ToList();
        Assert.Equal(2, sections.OfType<Orbit.Reporting.ReportPdfBuilder.Charts>().Count());
        var tables = sections.OfType<Orbit.Reporting.ReportPdfBuilder.Table>().ToList();
        Assert.Equal(["Summary", "By person", "Timesheet by day", "By task"], tables.Select(t => t.Caption?.Split(" (")[0]));
        var grid = tables[2];
        Assert.Equal(7f, grid.FontSize);
        Assert.Equal(["Person", .. Enumerable.Range(0, 31).Select(i => From.AddDays(i).ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture)), "Total"], grid.Headers);
        var byTask = tables[3];
        Assert.Equal("People", byTask.Headers[^1]);
        Assert.Contains("Person 2 2h (100%) · assignee", byTask.Rows[0][^1]);
        Assert.Equal("Total", byTask.Rows[^1][0]);
    }

    /// <summary>RPT-051: with nobody in the report, every chart says so; the period and person charts say there is no availability or time.</summary>
    [Fact]
    public void Every_chart_says_so_when_nothing_was_logged()
    {
        var charts = TimeUtilisationCharts.All(Report(From, To, people: 0));
        Assert.All(charts.Take(2), c => Assert.Contains(TimeUtilisationCharts.EmptyAvailability, c.Svg));
        Assert.All(charts.Skip(2), c => Assert.Contains(TimeUtilisationCharts.Empty, c.Svg));
    }

    /// <summary>RPT-051: a share reads as a whole percentage, "&lt;1%" above nothing that rounds to 0, and "-" of nothing.</summary>
    [Theory]
    [InlineData(24, 100, "24%")]
    [InlineData(1, 1000, "<1%")]
    [InlineData(0, 100, "0%")]
    [InlineData(5, 0, "-")]
    [InlineData(2, 3, "67%")]
    public void A_share_reads_as_a_whole_percentage(int part, int whole, string text) =>
        Assert.Equal(text, ReportText.ShareText(part, whole));
}
