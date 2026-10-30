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

    /// <summary>
    /// RPT-032: the sprint report's seven charts are well-formed whatever the report holds, an empty one included: Planned work outcome
    /// across the top, then by project, by person and per day, tasks on the left and time on the right.
    /// </summary>
    [Fact]
    public void Sprint_charts_are_well_formed()
    {
        var sprint = new SprintFacts(Guid.NewGuid(), "Sprint <14>", null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30),
            SprintStatus.Active, null);
        var alice = Guid.NewGuid();
        var task = new SprintTaskFacts(new TaskTimeFacts(Guid.NewGuid(), "T-26-00001", "Fix & ship", TaskItemStatus.Done, [alice], 60, 90),
            null, sprint.Id, false, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), 90);
        var report = SprintReportRules.Build(sprint, [task], [new(task.Task.Id, alice, 90, 90)], [new(task.Task.Id, sprint.StartDate, 90)], [],
            new Dictionary<Guid, SprintProjectFacts>(), [], new Dictionary<Guid, string>(), new HashSet<Guid>(),
            new Dictionary<Guid, ReportPerson> { [alice] = new(alice, "Al & Co", true) }, null, null, DateTime.UtcNow);

        foreach (var r in new[] { report, SprintStatusReport.Empty(DateTime.UtcNow) })
        {
            var charts = SprintCharts.All(r);
            Assert.Equal(
                ["Planned work outcome", "Tasks by project", "Time by project", "Tasks by person", "Time by person", "Tasks per day", "Time per day"],
                charts.Select(c => c.Title));
            Assert.Equal([true, false, false, false, false, false, false], charts.Select(c => c.Wide));
            Assert.All(charts, c => XDocument.Parse(c.Svg));
            Assert.Equal([1, 2], SprintCharts.PdfSections(r).Select(s => s.PerRow)); // the outcome across the page, then pairs
            Assert.Equal([1, 6], SprintCharts.PdfSections(r).Select(s => s.Items.Count));
            // The wide chart comes at half width too, for the page's one-column layout; the others don't need to.
            static string Width(string svg) => XDocument.Parse(svg).Root!.Attribute("width")!.Value;
            Assert.Equal(("1280", "640"), (Width(charts[0].Svg), Width(charts[0].NarrowSvg!)));
            Assert.All(charts.Skip(1), c => Assert.Null(c.NarrowSvg));
        }
        Assert.Contains("Al & Co", Texts(XDocument.Parse(SprintCharts.People(report).Svg)));
    }

    /// <summary>
    /// RPT-037: with Backlog tasks worked on, the charts are still well-formed and call them plain "Backlog" - a bar on the outcome
    /// chart, a third series on the time charts, cyan - and a sprint without any shows no Backlog series at all. The projects charts
    /// count a Backlog task on its own project (RPT-042), so they have no Backlog bar.
    /// </summary>
    [Fact]
    public void Sprint_charts_show_backlog_work_as_backlog()
    {
        var sprint = new SprintFacts(Guid.NewGuid(), "Sprint 14", null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30),
            SprintStatus.Active, null);
        var alice = Guid.NewGuid();
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var planned = new SprintTaskFacts(new TaskTimeFacts(Guid.NewGuid(), "T-26-00001", "Planned", TaskItemStatus.InProgress, [alice], null, 60),
            null, sprint.Id, false, created, null, 60);
        var backlog = new SprintTaskFacts(new TaskTimeFacts(Guid.NewGuid(), "T-26-00002", "Pulled in", TaskItemStatus.InProgress, [alice], null, 30),
            null, null, true, created, null, 30);
        SprintStatusReport Build(params SprintTaskFacts[] tasks) => SprintReportRules.Build(sprint, tasks,
            tasks.Select(t => new SprintPersonTime(t.Task.Id, alice, t.SprintMinutes, t.SprintMinutes)).ToList(),
            tasks.Select(t => new SprintDayTime(t.Task.Id, sprint.StartDate, t.SprintMinutes)).ToList(), [],
            new Dictionary<Guid, SprintProjectFacts>(), [], new Dictionary<Guid, string>(), new HashSet<Guid>(),
            new Dictionary<Guid, ReportPerson> { [alice] = new(alice, "Alice", true) }, null, null, DateTime.UtcNow);

        var withBacklog = Build(planned, backlog);
        var outcome = XDocument.Parse(SprintCharts.Outcome(withBacklog).Svg);
        Assert.Contains("Backlog", Texts(outcome));
        Assert.DoesNotContain("From the Backlog", Texts(outcome));
        Assert.DoesNotContain("Backlog", Texts(XDocument.Parse(SprintCharts.Projects(withBacklog).Svg)));
        foreach (var chart in new[] { SprintCharts.ProjectTime(withBacklog), SprintCharts.People(withBacklog), SprintCharts.Days(withBacklog) })
        {
            var doc = XDocument.Parse(chart.Svg);
            Assert.Contains("Backlog", Texts(doc)); // the legend
            var mark = Assert.Single(Marks(doc), m => m.Attribute("class")!.Value.Contains("oc-backlog"));
            Assert.Equal("#0891b2", mark.Attribute("fill")!.Value);
        }
        Assert.Contains("33% unplanned", SprintCharts.People(withBacklog).Svg); // 30 of 90
        Assert.Contains("33% unplanned", SprintCharts.ProjectTime(withBacklog).Svg); // both tasks on no project

        var without = Build(planned);
        foreach (var chart in SprintCharts.All(without))
            Assert.DoesNotContain("Backlog", Texts(XDocument.Parse(chart.Svg)));
    }

    /// <summary>
    /// RPT-038: with other sprints' tasks worked on, the charts are still well-formed and call them "Other sprint" - a bar on the
    /// outcome chart, a fourth series on the time charts, brown-gold - and a sprint without any shows no such series. The projects
    /// charts count such a task on its own project (RPT-042), so they have no Other sprint bar.
    /// </summary>
    [Fact]
    public void Sprint_charts_show_other_sprint_work_as_other_sprint()
    {
        var sprint = new SprintFacts(Guid.NewGuid(), "Sprint 14", null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30),
            SprintStatus.Active, null);
        var nextSprint = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        SprintTaskFacts Facts(string number, Guid? sprintId, bool inBacklog, int minutes) => new(
            new TaskTimeFacts(Guid.NewGuid(), number, "Task", TaskItemStatus.InProgress, [alice], null, minutes),
            null, sprintId, inBacklog, created, null, minutes);
        var planned = Facts("T-26-00001", sprint.Id, false, 60);
        var backlog = Facts("T-26-00002", null, true, 30);
        var ahead = Facts("T-26-00003", nextSprint, false, 30);
        SprintStatusReport Build(params SprintTaskFacts[] tasks) => SprintReportRules.Build(sprint, tasks,
            tasks.Select(t => new SprintPersonTime(t.Task.Id, alice, t.SprintMinutes, t.SprintMinutes)).ToList(),
            tasks.Select(t => new SprintDayTime(t.Task.Id, sprint.StartDate, t.SprintMinutes)).ToList(), [],
            new Dictionary<Guid, SprintProjectFacts>(), [], new Dictionary<Guid, string> { [nextSprint] = "Sprint 15" }, new HashSet<Guid> { nextSprint },
            new Dictionary<Guid, ReportPerson> { [alice] = new(alice, "Alice", true) }, null, null, DateTime.UtcNow);

        var withLater = Build(planned, backlog, ahead);
        var outcome = XDocument.Parse(SprintCharts.Outcome(withLater).Svg);
        Assert.Contains("Other sprint", Texts(outcome));
        Assert.DoesNotContain("From another sprint", Texts(outcome));
        Assert.DoesNotContain("Other sprint", Texts(XDocument.Parse(SprintCharts.Projects(withLater).Svg)));
        foreach (var chart in new[] { SprintCharts.ProjectTime(withLater), SprintCharts.People(withLater), SprintCharts.Days(withLater) })
        {
            var doc = XDocument.Parse(chart.Svg);
            Assert.Contains("Other sprint", Texts(doc)); // the legend, beside Planned, Not Planned and Backlog
            Assert.Contains("Backlog", Texts(doc));
            var mark = Assert.Single(Marks(doc), m => m.Attribute("class")!.Value.Contains("oc-other-sprint"));
            Assert.Equal("#a16207", mark.Attribute("fill")!.Value);
        }
        Assert.Contains("50% unplanned", SprintCharts.People(withLater).Svg); // 60 of 120

        foreach (var chart in SprintCharts.All(Build(planned, backlog)))
            Assert.DoesNotContain("Other sprint", Texts(XDocument.Parse(chart.Svg)));
    }

    /// <summary>
    /// RPT-040: Time by project stacks each project's time by kind of work, as Time by person does each person's - a Backlog task's
    /// time on its own project (RPT-042) - and Tasks by project has the same rows; Tasks by person each person's tasks by outcome,
    /// Unassigned last; Tasks per day each day's finished tasks by kind of work on whole-task ticks, and a message when nothing was
    /// finished.
    /// </summary>
    [Fact]
    public void The_new_charts_draw_time_by_project_tasks_by_person_and_tasks_per_day()
    {
        var sprint = new SprintFacts(Guid.NewGuid(), "Sprint 14", null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30),
            SprintStatus.Active, null);
        var alice = Guid.NewGuid();
        var project = new SprintProjectFacts(Guid.NewGuid(), "P-26-00001", "Alpha", "Ops");
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var done = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);
        SprintTaskFacts Facts(string number, Guid? projectId, Guid? sprintId, bool inBacklog, TaskItemStatus status, Guid[] assignees,
            int minutes, DateTime? completed = null) => new(
            new TaskTimeFacts(Guid.NewGuid(), number, "Task", status, assignees, null, minutes),
            projectId, sprintId, inBacklog, created, completed, minutes);
        var planned = Facts("T-26-00001", project.Id, sprint.Id, false, TaskItemStatus.Done, [alice], 60, done);
        var nobodys = Facts("T-26-00002", null, sprint.Id, false, TaskItemStatus.InProgress, [], 0);
        var pulledIn = Facts("T-26-00003", project.Id, null, true, TaskItemStatus.Done, [alice], 30, done);
        SprintTaskFacts[] tasks = [planned, nobodys, pulledIn];
        var report = SprintReportRules.Build(sprint, tasks,
            tasks.Where(t => t.SprintMinutes > 0).Select(t => new SprintPersonTime(t.Task.Id, alice, t.SprintMinutes, t.SprintMinutes)).ToList(),
            tasks.Where(t => t.SprintMinutes > 0).Select(t => new SprintDayTime(t.Task.Id, sprint.StartDate, t.SprintMinutes)).ToList(), [],
            new Dictionary<Guid, SprintProjectFacts> { [project.Id] = project }, [], new Dictionary<Guid, string>(), new HashSet<Guid>(),
            new Dictionary<Guid, ReportPerson> { [alice] = new(alice, "Alice", true) }, null, null, DateTime.UtcNow);

        var projectTime = XDocument.Parse(SprintCharts.ProjectTime(report).Svg);
        var marks = Marks(projectTime).Select(m => (Class: m.Attribute("class")!.Value, Title: m.Element(Svg + "title")!.Value)).ToList();
        // Alpha's bar holds its planned time and its Backlog task's; No project logged nothing, so has no mark.
        Assert.Equal(2, marks.Count);
        Assert.Contains(marks, m => m.Class.Contains("oc-planned") && m.Title.StartsWith("P-26-00001 Alpha"));
        Assert.Contains(marks, m => m.Class.Contains("oc-backlog") && m.Title.StartsWith("P-26-00001 Alpha"));
        Assert.Contains("Backlog", Texts(projectTime)); // the legend: planned and Backlog work
        Assert.Contains("33% unplanned", SprintCharts.ProjectTime(report).Svg); // 30 of Alpha's 90

        var projectTasks = XDocument.Parse(SprintCharts.Projects(report).Svg);
        Assert.Equal(["P-26-00001 Alpha · Done: 2 tasks", "No project · Left open: 1 task"],
            Marks(projectTasks).Select(m => m.Element(Svg + "title")!.Value));

        var personTasks = XDocument.Parse(SprintCharts.PeopleTasks(report).Svg);
        var labels = Texts(personTasks);
        Assert.True(labels.IndexOf("Alice") < labels.IndexOf("Unassigned"));
        Assert.Equal(["Alice · Done: 2 tasks", "Unassigned · Left open: 1 task"], Marks(personTasks).Select(m => m.Element(Svg + "title")!.Value));

        var tasksPerDay = XDocument.Parse(SprintCharts.DaysTasks(report).Svg);
        var dayMarks = Marks(tasksPerDay);
        Assert.Equal(2, dayMarks.Count); // the planned task and the Backlog one, both finished on the 21st
        Assert.Contains(dayMarks, m => m.Attribute("class")!.Value.Contains("oc-planned"));
        Assert.Contains(dayMarks, m => m.Attribute("class")!.Value.Contains("oc-backlog"));
        Assert.Equal(new SprintKindCounts(1, 0, 1, 0), report.Days[2].Finished);
        Assert.DoesNotContain(Texts(tasksPerDay), t => t.Contains('.')); // whole-task ticks

        var nothingFinished = SprintReportRules.Build(sprint, [nobodys], [], [], [], new Dictionary<Guid, SprintProjectFacts>(), [],
            new Dictionary<Guid, string>(), new HashSet<Guid>(), new Dictionary<Guid, ReportPerson>(), null, null, DateTime.UtcNow);
        Assert.Equal(["No tasks finished in this sprint."], Texts(XDocument.Parse(SprintCharts.DaysTasks(nothingFinished).Svg)));
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
        // The fullest the projects charts get: eleven projects, the other projects and No project - thirteen bars - with No project
        // holding a planned task and Not Planned, Backlog (RPT-037) and other-sprint (RPT-038) work, each kind on its project (RPT-042).
        var nextSprint = Guid.NewGuid();
        SprintTaskFacts Other(int n, Guid? sprintId, bool inBacklog) => new(
            new TaskTimeFacts(Guid.NewGuid(), $"T-26-{n:00000}", "Task", TaskItemStatus.InProgress, [people[0].Id], null, 0),
            null, sprintId, inBacklog, created, null, 30);
        tasks.AddRange([Other(90, sprint.Id, false), Other(91, null, false), Other(92, null, true), Other(93, nextSprint, false)]);
        var time = people.Select((p, i) => new SprintPersonTime(tasks[i % tasks.Count].Task.Id, p.Id, 60 + i, 60 + i)).ToList();
        var report = SprintReportRules.Build(sprint, tasks, time, [], [], projects.ToDictionary(p => p.Id), [],
            new Dictionary<Guid, string>(), new HashSet<Guid> { nextSprint }, people.ToDictionary(p => p.Id), null, null, DateTime.UtcNow);
        Assert.Equal(34, report.Rows.Count);
        Assert.True(report.Summary.HasBacklog);
        Assert.True(report.Summary.HasOtherSprint);
        Assert.Equal(40, report.People.Count);

        var byProject = XDocument.Parse(SprintCharts.Projects(report).Svg);
        Assert.Contains("19 other projects", Texts(byProject));
        var byPerson = XDocument.Parse(SprintCharts.People(report).Svg);
        Assert.Contains("29 others", Texts(byPerson));
        Assert.Contains("Person 40", Texts(byPerson)); // the most time keeps its bar
        Assert.DoesNotContain("Person 1", Texts(byPerson));
        var projectTime = XDocument.Parse(SprintCharts.ProjectTime(report).Svg);
        Assert.Contains("19 other projects", Texts(projectTime));
        var personTasks = XDocument.Parse(SprintCharts.PeopleTasks(report).Svg);
        Assert.Contains("29 others", Texts(personTasks)); // Time by person's people: the ten with time and no task too
        Assert.Contains("Person 40", Texts(personTasks));
        // Thirteen bars and a legend: well short of the ~27 rows that overflowed a page.
        foreach (var chart in new[] { byProject, byPerson, projectTime, personTasks })
            Assert.True(double.Parse(chart.Root!.Attribute("height")!.Value, CultureInfo.InvariantCulture) <= 480);

        // A picture QuestPDF can't fit on one page fails the whole document, so the PDF is the proof.
        QuestPDF.Settings.License = LicenseType.Community;
        var pdf = ReportPdfBuilder.Build("Sprint status", "Sprint 14", [.. SprintCharts.PdfSections(report)], landscape: true);
        Assert.NotEmpty(pdf);
    }

    /// <summary>
    /// RPT-036: the two charts of a pair keep the same projects, and the same people, in the same order: past
    /// <see cref="SprintCharts.MaxBars"/>, the ones with the most time in the sprint, though others have more tasks. A person with
    /// time and no task is on both, as is one with tasks and no time; Unassigned only on Tasks by person.
    /// </summary>
    [Fact]
    public void The_charts_of_a_pair_keep_the_same_rows()
    {
        var sprint = new SprintFacts(Guid.NewGuid(), "Sprint 14", null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30),
            SprintStatus.Active, null);
        var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var projects = Enumerable.Range(1, 14).Select(i => new SprintProjectFacts(Guid.NewGuid(), $"P-26-{i:00000}", $"Project {i}", "Ops")).ToList();
        var people = Enumerable.Range(1, 15).Select(i => new ReportPerson(Guid.NewGuid(), $"Person {i}", true)).ToList();
        var n = 0;
        SprintTaskFacts NewTask(SprintProjectFacts p, Guid[] assignees, int minutes) => new(
            new TaskTimeFacts(Guid.NewGuid(), $"T-26-{++n:00000}", "Task", TaskItemStatus.InProgress, assignees, null, minutes),
            p.Id, sprint.Id, false, created, null, minutes);
        // Projects 1-13 and people 1-13: two tasks and 10 minutes each. Project 14 and Person 14: one task and 10 hours.
        // Person 15: no task, 8 hours on Project 1's. One task is nobody's.
        List<SprintTaskFacts> tasks = [];
        List<SprintPersonTime> time = [];
        for (var i = 0; i < 13; i++)
            foreach (var minutes in new[] { 5, 5 })
            {
                var t = NewTask(projects[i], [people[i].Id], minutes);
                tasks.Add(t);
                time.Add(new(t.Task.Id, people[i].Id, minutes, minutes));
            }
        var big = NewTask(projects[13], [people[13].Id], 600);
        tasks.Add(big);
        time.Add(new(big.Task.Id, people[13].Id, 600, 600));
        time.Add(new(tasks[0].Task.Id, people[14].Id, 480, 480));
        tasks.Add(NewTask(projects[0], [], 0));
        var report = SprintReportRules.Build(sprint, tasks, time, [], [], projects.ToDictionary(p => p.Id), [],
            new Dictionary<Guid, string>(), new HashSet<Guid>(), people.ToDictionary(p => p.Id), null, null, DateTime.UtcNow);

        static List<string> Labels(SprintCharts.Chart c, string prefix) =>
            Texts(XDocument.Parse(c.Svg)).Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        var byProject = Labels(SprintCharts.Projects(report), "Project ");
        Assert.Equal(byProject, Labels(SprintCharts.ProjectTime(report), "Project "));
        Assert.Contains("Project 14", byProject); // the most time, though the fewest tasks
        Assert.Equal(11, byProject.Count);
        Assert.Contains("3 other projects", Texts(XDocument.Parse(SprintCharts.Projects(report).Svg)));
        Assert.Contains("3 other projects", Texts(XDocument.Parse(SprintCharts.ProjectTime(report).Svg)));

        var byPerson = Labels(SprintCharts.PeopleTasks(report), "Person ");
        Assert.Equal(byPerson, Labels(SprintCharts.People(report), "Person "));
        Assert.Equal(["Person 14", "Person 15"], byPerson.Take(2)); // by time in the sprint
        Assert.Equal(11, byPerson.Count);
        Assert.Contains("4 others", Texts(XDocument.Parse(SprintCharts.PeopleTasks(report).Svg)));
        Assert.Contains("4 others", Texts(XDocument.Parse(SprintCharts.People(report).Svg)));
        Assert.Contains("Unassigned", Texts(XDocument.Parse(SprintCharts.PeopleTasks(report).Svg)));
        Assert.DoesNotContain("Unassigned", Texts(XDocument.Parse(SprintCharts.People(report).Svg)));
    }
}
