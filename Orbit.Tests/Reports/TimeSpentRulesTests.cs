using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>Time spent (spec §12).</summary>
public class TimeSpentRulesTests
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

    private TimeSpentTaskFacts Task(
        int? estimate = null, int total = 0, TaskItemStatus status = TaskItemStatus.InProgress, Guid[]? assignees = null,
        TaskType type = TaskType.Task, TaskSource source = TaskSource.Manual, Guid? projectId = null, string? number = null) =>
        new(Guid.NewGuid(), number ?? $"T-26-{++_number:00000}", "Task", status, type, source, projectId, assignees ?? [], estimate, total);

    private static TimeSpentEntry Entry(Guid user, TimeSpentTaskFacts task, int minutes, DateOnly? date = null) =>
        new(task.Id, user, date ?? Day, minutes);

    private static TimeSpentReport Run(
        IEnumerable<TimeSpentEntry> entries, IEnumerable<TimeSpentTaskFacts> tasks, IEnumerable<Guid>? covered = null,
        Guid? personId = null, IEnumerable<TimeSpentPersonTime>? toDate = null,
        IReadOnlyDictionary<Guid, TimeSpentProjectFacts>? projects = null, DateOnly? from = null, DateOnly? to = null) =>
        TimeSpentRules.Build(from ?? From, to ?? To, entries, tasks.ToDictionary(t => t.Id), toDate ?? [],
            projects ?? new Dictionary<Guid, TimeSpentProjectFacts>(), People, covered ?? [], personId);

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
        TimeSpentEntry[] entries = [Entry(Alice, t1, 120), Entry(Bob, t1, 60), Entry(Bob, t2, 30)];

        var report = Run(entries, [t1, t2], personId: Alice);
        Assert.Equal(["Alice", "Bob"], report.PersonOptions.Select(p => p.Name));
        Assert.Equal(120, report.Summary.Minutes);
        var row = Assert.Single(report.Tasks);
        Assert.Equal((120, 300), (row.Minutes, row.TotalMinutes));
        Assert.Equal("Alice", Assert.Single(row.People).Name);

        var nobody = Run(entries, [t1, t2], personId: Carol);
        Assert.Equal(["Alice", "Bob"], nobody.PersonOptions.Select(p => p.Name));
        Assert.Equal(0, nobody.Summary.Minutes);
        Assert.Empty(nobody.Tasks);
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
    /// RPT-047: By person by time then name, then the covered people who logged nothing, by name, at zero, and never doubled when
    /// they did log time.
    /// </summary>
    [Fact]
    public void By_person_orders_by_time_then_name_and_lists_who_logged_nothing_last()
    {
        var t1 = Task();
        var t2 = Task();
        var report = Run([Entry(Alice, t1, 60), Entry(Bob, t1, 60), Entry(Bob, t2, 60), Entry(Dan, t2, 60)], [t1, t2],
            covered: [Alice, Carol, Eve]);

        Assert.Equal(["Bob", "Alice", "Dan", "Carol", "Eve"], report.People.Select(p => p.Name));
        var bob = report.People[0];
        var carol = report.People[3];
        Assert.Equal((120, 2), (bob.Minutes, bob.Tasks));
        Assert.Equal((0, 0), (carol.Minutes, carol.Tasks));
        Assert.True(carol.LoggedNothing);
        Assert.False(bob.LoggedNothing);
        Assert.Equal((3, 2), (report.Summary.People, report.Summary.LoggedNothing));
        Assert.Single(report.People, p => p.Name == "Alice");
    }

    /// <summary>
    /// RPT-047: with the Person filter, only that person is considered: a covered person with no time is one zero row; someone from
    /// another department with none is nothing.
    /// </summary>
    [Fact]
    public void The_person_filter_narrows_who_logged_nothing_to_that_person()
    {
        var t1 = Task();
        var t2 = Task();
        TimeSpentEntry[] entries = [Entry(Alice, t1, 60), Entry(Bob, t1, 60), Entry(Bob, t2, 60), Entry(Dan, t2, 60)];

        var carol = Run(entries, [t1, t2], covered: [Alice, Carol, Eve], personId: Carol);
        Assert.Equal(["Carol"], carol.People.Select(p => p.Name));
        Assert.Equal((0, 1), (carol.Summary.People, carol.Summary.LoggedNothing));

        var dan = Run(entries, [t1, t2], covered: [Alice, Carol, Eve], personId: Dan);
        Assert.Equal(["Dan"], dan.People.Select(p => p.Name));
        Assert.Equal((1, 0), (dan.Summary.People, dan.Summary.LoggedNothing));

        var eve = Run(entries, [t1, t2], covered: [Alice, Carol], personId: Eve);
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
        var p = new TimeSpentProjectFacts(Guid.NewGuid(), "P-26-00001", "Website");
        var onProject = Task(projectId: p.Id);
        var standalone = Task();
        var lost = Task(projectId: Guid.NewGuid());
        var report = Run([Entry(Alice, onProject, 30), Entry(Alice, standalone, 20), Entry(Alice, lost, 10)], [onProject, standalone, lost],
            projects: new Dictionary<Guid, TimeSpentProjectFacts> { [p.Id] = p });

        Assert.Equal(p, report.Tasks.Single(t => t.TaskId == onProject.Id).Project);
        Assert.Null(report.Tasks.Single(t => t.TaskId == standalone.Id).Project);
        Assert.Equal("Unknown project", report.Tasks.Single(t => t.TaskId == lost.Id).Project!.Name);
    }

    /// <summary>Review focus: an entry by a user with no name row is Unknown; an entry for a task not in the facts is dropped.</summary>
    [Fact]
    public void An_unknown_person_is_named_unknown_and_an_unknown_tasks_time_is_dropped()
    {
        var t = Task();
        var stranger = Guid.NewGuid();
        var report = Run([Entry(stranger, t, 30), new TimeSpentEntry(Guid.NewGuid(), Alice, Day, 999)], [t]);

        Assert.Equal(30, report.Summary.Minutes);
        Assert.Equal("Unknown", Assert.Single(report.People).Name);
        Assert.Equal("Unknown", Assert.Single(report.PersonOptions).Name);
        Assert.Equal("Unknown", Assert.Single(Assert.Single(report.Tasks).People).Name);
    }
}
