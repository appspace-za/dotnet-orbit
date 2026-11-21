using Orbit.Application;
using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>Time utilisation (spec §12).</summary>
public class TimeUtilisationRulesTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();
    private static readonly Guid Dan = Guid.NewGuid();
    private static readonly Guid Eve = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, ReportPerson> People = new Dictionary<Guid, ReportPerson>
    {
        [Alice] = new(Alice, "Alice", true),
        [Bob] = new(Bob, "Bob", true),
        [Carol] = new(Carol, "Carol", true),
        [Dan] = new(Dan, "Dan", true),
        [Eve] = new(Eve, "Eve", true)
    };

    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);
    private static readonly DateOnly Day = new(2026, 10, 15);

    private int _number;

    private TimeUtilisationTaskFacts Task(
        int? estimate = null, int total = 0, TaskItemStatus status = TaskItemStatus.InProgress, Guid[]? assignees = null,
        TaskType type = TaskType.Task, TaskSource source = TaskSource.Manual, Guid? projectId = null, string? number = null,
        string title = "Task", string department = "IT", bool restricted = false) =>
        new(Guid.NewGuid(), number ?? $"T-26-{++_number:00000}", title, status, type, source, projectId, assignees ?? [], estimate, total,
            department, restricted);

    private static TimeUtilisationEntry Entry(Guid user, TimeUtilisationTaskFacts task, int minutes, DateOnly? date = null) =>
        new(task.Id, user, date ?? Day, minutes);

    private static readonly WeekMinutes FullTime = new(480, 480, 480, 480, 480, 0, 0);

    /// <summary>A person's availability over the window from <see cref="AvailabilityRules"/>: employed throughout, on <paramref name="week"/>.</summary>
    private static ReportAvailability Avail(
        DateOnly from, DateOnly to, WeekMinutes? week = null, UnavailableFacts[]? off = null, IReadOnlyDictionary<DateOnly, string>? holidays = null) =>
        new(true, AvailabilityRules.Days(
            new PersonAvailabilityFacts(Guid.NewGuid(), false, [new PatternFacts(Guid.NewGuid(), new DateOnly(2020, 1, 1), week ?? FullTime)],
                off ?? [], [new EmploymentPeriod(new DateOnly(2020, 1, 1), null)]),
            holidays ?? new Dictionary<DateOnly, string>(), from, to));

    private static UnavailableFacts Leave(DateOnly day) => new(Guid.NewGuid(), day, day, null, UnavailableReason.Leave, DateTime.UtcNow);

    /// <summary>
    /// The report over <see cref="People"/> - everyone in the department scope (§6.3). <paramref name="availability"/> is each person's
    /// availability; without it, the people in <paramref name="employed"/> work the full-time week throughout and no one else has any.
    /// </summary>
    private static TimeUtilisationReport Run(
        IEnumerable<TimeUtilisationEntry> entries, IEnumerable<TimeUtilisationTaskFacts> tasks, IEnumerable<Guid>? employed = null,
        Guid? personId = null, IEnumerable<TimeUtilisationPersonTime>? toDate = null,
        IReadOnlyDictionary<Guid, TimeUtilisationProjectFacts>? projects = null, DateOnly? from = null, DateOnly? to = null,
        IReadOnlyDictionary<Guid, ReportPerson>? people = null, IReadOnlyDictionary<Guid, ReportAvailability>? availability = null,
        int target = 80)
    {
        var (f, t) = (from ?? From, to ?? To);
        availability ??= (employed ?? []).Distinct().ToDictionary(id => id, _ => Avail(f, t));
        return TimeUtilisationRules.Build(f, t, entries, tasks.ToDictionary(x => x.Id), toDate ?? [],
            projects ?? new Dictionary<Guid, TimeUtilisationProjectFacts>(), people ?? People, availability, target, personId);
    }

    /// <summary>RPT-044: entries on the first and last days count; a day outside either end doesn't.</summary>
    [Fact]
    public void Entries_on_the_first_and_last_days_count_and_those_outside_do_not()
    {
        var t = Task();
        var report = Run(
            [
                Entry(Alice, t, 60, new DateOnly(2026, 9, 30)),
                Entry(Alice, t, 30, new DateOnly(2026, 10, 1)),
                Entry(Alice, t, 45, new DateOnly(2026, 10, 31)),
                Entry(Alice, t, 90, new DateOnly(2026, 11, 1))
            ], [t]);

        Assert.Equal(75, report.Summary.Minutes);
        Assert.Equal(75, Assert.Single(report.Tasks).Minutes);
        Assert.Equal((From, To), (report.From, report.To));
    }

    /// <summary>
    /// RPT-045: the Person options are worked out before the person filter, which keeps only that person's time; To date stays
    /// everyone's. A person chosen but offered to nobody finds nothing.
    /// </summary>
    [Fact]
    public void Person_options_come_before_the_person_filter_which_keeps_only_their_time()
    {
        var t1 = Task(total: 300);
        var t2 = Task(total: 30);
        TimeUtilisationEntry[] entries = [Entry(Alice, t1, 120), Entry(Bob, t1, 60), Entry(Bob, t2, 30)];

        var report = Run(entries, [t1, t2], employed: [Carol], personId: Alice);
        Assert.Equal(["Alice", "Bob", "Carol"], report.PersonOptions.Select(p => p.Name));
        Assert.Equal(120, report.Summary.Minutes);
        var row = Assert.Single(report.Tasks);
        Assert.Equal((120, 300), (row.Minutes, row.TotalMinutes));
        Assert.Equal("Alice", Assert.Single(row.People).Name);

        var nobody = Run(entries, [t1, t2], personId: Dan);
        Assert.Equal(["Alice", "Bob"], nobody.PersonOptions.Select(p => p.Name));
        Assert.Equal(0, nobody.Summary.Minutes);
        Assert.Empty(nobody.Tasks);
        Assert.Null(nobody.Person);
    }

    /// <summary>
    /// RPT-045: the person the Person filter picked is named when they are in the report - they logged time in the window or were
    /// employed in it - though they logged nothing in it, so stepping through sprints for someone never shows them as Unknown; anyone
    /// else isn't named.
    /// </summary>
    [Fact]
    public void The_chosen_person_is_named_though_they_logged_nothing_in_the_window()
    {
        var t = Task();
        TimeUtilisationEntry[] entries = [Entry(Bob, t, 60)];

        Assert.Equal("Bob", Run(entries, [t], personId: Bob).Person?.Name);
        Assert.Equal("Alice", Run(entries, [t], employed: [Alice], personId: Alice).Person?.Name);
        Assert.Null(Run(entries, [t], employed: [Alice], personId: Eve).Person);
        Assert.Null(Run(entries, [t], employed: [Alice]).Person);
    }

    /// <summary>RPT-046: the summary counts people and tasks, unestimated tasks, and a shared task's estimate once.</summary>
    [Fact]
    public void Summary_counts_people_tasks_and_a_shared_task_once()
    {
        var shared = Task(estimate: 240, total: 300, assignees: [Alice, Bob]);
        var unestimated = Task(total: 90);
        var cancelled = Task(estimate: 60, total: 500, status: TaskItemStatus.Cancelled);
        var report = Run([Entry(Alice, shared, 60), Entry(Bob, shared, 60), Entry(Alice, unestimated, 30), Entry(Bob, cancelled, 15)],
            [shared, unestimated, cancelled]);

        var s = report.Summary;
        Assert.Equal((165, 2, 3, 1), (s.Minutes, s.People, s.Tasks, s.UnestimatedTasks));
        Assert.Equal((1, 240, 300, 1), (s.Estimate.EstimatedTasks, s.Estimate.EstimatedMinutes, s.Estimate.ActualMinutes, s.Estimate.OverTasks));
    }

    /// <summary>
    /// RPT-047: By person counts each person's tasks; the employed who logged nothing are zero rows, never doubled when they did log
    /// time; the summary counts each task once.
    /// </summary>
    [Fact]
    public void By_person_counts_tasks_and_the_total_counts_each_task_once()
    {
        var t1 = Task();
        var t2 = Task();
        var report = Run([Entry(Alice, t1, 60), Entry(Bob, t1, 60), Entry(Bob, t2, 60), Entry(Dan, t2, 60)], [t1, t2],
            employed: [Alice, Carol, Eve]);

        var bob = report.People.Single(p => p.Name == "Bob");
        var carol = report.People.Single(p => p.Name == "Carol");
        Assert.Equal((120, 2), (bob.Minutes, bob.Tasks));
        Assert.Equal((0, 0), (carol.Minutes, carol.Tasks));
        Assert.True(carol.LoggedNothing);
        Assert.False(bob.LoggedNothing);
        Assert.Equal((3, 2, 2), (report.Summary.People, report.Summary.LoggedNothing, report.Summary.Tasks));
        Assert.Single(report.People, p => p.Name == "Alice");
        Assert.Equal(5, report.People.Count);
    }

    /// <summary>
    /// RPT-047: with the Person filter, only that person is considered: an employed person with no time is one zero row; someone
    /// neither employed nor with time is nothing.
    /// </summary>
    [Fact]
    public void The_person_filter_narrows_who_logged_nothing_to_that_person()
    {
        var t1 = Task();
        var t2 = Task();
        TimeUtilisationEntry[] entries = [Entry(Alice, t1, 60), Entry(Bob, t1, 60), Entry(Bob, t2, 60), Entry(Dan, t2, 60)];

        var carol = Run(entries, [t1, t2], employed: [Alice, Carol, Eve], personId: Carol);
        Assert.Equal(["Carol"], carol.People.Select(p => p.Name));
        Assert.Equal((0, 1), (carol.Summary.People, carol.Summary.LoggedNothing));

        var dan = Run(entries, [t1, t2], employed: [Alice, Carol, Eve], personId: Dan);
        Assert.Equal(["Dan"], dan.People.Select(p => p.Name));
        Assert.Equal((1, 0), (dan.Summary.People, dan.Summary.LoggedNothing));

        var eve = Run(entries, [t1, t2], employed: [Alice, Carol], personId: Eve);
        Assert.Empty(eve.People);
        Assert.Equal(0, eve.Summary.LoggedNothing);
    }

    /// <summary>RPT-048: By task by time then number; each task's people by time then name, with their time to date and the assignee flag.</summary>
    [Fact]
    public void By_task_orders_by_time_then_number_with_each_tasks_people()
    {
        var t2 = Task(number: "T-26-00002", assignees: [Alice]);
        var t1 = Task(number: "T-26-00001");
        var t3 = Task(number: "T-26-00003");
        var report = Run([Entry(Bob, t1, 120), Entry(Bob, t2, 30), Entry(Alice, t2, 90), Entry(Alice, t3, 30)], [t1, t2, t3],
            toDate: [new(t2.Id, Alice, 200), new(t2.Id, Bob, 30)]);

        Assert.Equal(["T-26-00001", "T-26-00002", "T-26-00003"], report.Tasks.Select(t => t.Number));
        Assert.Equal([("Alice", 90, 200, true), ("Bob", 30, 30, false)],
            report.Tasks[1].People.Select(p => (p.Name, p.Minutes, p.ToDateMinutes, p.IsAssignee)));
        // No row of time to date for a person falls back to their time in the window.
        Assert.Equal(120, Assert.Single(report.Tasks[0].People).ToDateMinutes);
    }

    /// <summary>A task's project: its own, none, or an unknown one when the id isn't found.</summary>
    [Fact]
    public void A_tasks_project_is_its_own_none_or_unknown()
    {
        var p = new TimeUtilisationProjectFacts(Guid.NewGuid(), "P-26-00001", "Website");
        var onProject = Task(projectId: p.Id);
        var standalone = Task();
        var lost = Task(projectId: Guid.NewGuid());
        var report = Run([Entry(Alice, onProject, 30), Entry(Alice, standalone, 20), Entry(Alice, lost, 10)], [onProject, standalone, lost],
            projects: new Dictionary<Guid, TimeUtilisationProjectFacts> { [p.Id] = p });

        Assert.Equal(p, report.Tasks.Single(t => t.TaskId == onProject.Id).Project);
        Assert.Null(report.Tasks.Single(t => t.TaskId == standalone.Id).Project);
        Assert.Equal("Unknown project", report.Tasks.Single(t => t.TaskId == lost.Id).Project!.Name);
    }

    /// <summary>
    /// RPT-049: projects, types and sources with time, most first (ties in declared order), No project last whatever its time; each
    /// adds up to the total.
    /// </summary>
    [Fact]
    public void Projects_types_and_sources_are_ordered_by_time_and_add_up()
    {
        var p1 = new TimeUtilisationProjectFacts(Guid.NewGuid(), "P-26-00001", "Website");
        var p2 = new TimeUtilisationProjectFacts(Guid.NewGuid(), "P-26-00002", "Network");
        var a = Task(projectId: p1.Id, type: TaskType.Meeting, source: TaskSource.Manual);
        var b = Task(projectId: p2.Id, type: TaskType.Task, source: TaskSource.Nagios);
        var c = Task(type: TaskType.Request, source: TaskSource.Nagios);
        var d = Task(type: TaskType.Task, source: TaskSource.Manual);
        var report = Run([Entry(Alice, a, 60), Entry(Bob, b, 120), Entry(Alice, c, 60), Entry(Bob, d, 120)], [a, b, c, d],
            projects: new Dictionary<Guid, TimeUtilisationProjectFacts> { [p1.Id] = p1, [p2.Id] = p2 });

        Assert.Equal([(p2, 120), (p1, 60), (null, 180)], report.Projects.Select(p => (p.Project, p.Minutes)));
        Assert.Equal([(TaskType.Task, 240), (TaskType.Meeting, 60), (TaskType.Request, 60)], report.Types.Select(t => (t.Type, t.Minutes)));
        Assert.Equal([(TaskSource.Manual, 180), (TaskSource.Nagios, 180)], report.Sources.Select(s => (s.Source, s.Minutes)));
        Assert.Equal(360, report.Projects.Sum(p => p.Minutes));
        Assert.Equal(360, report.Types.Sum(t => t.Minutes));
        Assert.Equal(360, report.Sources.Sum(s => s.Minutes));
    }

    /// <summary>RPT-049: with no task on a project, there is no No project row.</summary>
    [Fact]
    public void No_project_has_a_row_only_with_time()
    {
        var p = new TimeUtilisationProjectFacts(Guid.NewGuid(), "P-26-00001", "Website");
        var t = Task(projectId: p.Id);
        var report = Run([Entry(Alice, t, 60)], [t], projects: new Dictionary<Guid, TimeUtilisationProjectFacts> { [p.Id] = p });
        Assert.Equal([p], report.Projects.Select(x => x.Project));
    }

    /// <summary>RPT-050: 62 days are daily, 63 and 366 weekly, 367 monthly.</summary>
    [Theory]
    [InlineData("2026-01-01", "2026-03-03", TimeBucketSize.Day, 62)]
    [InlineData("2026-01-01", "2026-03-04", TimeBucketSize.Week, 10)]
    [InlineData("2026-01-01", "2027-01-01", TimeBucketSize.Week, 53)]
    [InlineData("2026-01-01", "2027-01-02", TimeBucketSize.Month, 13)]
    public void Buckets_switch_from_days_to_weeks_to_months_by_range_length(string from, string to, TimeBucketSize size, int count)
    {
        var (actual, buckets) = TimeUtilisationRules.Buckets(DateOnly.Parse(from), DateOnly.Parse(to));
        Assert.Equal((size, count), (actual, buckets.Count));
        Assert.Equal(DateOnly.Parse(from), buckets[0].From);
        Assert.Equal(DateOnly.Parse(to), buckets[^1].To);
    }

    /// <summary>RPT-050: weeks run Monday to Sunday, clipped at both ends, and one across New Year stays whole.</summary>
    [Fact]
    public void Weeks_run_monday_to_sunday_clipped_to_the_range()
    {
        var (_, weeks) = TimeUtilisationRules.Buckets(new DateOnly(2026, 10, 14), new DateOnly(2026, 12, 31));
        Assert.Equal((new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 18)), (weeks[0].From, weeks[0].To));
        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25)), (weeks[1].From, weeks[1].To));
        Assert.Equal((new DateOnly(2026, 12, 28), new DateOnly(2026, 12, 31)), (weeks[^1].From, weeks[^1].To));

        var (_, newYear) = TimeUtilisationRules.Buckets(new DateOnly(2026, 12, 2), new DateOnly(2027, 2, 28));
        Assert.Contains(newYear, b => b.From == new DateOnly(2026, 12, 28) && b.To == new DateOnly(2027, 1, 3));
    }

    /// <summary>RPT-050: months are clipped at both ends, and every minute falls in exactly one bucket.</summary>
    [Fact]
    public void Every_minute_falls_in_exactly_one_bucket()
    {
        var t = Task();
        var from = new DateOnly(2025, 1, 15);
        var to = new DateOnly(2026, 3, 10);
        var report = Run(
            [
                Entry(Alice, t, 10, from), Entry(Alice, t, 20, new DateOnly(2025, 6, 30)), Entry(Alice, t, 40, new DateOnly(2025, 7, 1)),
                Entry(Alice, t, 80, to)
            ], [t], from: from, to: to);

        Assert.Equal(TimeBucketSize.Month, report.BucketSize);
        Assert.Equal(15, report.Buckets.Count);
        Assert.Equal((from, new DateOnly(2025, 1, 31), 10), (report.Buckets[0].From, report.Buckets[0].To, report.Buckets[0].Minutes));
        Assert.Equal((new DateOnly(2026, 3, 1), to, 80), (report.Buckets[^1].From, report.Buckets[^1].To, report.Buckets[^1].Minutes));
        Assert.Equal((20, 40), (report.Buckets[5].Minutes, report.Buckets[6].Minutes));
        Assert.Equal(report.Summary.Minutes, report.Buckets.Sum(b => b.Minutes));
    }

    /// <summary>RPT-050: a daily report has a bucket for every day, with nothing on the days nobody logged.</summary>
    [Fact]
    public void Daily_buckets_cover_every_day()
    {
        var t = Task();
        var report = Run([Entry(Alice, t, 30, new DateOnly(2026, 10, 3))], [t]);
        Assert.Equal(TimeBucketSize.Day, report.BucketSize);
        Assert.Equal(31, report.Buckets.Count);
        Assert.All(report.Buckets, b => Assert.Equal(b.From, b.To));
        Assert.Equal(30, report.Buckets[2].Minutes);
        Assert.Equal(30, report.Buckets.Sum(b => b.Minutes));
    }

    /// <summary>RPT-053: an entry by someone outside the people reported on is dropped, and so is one on a task not in the facts.</summary>
    [Fact]
    public void Entries_outside_the_people_or_the_tasks_are_dropped()
    {
        var t = Task();
        var stranger = Guid.NewGuid();
        var report = Run([Entry(stranger, t, 30), Entry(Alice, t, 15), new TimeUtilisationEntry(Guid.NewGuid(), Alice, Day, 999)], [t]);

        Assert.Equal(15, report.Summary.Minutes);
        Assert.Equal("Alice", Assert.Single(report.People).Name);
        Assert.Equal("Alice", Assert.Single(report.PersonOptions).Name);
        Assert.Equal("Alice", Assert.Single(Assert.Single(report.Tasks).People).Name);
    }

    /// <summary>
    /// RPT-053: the report holds the people of the department scope who were employed in the window or logged time in it - so a
    /// leaver with time is in it and someone gone before the window isn't - by time, then the employed who logged nothing; the
    /// Person options are the same people, by name.
    /// </summary>
    [Fact]
    public void People_in_the_report_are_employed_or_logged_time()
    {
        var t = Task();
        var eve = Guid.NewGuid();
        var report = Run([Entry(Alice, t, 60), Entry(Carol, t, 30), Entry(eve, t, 45)], [t], employed: [Alice, Bob]);

        Assert.Equal(["Alice", "Bob", "Carol"], report.People.Select(p => p.Name).Order());
        Assert.DoesNotContain(report.People, p => p.Name == "Dan");
        Assert.Equal(90, report.Summary.Minutes);
        Assert.Equal(["Alice", "Bob", "Carol"], report.PersonOptions.Select(p => p.Name));
        Assert.Equal(1, report.Summary.LoggedNothing);
    }

    private static readonly DateOnly Mon = new(2026, 11, 23);
    private static DateOnly Wk(int day) => Mon.AddDays(day); // 0 = Monday 23 November 2026

    /// <summary>A week's report, Monday 23 to Friday 27 November 2026, or to the Sunday.</summary>
    private TimeUtilisationReport Week(
        IEnumerable<TimeUtilisationEntry> entries, IEnumerable<TimeUtilisationTaskFacts> tasks, IReadOnlyDictionary<Guid, ReportAvailability> availability,
        bool toSunday = false, IReadOnlyDictionary<Guid, ReportPerson>? people = null, Guid? personId = null) =>
        Run(entries, tasks, from: Mon, to: Wk(toSunday ? 6 : 4), availability: availability, people: people, personId: personId);

    /// <summary>
    /// RPT-055: utilisation is logged over available, whole and uncapped; unlogged is each person's gap, so one person's overtime
    /// doesn't hide another's; under target is below 80% of a real availability.
    /// </summary>
    [Fact]
    public void Utilisation_and_unlogged_are_per_person_not_netted()
    {
        var t = Task();
        var week = Avail(Mon, Wk(4));
        var report = Week(
            [Entry(Alice, t, 1920, Mon), Entry(Bob, t, 2700, Mon), Entry(Dan, t, 1900, Mon)], [t],
            new Dictionary<Guid, ReportAvailability> { [Alice] = week, [Bob] = week, [Dan] = week });

        var alice = report.People.Single(p => p.Name == "Alice");
        var bob = report.People.Single(p => p.Name == "Bob");
        var dan = report.People.Single(p => p.Name == "Dan");
        Assert.Equal((2400, 80, 480, false), (alice.AvailableMinutes, alice.UtilisationPercent, alice.UnloggedMinutes, alice.UnderTarget));
        Assert.Equal((113, 0, 300), (bob.UtilisationPercent, bob.UnloggedMinutes, bob.OverMinutes));
        Assert.Equal((79, true), (dan.UtilisationPercent, dan.UnderTarget));
        var s = report.Summary;
        Assert.Equal((7200, 480 + 500, 1, UtilisationRules.Percent(6520, 7200)), (s.AvailableMinutes, s.UnloggedMinutes, s.UnderTarget, s.UtilisationPercent));
        Assert.Equal(80, report.TargetPercent);
    }

    /// <summary>
    /// RPT-055 (Review Focus 5): someone with time but no work pattern is in the report, last, with no availability and no
    /// percentage, and is never under target.
    /// </summary>
    [Fact]
    public void A_person_with_time_but_no_pattern()
    {
        var t = Task();
        var report = Week([Entry(Alice, t, 1920, Mon), Entry(Carol, t, 60, Mon)], [t], new Dictionary<Guid, ReportAvailability> { [Alice] = Avail(Mon, Wk(4)) });

        var carol = report.People[^1];
        Assert.Equal(("Carol", false, 0, null, false), (carol.Name, carol.HasPattern, carol.AvailableMinutes, carol.UtilisationPercent, carol.UnderTarget));
        Assert.Equal(1980, report.Summary.Minutes);
    }

    /// <summary>
    /// RPT-056: the tiles add up availability - a holiday takes everyone's day, leave the person's - and count the holidays, the
    /// people who logged nothing and those with a gap.
    /// </summary>
    [Fact]
    public void The_tiles_count_availability()
    {
        var t = Task();
        var holidays = new Dictionary<DateOnly, string> { [Wk(3)] = "Day of Goodwill" };
        var report = Week([Entry(Alice, t, 600, Mon)], [t], new Dictionary<Guid, ReportAvailability>
        {
            [Alice] = Avail(Mon, Wk(4), off: [Leave(Wk(1))], holidays: holidays),
            [Bob] = Avail(Mon, Wk(4), holidays: holidays)
        });

        var s = report.Summary;
        Assert.Equal((2 * (2400 - 480) - 480, 480, 1), (s.AvailableMinutes, s.UnavailableMinutes, s.HolidayDays));
        Assert.Equal((1, 1, 2), (s.People, s.LoggedNothing, s.UnloggedPeople));
        Assert.Equal(1440 - 600 + 1920, s.UnloggedMinutes);
    }

    /// <summary>RPT-057: By person puts the lowest utilisation first, then name; people with no availability last.</summary>
    [Fact]
    public void By_person_puts_the_lowest_utilisation_first()
    {
        var zed = Guid.NewGuid();
        var amy = Guid.NewGuid();
        var people = new Dictionary<Guid, ReportPerson>(People) { [zed] = new(zed, "Zed", true), [amy] = new(amy, "Amy", true) };
        var t = Task();
        var week = Avail(Mon, Wk(4));
        var report = Week([Entry(zed, t, 1200, Mon), Entry(amy, t, 1200, Mon), Entry(Bob, t, 2160, Mon), Entry(Carol, t, 60, Mon)], [t],
            new Dictionary<Guid, ReportAvailability> { [zed] = week, [amy] = week, [Bob] = week }, people: people);

        Assert.Equal(["Amy", "Zed", "Bob", "Carol"], report.People.Select(p => p.Name));
    }

    /// <summary>
    /// RPT-058: the timesheet grid's cells are logged against available, under target, unavailable, holiday, over (time with no
    /// availability) or empty; a row totals its cells.
    /// </summary>
    [Fact]
    public void The_grid_shows_logged_against_available()
    {
        var t = Task();
        var holidays = new Dictionary<DateOnly, string> { [Wk(3)] = "Day of Goodwill" };
        var report = Week([Entry(Alice, t, 360, Wk(0)), Entry(Alice, t, 480, Wk(2)), Entry(Alice, t, 60, Wk(5))], [t],
            new Dictionary<Guid, ReportAvailability> { [Alice] = Avail(Mon, Wk(6), off: [Leave(Wk(1))], holidays: holidays) }, toSunday: true);

        Assert.Equal(TimeBucketSize.Day, report.GridBucketSize);
        var cells = Assert.Single(report.Grid).Cells;
        Assert.Equal(7, cells.Count);
        Assert.Equal((360, 480, true), (cells[0].Minutes, cells[0].AvailableMinutes, cells[0].UnderTarget));
        Assert.Equal((true, 0), (cells[1].Unavailable, cells[1].AvailableMinutes));
        Assert.Equal((480, 480, false), (cells[2].Minutes, cells[2].AvailableMinutes, cells[2].UnderTarget));
        Assert.True(cells[3].AllHoliday);
        Assert.True(cells[4].UnderTarget);
        Assert.True(cells[5].Over);
        Assert.True(cells[6].Empty);
        Assert.Equal((900, 1440), (report.Grid[0].Minutes, report.Grid[0].AvailableMinutes));
    }

    /// <summary>RPT-058: past 31 days the PDF's grid goes weekly while the page's stays daily to 62; every minute is in one PDF cell.</summary>
    [Fact]
    public void The_pdf_grid_uses_weeks_past_31_days()
    {
        var t = Task();
        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 11, 14);
        var entries = Enumerable.Range(0, 45).Select(i => Entry(Alice, t, 30 + i, from.AddDays(i))).ToList();
        var report = Run(entries, [t], from: from, to: to, availability: new Dictionary<Guid, ReportAvailability> { [Alice] = Avail(from, to) });

        Assert.Equal((TimeBucketSize.Day, 45), (report.GridBucketSize, report.Grid[0].Cells.Count));
        Assert.Equal(TimeBucketSize.Week, report.PdfGridBucketSize);
        Assert.Equal(report.Summary.Minutes, report.PdfGrid[0].Cells.Sum(c => c.Minutes));
        Assert.Equal(report.Summary.AvailableMinutes, report.PdfGrid[0].Cells.Sum(c => c.AvailableMinutes));
    }

    /// <summary>RPT-058: each period bucket carries the time logged, the time available, and the per-person gaps and excess summed.</summary>
    [Fact]
    public void Period_buckets_carry_available_unlogged_and_over()
    {
        var t = Task();
        var week = Avail(Mon, Wk(4));
        List<TimeUtilisationEntry> entries = [];
        for (var d = 0; d < 5; d++)
        {
            if (d < 4) entries.Add(Entry(Alice, t, 480, Wk(d)));
            entries.Add(Entry(Bob, t, 540, Wk(d)));
        }
        var report = Week(entries, [t], new Dictionary<Guid, ReportAvailability> { [Alice] = week, [Bob] = week });

        var monday = report.Buckets[0];
        Assert.Equal((1020, 960, 0, 60), (monday.Minutes, monday.AvailableMinutes, monday.UnloggedMinutes, monday.OverMinutes));
        var friday = report.Buckets[4];
        Assert.Equal((540, 960, 480, 60), (friday.Minutes, friday.AvailableMinutes, friday.UnloggedMinutes, friday.OverMinutes));
    }

    /// <summary>RPT-054: all of a person's time counts, on any department's tasks; the report has no project filter.</summary>
    [Fact]
    public void All_of_a_persons_time_counts_on_any_departments_tasks()
    {
        var mine = Task(department: "IT");
        var theirs = Task(department: "Marketing");
        var report = Run([Entry(Alice, mine, 60), Entry(Alice, theirs, 30)], [mine, theirs], employed: [Alice]);

        Assert.Equal(90, report.Summary.Minutes);
        Assert.Equal(["IT", "Marketing"], report.Tasks.Select(r => r.DepartmentName).Order());
        Assert.False(ReportCatalog.Get(ReportKind.TimeUtilisation).ByProject);
    }

    /// <summary>RPT-059: a task the viewer can't open is listed by number and department, with no title; its time still counts.</summary>
    [Fact]
    public void Restricted_tasks_are_shown_by_number_and_department()
    {
        var secret = Task(title: "Secret", department: "Marketing", restricted: true);
        var open = Task(title: "Open work");
        var report = Run([Entry(Alice, secret, 60), Entry(Alice, open, 30)], [secret, open], employed: [Alice]);

        var hidden = report.Tasks.Single(r => r.TaskId == secret.Id);
        Assert.Equal((true, "", secret.Number, "Marketing", 60), (hidden.Restricted, hidden.Title, hidden.Number, hidden.DepartmentName, hidden.Minutes));
        var shown = report.Tasks.Single(r => r.TaskId == open.Id);
        Assert.Equal((false, "Open work"), (shown.Restricted, shown.Title));
    }
}
