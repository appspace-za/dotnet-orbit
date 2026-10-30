using System.Text.Json;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Components.Pages.Reports;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>Sprint status (spec §12).</summary>
public class SprintReportRulesTests
{
    private static readonly DateOnly Start = new(2026, 10, 19);
    private static readonly DateOnly End = new(2026, 10, 30);
    private static readonly DateTime AsAt = new(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc);

    private static readonly Guid SprintId = Guid.NewGuid();
    private static readonly Guid PreviousSprintId = Guid.NewGuid();
    private static readonly Guid NextSprintId = Guid.NewGuid();
    private static readonly Guid OtherSprintId = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, string> SprintNames = new Dictionary<Guid, string>
    {
        [SprintId] = "Sprint 14", [PreviousSprintId] = "Sprint 13", [NextSprintId] = "Sprint 15", [OtherSprintId] = "Sprint 9"
    };

    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, ReportPerson> People = new Dictionary<Guid, ReportPerson>
    {
        [Alice] = new(Alice, "Alice", true), [Bob] = new(Bob, "Bob", true), [Carol] = new(Carol, "Carol", false)
    };

    private static readonly SprintProjectFacts Alpha = new(Guid.NewGuid(), "P-26-00001", "Alpha", ProjectStatus.Active, "Ops");
    private static readonly SprintProjectFacts Beta = new(Guid.NewGuid(), "P-26-00002", "Beta", ProjectStatus.OnHold, "Dev");
    private static readonly SprintProjectFacts Gamma = new(Guid.NewGuid(), "P-26-00003", "Gamma", ProjectStatus.Active, "Ops");

    private static readonly IReadOnlyDictionary<Guid, SprintProjectFacts> Projects =
        new[] { Alpha, Beta, Gamma }.ToDictionary(p => p.Id);

    private static int _number;

    private static DateTime Oct(int day, int hour = 10) => new(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);

    private static SprintFacts Sprint(SprintStatus status = SprintStatus.Active, DateTime? completedAt = null) =>
        new(SprintId, "Sprint 14", "Ship the portal", Start, End, status, completedAt);

    /// <summary>A task planned into the sprint (by default), or in <paramref name="sprintId"/>, or with none (<paramref name="planned"/> false).</summary>
    private static SprintTaskFacts Task(
        bool planned = true, Guid? sprintId = null, bool inBacklog = false, TaskItemStatus status = TaskItemStatus.InProgress,
        SprintProjectFacts? project = null, Guid[]? assignees = null, int? estimate = null, int total = 0, int sprint = 0,
        DateTime? created = null, DateTime? completed = null) =>
        new(new TaskTimeFacts(Guid.NewGuid(), $"T-26-{Interlocked.Increment(ref _number):00000}", "Task", status, assignees ?? [], estimate, total),
            project?.Id, sprintId ?? (planned ? SprintId : null), inBacklog, created ?? Oct(1), completed, sprint);

    private static SprintTaskFacts NotPlanned(
        TaskItemStatus status = TaskItemStatus.InProgress, SprintProjectFacts? project = null, Guid[]? assignees = null,
        int? estimate = null, int total = 0, int sprint = 0, DateTime? created = null, DateTime? completed = null) =>
        Task(false, null, false, status, project, assignees, estimate, total, sprint, created ?? Oct(20), completed);

    /// <summary>A task on the Backlog, created long before the sprint by default.</summary>
    private static SprintTaskFacts Backlog(
        TaskItemStatus status = TaskItemStatus.InProgress, SprintProjectFacts? project = null, Guid[]? assignees = null,
        int? estimate = null, int total = 0, int sprint = 0, DateTime? created = null, DateTime? completed = null) =>
        Task(false, null, true, status, project, assignees, estimate, total, sprint, created ?? Oct(1), completed);

    /// <summary>A task of the next sprint, Sprint 15, created long before this one by default: work done on it now is done ahead.</summary>
    private static SprintTaskFacts Ahead(
        TaskItemStatus status = TaskItemStatus.InProgress, SprintProjectFacts? project = null, Guid[]? assignees = null,
        int? estimate = null, int total = 0, int sprint = 0, DateTime? created = null, DateTime? completed = null) =>
        Task(false, NextSprintId, false, status, project, assignees, estimate, total, sprint, created ?? Oct(1), completed);

    private static SprintStatusReport Build(
        SprintFacts sprint, SprintTaskFacts[] tasks, SprintPersonTime[]? time = null, SprintDayTime[]? days = null,
        SprintCompletion[]? completions = null, Guid? projectId = null, Guid? assigneeId = null, DateTime? asAt = null,
        SprintStatusChange[]? changes = null, Guid[]? laterSprintIds = null) =>
        SprintReportRules.Build(sprint, tasks, time ?? [], days ?? [], changes ?? [], Projects, completions ?? [], SprintNames,
            (laterSprintIds ?? []).ToHashSet(), People,
            projectId, assigneeId, asAt ?? AsAt);

    /// <summary>A change to <paramref name="t"/>'s status, as its audit row records it.</summary>
    private static SprintStatusChange Change(SprintTaskFacts t, DateTime at, TaskItemStatus from, TaskItemStatus to) =>
        new(t.Task.Id, at, from, to);

    private static SprintTaskRow Row(SprintStatusReport r, SprintTaskFacts t) =>
        r.Rows.SelectMany(g => g.TaskRows).Single(x => x.TaskId == t.Task.Id);

    private static string Json(object details) => JsonSerializer.Serialize(details, OrbitJson.Options);

    /// <summary>
    /// RPT-022: a sprint's Completed audit row is read in both shapes SprintService writes (<see cref="SprintCompletedDetails"/>), which
    /// are those of the rows already written, and nothing else.
    /// </summary>
    [Fact]
    public void Completion_rows_are_read_as_the_sprint_service_writes_them()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        // The record writes exactly what the rows already in the database hold, so they still read.
        var rolledRow = $$"""{"rolledForwardTo":"{{NextSprintId}}","rolledForwardTaskIds":["{{a}}","{{b}}"]}""";
        var returnedRow = $$"""{"returnedToBacklogTaskIds":["{{a}}"]}""";
        Assert.Equal(rolledRow, Json(SprintCompletedDetails.RolledForward(NextSprintId, [a, b])));
        Assert.Equal(returnedRow, Json(SprintCompletedDetails.ReturnedToBacklog([a])));

        Assert.True(SprintReportRules.TryReadCompletion(SprintId, AsAt, rolledRow, out var rolled));
        Assert.Equal(SprintId, rolled.SprintId);
        Assert.Equal(AsAt, rolled.At);
        Assert.Equal(NextSprintId, rolled.RolledForwardTo);
        Assert.Equal([a, b], rolled.OpenTaskIds);

        Assert.True(SprintReportRules.TryReadCompletion(SprintId, AsAt, returnedRow, out var back));
        Assert.Null(back.RolledForwardTo);
        Assert.Equal([a], back.OpenTaskIds);

        Assert.True(SprintReportRules.TryReadCompletion(SprintId, AsAt, Json(SprintCompletedDetails.ReturnedToBacklog([])), out var none));
        Assert.Empty(none.OpenTaskIds);

        foreach (var other in new[]
                 {
                     null, "", "not json", "[]", "{}", "null", """{"status":{"from":"Active","to":"Completed"}}""",
                     """{"returnedToBacklogTaskIds":["not-a-guid"]}""", """{"returnedToBacklogTaskIds":"x"}""",
                     Json(new { rolledForwardTaskIds = new[] { a } }), // no sprint it went to
                     Json(new { previousSprintId = PreviousSprintId })
                 })
            Assert.False(SprintReportRules.TryReadCompletion(SprintId, AsAt, other, out _));
    }

    /// <summary>RPT-023: Not Planned work counts when it is Not Planned now and was created, done or logged in the window.</summary>
    [Fact]
    public void Not_planned_work_is_matched_to_the_sprint_by_date()
    {
        var sprint = Sprint();
        Assert.Equal(SprintWorkKind.NotPlanned, SprintReportRules.UnplannedKind(NotPlanned(created: Oct(19, 0)), sprint));
        Assert.Equal(SprintWorkKind.NotPlanned, SprintReportRules.UnplannedKind(NotPlanned(created: new DateTime(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc)), sprint));
        Assert.Equal(SprintWorkKind.NotPlanned, SprintReportRules.UnplannedKind(
            NotPlanned(TaskItemStatus.Done, created: Oct(1), completed: Oct(25)), sprint));
        Assert.Equal(SprintWorkKind.NotPlanned, SprintReportRules.UnplannedKind(NotPlanned(created: Oct(1), sprint: 30), sprint));

        Assert.Null(SprintReportRules.UnplannedKind(NotPlanned(created: Oct(1)), sprint)); // nothing in the window
        Assert.Null(SprintReportRules.UnplannedKind(NotPlanned(created: Oct(31, 0)), sprint)); // the day after it
        Assert.Null(SprintReportRules.UnplannedKind(NotPlanned(created: Oct(18, 23)), sprint)); // the day before it
        Assert.Null(SprintReportRules.UnplannedKind(
            NotPlanned(TaskItemStatus.Done, created: Oct(1), completed: Oct(31)), sprint)); // done after it
        Assert.Null(SprintReportRules.UnplannedKind(
            NotPlanned(TaskItemStatus.Cancelled, created: Oct(1), completed: null), sprint));
        // Set to Done in the window and reopened since: CompletedAt is cleared, the change to Done still says so.
        var reopened = NotPlanned(created: Oct(1));
        Assert.Equal(SprintWorkKind.NotPlanned, SprintReportRules.UnplannedKind(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Done), Change(reopened, Oct(31), TaskItemStatus.Done, TaskItemStatus.Todo)]));
        Assert.Null(SprintReportRules.UnplannedKind(reopened, sprint,
            [Change(reopened, Oct(31), TaskItemStatus.Todo, TaskItemStatus.Done), Change(reopened, Oct(31, 12), TaskItemStatus.Done, TaskItemStatus.Todo)]));
        Assert.Null(SprintReportRules.UnplannedKind(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Cancelled)])); // closed, but not Done
        // On the Backlog, or in another sprint, work is never Not Planned, whatever happened to it in the window.
        Assert.Equal(SprintWorkKind.Backlog, SprintReportRules.UnplannedKind(Task(false, inBacklog: true, created: Oct(20), sprint: 60), sprint));
        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(Task(false, OtherSprintId, created: Oct(20), sprint: 60), sprint));

        var counted = NotPlanned(created: Oct(22));
        var backlog = Task(false, inBacklog: true, created: Oct(22), sprint: 30);
        var elsewhere = Task(false, OtherSprintId, sprint: 30);
        var quiet = NotPlanned(created: Oct(1));
        var r = Build(sprint, [counted, backlog, elsewhere, quiet]);
        // The Backlog task worked on (RPT-037) and the other sprint's (RPT-038) are never Not Planned work. All on no project, the
        // three share the No project row, each as its kind (RPT-043).
        Assert.Null(Assert.Single(r.Rows).Project);
        Assert.Equal(
            [(counted.Task.Id, SprintWorkKind.NotPlanned), (backlog.Task.Id, SprintWorkKind.Backlog), (elsewhere.Task.Id, SprintWorkKind.OtherSprint)],
            r.Rows[0].TaskRows.Select(x => (x.TaskId, x.Work)));
    }

    /// <summary>
    /// RPT-037: a Backlog task counts as Backlog work when it is on the Backlog now and was set to Done or logged against in the
    /// window, its first and last days included - done in it and reopened since too - but not when it was only created in it, or
    /// cancelled, and a task the sprint itself returned to the Backlog stays one of its planned tasks.
    /// </summary>
    [Fact]
    public void Backlog_work_is_matched_to_the_sprint_by_work_done()
    {
        var sprint = Sprint();
        Assert.Equal(SprintWorkKind.Backlog, SprintReportRules.UnplannedKind(Backlog(sprint: 30), sprint));
        Assert.Equal(SprintWorkKind.Backlog, SprintReportRules.UnplannedKind(Backlog(TaskItemStatus.Done, completed: Oct(19, 0)), sprint));
        Assert.Equal(SprintWorkKind.Backlog, SprintReportRules.UnplannedKind(
            Backlog(TaskItemStatus.Done, completed: new DateTime(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc)), sprint));
        var reopened = Backlog();
        Assert.Equal(SprintWorkKind.Backlog, SprintReportRules.UnplannedKind(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Done), Change(reopened, Oct(31), TaskItemStatus.Done, TaskItemStatus.Todo)]));

        Assert.Null(SprintReportRules.UnplannedKind(Backlog(created: Oct(22)), sprint)); // only created in it
        Assert.Null(SprintReportRules.UnplannedKind(Backlog(), sprint)); // nothing in the window
        Assert.Null(SprintReportRules.UnplannedKind(Backlog(TaskItemStatus.Done, completed: Oct(31, 0)), sprint)); // the day after
        Assert.Null(SprintReportRules.UnplannedKind(Backlog(TaskItemStatus.Done, completed: Oct(18, 23)), sprint)); // the day before
        Assert.Null(SprintReportRules.UnplannedKind(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Cancelled)])); // closed, but not Done
        // Not Planned work, and work in a sprint, is never Backlog work.
        Assert.Equal(SprintWorkKind.NotPlanned, SprintReportRules.UnplannedKind(NotPlanned(sprint: 30), sprint));
        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(Task(false, OtherSprintId, sprint: 30), sprint));

        // Once the sprint has ended, a task it returned to the Backlog is still its own planned work, Left open → Backlog.
        var completedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var returned = Backlog(sprint: 45);
        var picked = Backlog(sprint: 20);
        var r = Build(Sprint(SprintStatus.Completed, completedAt), [returned, picked],
            completions: [new(SprintId, completedAt, null, [returned.Task.Id])], asAt: completedAt);
        // Both on no project: the task it returned is planned, so first; the one taken from the Backlog follows it (RPT-043).
        Assert.Null(Assert.Single(r.Rows).Project);
        Assert.Equal((SprintTaskOutcome.LeftOpen, "Backlog"), (Row(r, returned).Outcome, Row(r, returned).LeftOpenTo));
        Assert.Equal([(returned.Task.Id, SprintWorkKind.Planned), (picked.Task.Id, SprintWorkKind.Backlog)],
            r.Rows[0].TaskRows.Select(x => (x.TaskId, x.Work)));
    }

    /// <summary>
    /// RPT-037: the Backlog tasks worked on join their project's row, marked "From the Backlog" - a project with only Backlog work
    /// gets a row and a project option, its assignees join the assignee options and the filters pick it - and with the Not Planned
    /// work they are the unplanned work: the summary, the people and the days set their time apart, and the unplanned share covers
    /// both.
    /// </summary>
    [Fact]
    public void Backlog_work_done_in_the_sprint_joins_its_project()
    {
        var planned = Task(project: Alpha, status: TaskItemStatus.Done, assignees: [Alice], estimate: 60, sprint: 60, total: 60, completed: Oct(22));
        var support = NotPlanned(assignees: [Bob], sprint: 30, total: 30);
        var pulledIn = Backlog(TaskItemStatus.Done, project: Gamma, assignees: [Carol], estimate: 120, sprint: 90, total: 150, completed: Oct(27));
        var started = Backlog(project: Beta, assignees: [Alice], sprint: 20, total: 20);
        SprintPersonTime[] time =
        [
            new(planned.Task.Id, Alice, 60, 60), new(support.Task.Id, Bob, 30, 30), new(pulledIn.Task.Id, Carol, 90, 150),
            new(started.Task.Id, Alice, 20, 20)
        ];
        SprintDayTime[] days =
        [
            new(planned.Task.Id, Start, 60), new(support.Task.Id, Start, 30), new(pulledIn.Task.Id, Start, 90),
            new(started.Task.Id, End, 20)
        ];
        SprintTaskFacts[] tasks = [planned, support, pulledIn, started];
        var r = Build(Sprint(), tasks, time, days);

        // Beta and Gamma have only Backlog work, and a row each; the Not Planned task, on no project, is on the last row.
        Assert.Equal([Beta, Alpha, Gamma, null], r.Rows.Select(g => g.Project));
        Assert.Equal(["Beta", "Alpha", "Gamma", "No project"], r.Rows.Select(g => g.Name));
        Assert.Equal(support.Task.Id, Assert.Single(r.Rows[3].TaskRows).TaskId);
        var gamma = r.Rows[2];
        Assert.Equal(Gamma.Id.ToString(), gamma.Key);
        Assert.Equal(new SprintTaskCounts(1, 1, 0, 0, 0, 0, 0), gamma.Tasks);
        Assert.Equal(gamma.Tasks, gamma.UnplannedTasks);
        Assert.Equal((90, 150, 90), (gamma.SprintMinutes, gamma.ToDateMinutes, gamma.UnplannedSprintMinutes));
        Assert.Equal(SprintTaskCounts.None, r.Rows[1].UnplannedTasks); // Alpha has only planned work
        var pulledInRow = Row(r, pulledIn);
        Assert.Equal(SprintWorkKind.Backlog, pulledInRow.Work);
        Assert.Equal("From the Backlog", ReportText.Note(pulledInRow, withOver: false));
        Assert.Equal("From the Backlog · No estimate", ReportText.Note(Row(r, started)));
        Assert.Equal([Beta, Alpha, Gamma], r.ProjectOptions); // the projects with a row
        Assert.Equal(["Alice", "Bob", "Carol"], r.AssigneeOptions.Select(p => p.Name));

        var s = r.Summary;
        Assert.True(s.HasBacklog);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 0, 1), s.Backlog.Tasks);
        Assert.Equal(4, s.All.Total);
        Assert.Equal((60, 30, 90 + 20), (s.Planned.SprintMinutes, s.NotPlanned.SprintMinutes, s.Backlog.SprintMinutes));
        Assert.Equal(200, s.SprintMinutes);
        Assert.Equal(260, s.ToDateMinutes);
        Assert.Equal(140, s.UnplannedSprintMinutes);
        Assert.Equal(70, s.UnplannedPercent); // 140 of 200
        Assert.Equal(new EstimateComparison(1, 120, 150, 1), s.Backlog.Estimate);
        Assert.Equal(new EstimateComparison(2, 180, 210, 1), s.AllEstimate);

        Assert.Equal(
            [
                new SprintImpactRow(Carol, "Carol", 0, 0, new(0, 0, 1, 0), new(0, 0, 90, 0)),
                new SprintImpactRow(Alice, "Alice", 1, 0, new(1, 0, 1, 0), new(60, 0, 20, 0)),
                new SprintImpactRow(Bob, "Bob", 0, 0, new(0, 1, 0, 0), new(0, 30, 0, 0))
            ],
            r.People);
        Assert.Equal(100, r.People[0].UnplannedPercent);
        Assert.Equal(25, r.People[1].UnplannedPercent); // 20 of 80
        Assert.Equal((Start, new SprintKindCounts(60, 30, 90, 0)), (r.Days[0].Date, r.Days[0].Minutes));
        Assert.Equal((End, new SprintKindCounts(0, 0, 20, 0)), (r.Days[^1].Date, r.Days[^1].Minutes));
        Assert.Equal(180, r.Days[0].Minutes.Total);

        var byProject = Build(Sprint(), tasks, time, days, projectId: Gamma.Id);
        Assert.Equal([pulledIn.Task.Id], byProject.Rows.SelectMany(g => g.TaskRows).Select(x => x.TaskId));
        var byPerson = Build(Sprint(), tasks, time, days, assigneeId: Alice);
        Assert.Equal([started.Task.Id, planned.Task.Id], byPerson.Rows.SelectMany(g => g.TaskRows).Select(x => x.TaskId)); // Beta, Alpha
        Assert.Equal((Start, new SprintKindCounts(60, 0, 0, 0)), (byPerson.Days[0].Date, byPerson.Days[0].Minutes));

        var none = Build(Sprint(), [planned, support], time, days);
        Assert.False(none.Summary.HasBacklog);
        Assert.DoesNotContain(none.Rows.SelectMany(g => g.TaskRows), x => x.Work == SprintWorkKind.Backlog);
    }

    /// <summary>
    /// RPT-038: another sprint's task counts as other-sprint work when it was set to Done or logged against in the window, its first and
    /// last days included - done in it and reopened since too - whichever sprint it is in: a later one, worked on ahead, or an earlier
    /// one, a finished task reopened or logged against. Not when it was only created in it, or cancelled; never a task of this sprint,
    /// Not Planned or Backlog work; and a task this sprint handed on to the next stays one of its planned tasks. The later sprints are
    /// those that start after this one, whatever they are now: a task in one is Planned for it, in any other From it.
    /// </summary>
    [Fact]
    public void Other_sprint_work_is_matched_to_the_sprint_by_work_done()
    {
        var sprint = Sprint();
        static SprintFacts S(Guid id, DateOnly start, SprintStatus status) => new(id, "S", null, start, start.AddDays(11), status, null);
        Assert.Equal([NextSprintId], SprintReportRules.LaterSprints(
            [sprint, S(PreviousSprintId, Start.AddDays(-14), SprintStatus.Completed), S(NextSprintId, End.AddDays(1), SprintStatus.Planned),
             S(OtherSprintId, Start.AddDays(-60), SprintStatus.Completed)], sprint));

        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(Ahead(sprint: 30), sprint));
        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(Ahead(TaskItemStatus.Done, completed: Oct(19, 0)), sprint));
        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(
            Ahead(TaskItemStatus.Done, completed: new DateTime(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc)), sprint));
        var reopened = Ahead();
        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Done), Change(reopened, Oct(31), TaskItemStatus.Done, TaskItemStatus.Todo)]));
        // An earlier sprint's finished task, logged against now - or reopened and done again in the window.
        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(Task(false, PreviousSprintId, status: TaskItemStatus.Done, sprint: 30, completed: Oct(10)), sprint));
        var redone = Task(false, PreviousSprintId, status: TaskItemStatus.Done, completed: Oct(25));
        Assert.Equal(SprintWorkKind.OtherSprint, SprintReportRules.UnplannedKind(redone, sprint));

        Assert.Null(SprintReportRules.UnplannedKind(Ahead(created: Oct(22)), sprint)); // only created in it
        Assert.Null(SprintReportRules.UnplannedKind(Ahead(), sprint)); // nothing in the window
        Assert.Null(SprintReportRules.UnplannedKind(Task(false, PreviousSprintId, status: TaskItemStatus.Done, completed: Oct(10)), sprint));
        Assert.Null(SprintReportRules.UnplannedKind(Ahead(TaskItemStatus.Done, completed: Oct(31, 0)), sprint));
        Assert.Null(SprintReportRules.UnplannedKind(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Cancelled)])); // closed, but not Done
        Assert.Null(SprintReportRules.UnplannedKind(Task(sprint: 30), sprint)); // this sprint's own
        Assert.Equal(SprintWorkKind.NotPlanned, SprintReportRules.UnplannedKind(NotPlanned(sprint: 30), sprint));
        Assert.Equal(SprintWorkKind.Backlog, SprintReportRules.UnplannedKind(Backlog(sprint: 30), sprint));

        // Once the sprint has ended, the open tasks it rolled forward to Sprint 15 are still its own; other sprints' work is apart.
        var completedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var handedOn = Ahead(sprint: 45);
        var ahead = Ahead(sprint: 20);
        var earlier = Task(false, PreviousSprintId, status: TaskItemStatus.Done, sprint: 15, completed: Oct(10));
        var r = Build(Sprint(SprintStatus.Completed, completedAt), [handedOn, ahead, earlier],
            completions: [new(SprintId, completedAt, NextSprintId, [handedOn.Task.Id])], asAt: completedAt, laterSprintIds: [NextSprintId]);
        // All on no project: the task it handed on is planned, so first; the other sprints' follow it (RPT-043).
        Assert.Null(Assert.Single(r.Rows).Project);
        Assert.Equal((handedOn.Task.Id, SprintWorkKind.Planned), (r.Rows[0].TaskRows.First().TaskId, r.Rows[0].TaskRows.First().Work));
        Assert.Equal((SprintTaskOutcome.LeftOpen, "Sprint 15", (string?)null),
            (Row(r, handedOn).Outcome, Row(r, handedOn).LeftOpenTo, Row(r, handedOn).OtherSprint));
        Assert.Equal(("Sprint 15", true), (Row(r, ahead).OtherSprint, Row(r, ahead).OtherSprintIsLater));
        Assert.Equal(("Sprint 13", false), (Row(r, earlier).OtherSprint, Row(r, earlier).OtherSprintIsLater));
        Assert.Equal("Planned for Sprint 15 · No estimate", ReportText.Note(Row(r, ahead)));
        // Finished in Sprint 13 and only logged against in this one (RPT-039).
        Assert.Equal("From Sprint 13 · Done before the sprint · No estimate", ReportText.Note(Row(r, earlier)));
    }

    /// <summary>
    /// RPT-038: other sprints' tasks worked on join their project's row, or the No project row, each naming its sprint - a project
    /// with only such work gets a row and a project option, its assignees join the assignee options and the filters pick it - and
    /// they are unplanned work: the summary, the people and the days set their time apart, and the unplanned share covers Not
    /// Planned, Backlog and other-sprint work.
    /// </summary>
    [Fact]
    public void Other_sprint_work_done_in_the_sprint_joins_its_project()
    {
        var planned = Task(project: Alpha, assignees: [Alice], sprint: 60, total: 60);
        var support = NotPlanned(assignees: [Bob], sprint: 30, total: 30);
        var pulledIn = Backlog(project: Beta, assignees: [Bob], sprint: 15, total: 15);
        var ahead = Ahead(TaskItemStatus.Done, project: Gamma, assignees: [Carol], estimate: 60, sprint: 45, total: 45, completed: Oct(28));
        var started = Ahead(assignees: [Alice], sprint: 30, total: 50);
        SprintPersonTime[] time =
        [
            new(planned.Task.Id, Alice, 60, 60), new(support.Task.Id, Bob, 30, 30), new(pulledIn.Task.Id, Bob, 15, 15),
            new(ahead.Task.Id, Carol, 45, 45), new(started.Task.Id, Alice, 30, 50)
        ];
        SprintDayTime[] days =
        [
            new(planned.Task.Id, Start, 60), new(support.Task.Id, Start, 30), new(pulledIn.Task.Id, Start, 15),
            new(started.Task.Id, Start, 30), new(ahead.Task.Id, End, 45)
        ];
        SprintTaskFacts[] tasks = [planned, support, pulledIn, ahead, started];
        Guid[] later = [NextSprintId];
        var r = Build(Sprint(), tasks, time, days, laterSprintIds: later);

        Assert.Equal([Beta, Alpha, Gamma, null], r.Rows.Select(g => g.Project));
        Assert.Equal(["Beta", "Alpha", "Gamma", "No project"], r.Rows.Select(g => g.Name));
        Assert.Equal("NoProject", r.Rows[3].Key);
        // On no project, the later sprint's task shares the last row with the Not Planned one.
        var standalone = r.Rows[3];
        Assert.Equal([(support.Task.Id, SprintWorkKind.NotPlanned), (started.Task.Id, SprintWorkKind.OtherSprint)],
            standalone.TaskRows.Select(x => (x.TaskId, x.Work)));
        Assert.Equal(new SprintTaskCounts(2, 0, 2, 0, 0, 0, 2), standalone.Tasks);
        Assert.Equal(standalone.Tasks, standalone.UnplannedTasks);
        Assert.Equal((60, 80, 60), (standalone.SprintMinutes, standalone.ToDateMinutes, standalone.UnplannedSprintMinutes));
        var aheadRow = Row(r, ahead);
        Assert.Equal(("Sprint 15", SprintWorkKind.OtherSprint), (aheadRow.OtherSprint, aheadRow.Work));
        Assert.Equal("Planned for Sprint 15", ReportText.Note(aheadRow));
        Assert.Equal("Planned for Sprint 15 · No estimate", ReportText.Note(Row(r, started)));
        Assert.Null(Row(r, pulledIn).OtherSprint);
        Assert.Equal([Beta, Alpha, Gamma], r.ProjectOptions); // the projects with a row
        Assert.Equal(["Alice", "Bob", "Carol"], r.AssigneeOptions.Select(p => p.Name));

        var s = r.Summary;
        Assert.True(s.HasOtherSprint);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 0, 1), s.OtherSprint.Tasks);
        Assert.Equal((60, 30, 15, 75), (s.Planned.SprintMinutes, s.NotPlanned.SprintMinutes, s.Backlog.SprintMinutes, s.OtherSprint.SprintMinutes));
        Assert.Equal(180, s.SprintMinutes);
        Assert.Equal(200, s.ToDateMinutes);
        Assert.Equal(120, s.UnplannedSprintMinutes);
        Assert.Equal(67, s.UnplannedPercent); // 120 of 180
        Assert.Equal(new EstimateComparison(1, 60, 45, 0), s.OtherSprint.Estimate);
        Assert.Equal(5, s.All.Total);
        Assert.Equal([SprintWorkKind.NotPlanned, SprintWorkKind.Backlog, SprintWorkKind.OtherSprint], s.Unplanned);

        Assert.Equal(
            [
                new SprintImpactRow(Alice, "Alice", 0, 1, new(1, 0, 0, 1), new(60, 0, 0, 30)),
                new SprintImpactRow(Bob, "Bob", 0, 0, new(0, 1, 1, 0), new(0, 30, 15, 0)),
                new SprintImpactRow(Carol, "Carol", 0, 0, new(0, 0, 0, 1), new(0, 0, 0, 45))
            ],
            r.People);
        Assert.Equal(33, r.People[0].UnplannedPercent); // 30 of 90
        Assert.Equal((1, 30), (r.People[0].Tasks.OtherSprint, r.People[0].Minutes.OtherSprint));
        Assert.Equal((Start, new SprintKindCounts(60, 30, 15, 30)), (r.Days[0].Date, r.Days[0].Minutes));
        Assert.Equal((End, new SprintKindCounts(0, 0, 0, 45)), (r.Days[^1].Date, r.Days[^1].Minutes));
        Assert.Equal(30, r.Days[0].Minutes.Of(SprintWorkKind.OtherSprint));

        var byProject = Build(Sprint(), tasks, time, days, projectId: Gamma.Id, laterSprintIds: later);
        Assert.Equal([ahead.Task.Id], byProject.Rows.SelectMany(g => g.TaskRows).Select(x => x.TaskId));
        var byPerson = Build(Sprint(), tasks, time, days, assigneeId: Alice, laterSprintIds: later);
        Assert.Equal([planned.Task.Id, started.Task.Id], byPerson.Rows.SelectMany(g => g.TaskRows).Select(x => x.TaskId));

        // Whether a sprint is later only words the note: not among the later sprints, the same tasks are From it.
        var notLater = Build(Sprint(), tasks, time, days);
        Assert.True(notLater.Summary.HasOtherSprint);
        Assert.Equal("From Sprint 15", ReportText.Note(Row(notLater, ahead)));

        var none = Build(Sprint(), [planned, support, pulledIn], time, days);
        Assert.False(none.Summary.HasOtherSprint);
        Assert.DoesNotContain(none.Rows.SelectMany(g => g.TaskRows), x => x.Work == SprintWorkKind.OtherSprint);
        Assert.Equal([SprintWorkKind.NotPlanned, SprintWorkKind.Backlog], none.Summary.Unplanned);
    }

    /// <summary>
    /// RPT-039: a planned task that was already Done when the sprint started, and hasn't changed since, is marked Done before the
    /// sprint, its counts as they are; not one done in the window, one reopened and done again in it, or one the sprint handed on.
    /// A change after a completed sprint's end doesn't undo the mark.
    /// </summary>
    [Fact]
    public void A_planned_task_done_before_the_sprint_is_marked_and_keeps_its_counts()
    {
        var sprint = Sprint();
        Assert.True(SprintReportRules.DoneBeforeSprint(TaskItemStatus.Done, [], sprint, AsAt));
        Assert.True(SprintReportRules.DoneBeforeSprint(TaskItemStatus.Done, [], sprint, AsAt, Oct(10)));
        Assert.False(SprintReportRules.DoneBeforeSprint(TaskItemStatus.InProgress, [], sprint, AsAt));
        // Completed in the window with no change to show it: finished in the sprint (RPT-040's FinishedOn), not before it.
        Assert.False(SprintReportRules.DoneBeforeSprint(TaskItemStatus.Done, [], sprint, AsAt, Oct(22)));
        var completedInSprint = Task(status: TaskItemStatus.Done, completed: Oct(22));
        Assert.False(Row(Build(sprint, [completedInSprint]), completedInSprint).DoneBeforeSprint);

        var early = Task(status: TaskItemStatus.Done, completed: Oct(10));
        var inSprint = Task(status: TaskItemStatus.Done, completed: Oct(22));
        var redone = Task(status: TaskItemStatus.Done, completed: Oct(24));
        var open = Task();
        SprintStatusChange[] changes =
        [
            Change(inSprint, Oct(22), TaskItemStatus.InProgress, TaskItemStatus.Done),
            Change(redone, Oct(20), TaskItemStatus.Done, TaskItemStatus.InProgress), Change(redone, Oct(24), TaskItemStatus.InProgress, TaskItemStatus.Done)
        ];
        Assert.False(SprintReportRules.DoneBeforeSprint(TaskItemStatus.Done, changes.Where(c => c.TaskId == inSprint.Task.Id), sprint, AsAt));
        Assert.False(SprintReportRules.DoneBeforeSprint(TaskItemStatus.Done, changes.Where(c => c.TaskId == redone.Task.Id), sprint, AsAt));

        var r = Build(sprint, [early, inSprint, redone, open], changes: changes);
        Assert.True(Row(r, early).DoneBeforeSprint);
        Assert.StartsWith("Done before the sprint", ReportText.Note(Row(r, early)));
        Assert.False(Row(r, inSprint).DoneBeforeSprint);
        Assert.False(Row(r, redone).DoneBeforeSprint);
        Assert.False(Row(r, open).DoneBeforeSprint);
        Assert.Equal(new SprintTaskCounts(4, 3, 1, 0, 0, 0, 1, DoneBeforeSprint: 1), r.Summary.Planned.Tasks); // the counts are as they were

        // A completed sprint: reopened after it ended still reads Done before the sprint; one it handed on never does.
        var completedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var reopenedSince = Task(status: TaskItemStatus.Todo);
        var handedOn = Task(status: TaskItemStatus.Done, completed: Oct(10));
        var ended = Build(Sprint(SprintStatus.Completed, completedAt), [reopenedSince, handedOn],
            completions: [new(SprintId, completedAt, NextSprintId, [handedOn.Task.Id])], asAt: completedAt,
            changes: [Change(reopenedSince, new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc), TaskItemStatus.Done, TaskItemStatus.Todo)]);
        Assert.True(Row(ended, reopenedSince).DoneBeforeSprint);
        Assert.True(Row(ended, reopenedSince).ReopenedSince);
        Assert.False(Row(ended, handedOn).DoneBeforeSprint);
    }

    /// <summary>
    /// RPT-039: an unplanned task already Done when the sprint started, and only logged against in it - an earlier sprint's, a
    /// Backlog task or Not Planned work - is marked Done before the sprint too, its outcome Done and its counts as they are, and it
    /// isn't counted as finished on any day; one reopened in the window and done again isn't marked.
    /// </summary>
    [Fact]
    public void An_unplanned_task_done_before_the_sprint_is_marked()
    {
        var sprint = Sprint();
        var earlier = Task(false, PreviousSprintId, status: TaskItemStatus.Done, sprint: 40, completed: Oct(10));
        var backlog = Backlog(TaskItemStatus.Done, sprint: 20, completed: Oct(12));
        var notPlanned = NotPlanned(TaskItemStatus.Done, sprint: 15, created: Oct(5), completed: Oct(14));
        var redone = Task(false, PreviousSprintId, status: TaskItemStatus.Done, sprint: 10, completed: Oct(23));
        SprintStatusChange[] changes =
        [
            Change(redone, Oct(21), TaskItemStatus.Done, TaskItemStatus.InProgress), Change(redone, Oct(23), TaskItemStatus.InProgress, TaskItemStatus.Done)
        ];
        var r = Build(sprint, [earlier, backlog, notPlanned, redone], changes: changes);

        foreach (var t in new[] { earlier, backlog, notPlanned })
        {
            Assert.True(Row(r, t).DoneBeforeSprint);
            Assert.Equal(SprintTaskOutcome.Done, Row(r, t).Outcome);
        }
        Assert.StartsWith("From Sprint 13 · Done before the sprint", ReportText.Note(Row(r, earlier)));
        Assert.False(Row(r, redone).DoneBeforeSprint);

        var s = r.Summary;
        Assert.Equal(new SprintTaskCounts(2, 2, 0, 0, 0, 0, 0, DoneBeforeSprint: 1), s.OtherSprint.Tasks);
        Assert.Equal(new SprintTaskCounts(1, 1, 0, 0, 0, 0, 0, DoneBeforeSprint: 1), s.Backlog.Tasks);
        Assert.Equal(new SprintTaskCounts(1, 1, 0, 0, 0, 0, 0, DoneBeforeSprint: 1), s.NotPlanned.Tasks);
        Assert.Equal(0, s.Planned.Tasks.DoneBeforeSprint);
        // Only the task done again in the window was finished in it: on the 23rd.
        Assert.Equal(new SprintKindCounts(0, 0, 0, 1), Assert.Single(r.Days, d => d.Finished.Total > 0).Finished);
        Assert.Equal(new DateOnly(2026, 10, 23), Assert.Single(r.Days, d => d.Finished.Total > 0).Date);
    }

    /// <summary>
    /// RPT-040: a task is finished on the last day in the window, up to the as-at time, that it was set to Done - done twice, the later
    /// day; done and reopened since, still its day; failing a change, its completion time - and not at all when that was before the
    /// window or only after it. The day rows count each kind's finished tasks.
    /// </summary>
    [Fact]
    public void Tasks_per_day_count_each_task_once_on_the_day_it_was_finished()
    {
        var sprint = Sprint();
        var twice = Task(status: TaskItemStatus.Done, completed: Oct(27));
        SprintStatusChange[] twiceChanges =
        [
            Change(twice, Oct(21), TaskItemStatus.InProgress, TaskItemStatus.Done), Change(twice, Oct(22), TaskItemStatus.Done, TaskItemStatus.InProgress),
            Change(twice, Oct(27), TaskItemStatus.InProgress, TaskItemStatus.Done)
        ];
        Assert.Equal(new DateOnly(2026, 10, 27), SprintReportRules.FinishedOn(twice, twiceChanges, sprint, AsAt));
        Assert.Equal(new DateOnly(2026, 10, 21), SprintReportRules.FinishedOn(twice, twiceChanges, sprint, Oct(24))); // as at the 24th

        var reopened = Task(status: TaskItemStatus.InProgress);
        Assert.Equal(new DateOnly(2026, 10, 23), SprintReportRules.FinishedOn(reopened,
            [Change(reopened, Oct(23), TaskItemStatus.InProgress, TaskItemStatus.Done), Change(reopened, Oct(25), TaskItemStatus.Done, TaskItemStatus.Todo)],
            sprint, AsAt));
        Assert.Equal(new DateOnly(2026, 10, 20), SprintReportRules.FinishedOn(Task(status: TaskItemStatus.Done, completed: Oct(20)), [], sprint, AsAt));
        Assert.Null(SprintReportRules.FinishedOn(Task(status: TaskItemStatus.Done, completed: Oct(10)), [], sprint, AsAt)); // before it
        Assert.Null(SprintReportRules.FinishedOn(Task(status: TaskItemStatus.Done, completed: Oct(31, 1)), [], sprint, AsAt)); // after it
        Assert.Null(SprintReportRules.FinishedOn(Task(), [], sprint, AsAt));
        var cancelled = Task(status: TaskItemStatus.Cancelled);
        Assert.Null(SprintReportRules.FinishedOn(cancelled, [Change(cancelled, Oct(24), TaskItemStatus.InProgress, TaskItemStatus.Cancelled)], sprint, AsAt));

        var plannedDone = Task(status: TaskItemStatus.Done, completed: Oct(20));
        var support = NotPlanned(TaskItemStatus.Done, completed: Oct(20, 15));
        var pulledIn = Backlog(TaskItemStatus.Done, completed: Oct(22));
        var ahead = Ahead(TaskItemStatus.Done, completed: Oct(22));
        var early = Task(status: TaskItemStatus.Done, completed: Oct(10));
        var r = Build(sprint, [plannedDone, support, pulledIn, ahead, early, twice], changes: twiceChanges, laterSprintIds: [NextSprintId]);
        Assert.Equal(new SprintKindCounts(1, 1, 0, 0), r.Days[1].Finished); // the 20th
        Assert.Equal(new SprintKindCounts(0, 0, 1, 1), r.Days[3].Finished); // the 22nd
        Assert.Equal(new SprintKindCounts(1, 0, 0, 0), r.Days[8].Finished); // the 27th: done twice, counted once
        Assert.Equal(5, r.Days.Sum(d => d.Finished.Total)); // the one done before the sprint isn't counted
        Assert.Equal(SprintKindCounts.None, r.Days[2].Finished); // the 21st: done then, but done again later
    }

    /// <summary>
    /// RPT-040: each person's tasks by outcome take every kind of work and cancelled tasks too, a shared task for each assignee, in the
    /// people rows' order - by time in the sprint - with someone assigned only to cancelled tasks after them and Unassigned last.
    /// </summary>
    [Fact]
    public void Tasks_by_person_count_every_task_each_assignee_has()
    {
        var shared = Task(project: Alpha, status: TaskItemStatus.Done, assignees: [Alice, Bob], completed: Oct(22));
        var bobs = Task(assignees: [Bob], sprint: 120);
        var dropped = Task(status: TaskItemStatus.Cancelled, assignees: [Carol]);
        var support = NotPlanned(assignees: [Alice], sprint: 30);
        var nobodys = Task();
        SprintPersonTime[] time = [new(bobs.Task.Id, Bob, 120, 120), new(support.Task.Id, Alice, 30, 30)];
        var r = Build(Sprint(), [shared, bobs, dropped, support, nobodys], time);

        Assert.Equal(["Bob", "Alice", "Unassigned"], r.People.Select(p => p.Name)); // Carol has only a cancelled task
        Assert.Equal(
            [
                new SprintPersonTasks(Bob, "Bob", 1, 1, 0),
                new SprintPersonTasks(Alice, "Alice", 1, 1, 0),
                new SprintPersonTasks(Carol, "Carol", 0, 0, 1),
                new SprintPersonTasks(null, "Unassigned", 0, 1, 0)
            ],
            r.PersonTasks);
        Assert.Equal(2, r.PersonTasks[0].Total);
        Assert.Empty(SprintStatusReport.Empty(AsAt).PersonTasks);
    }

    /// <summary>
    /// RPT-041: every task logged against or set to Done in the window is in exactly one row, wherever it is - in the sprint, handed on
    /// by it, Not Planned, on the Backlog, in a later sprint, in an earlier one or in one starting the same day - so the report's time in
    /// the sprint is all the time logged in its window; a task with nothing in the window (or only created on the Backlog) is in none.
    /// </summary>
    [Fact]
    public void Every_task_worked_on_in_the_window_is_in_exactly_one_row()
    {
        var completedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var inSprint = Task(sprint: 30);
        var handedOn = Ahead(sprint: 30);
        var notPlanned = NotPlanned(created: Oct(1), sprint: 30);
        var backlog = Backlog(sprint: 30);
        var later = Ahead(sprint: 30);
        var earlier = Task(false, PreviousSprintId, status: TaskItemStatus.Done, completed: Oct(10), sprint: 30); // reopened? logged against
        var redoneEarlier = Task(false, PreviousSprintId, status: TaskItemStatus.Done, completed: Oct(27)); // done again, no time
        var sameStart = Task(false, OtherSprintId, sprint: 30); // not a later sprint
        // Each once, as its kind of work - all on no project, so all on the No project row.
        (SprintTaskFacts Task, SprintWorkKind Work)[] worked =
        [
            (inSprint, SprintWorkKind.Planned), (handedOn, SprintWorkKind.Planned), (notPlanned, SprintWorkKind.NotPlanned),
            (backlog, SprintWorkKind.Backlog), (later, SprintWorkKind.OtherSprint), (earlier, SprintWorkKind.OtherSprint),
            (redoneEarlier, SprintWorkKind.OtherSprint), (sameStart, SprintWorkKind.OtherSprint)
        ];
        SprintTaskFacts[] quiet =
        [
            Backlog(created: Oct(22)), NotPlanned(created: Oct(1)), Ahead(), Task(false, PreviousSprintId, status: TaskItemStatus.Done, completed: Oct(10))
        ];
        var time = worked.Where(w => w.Task.SprintMinutes > 0).Select(w => new SprintPersonTime(w.Task.Task.Id, Alice, 30, 30)).ToArray();

        var r = Build(Sprint(SprintStatus.Completed, completedAt), [.. worked.Select(w => w.Task), .. quiet], time,
            completions: [new(SprintId, completedAt, NextSprintId, [handedOn.Task.Id])], asAt: completedAt, laterSprintIds: [NextSprintId]);

        Assert.Null(Assert.Single(r.Rows).Project);
        var rows = r.Rows.SelectMany(g => g.TaskRows).ToList();
        foreach (var (task, work) in worked)
            Assert.Equal(work, Assert.Single(rows, x => x.TaskId == task.Task.Id).Work);
        foreach (var task in quiet)
            Assert.DoesNotContain(rows, x => x.TaskId == task.Task.Id);
        Assert.Equal(time.Sum(t => t.SprintMinutes), r.Summary.SprintMinutes); // all the time logged in the window
        Assert.Equal(r.Summary.SprintMinutes, r.People.Sum(p => p.SprintMinutes));
    }

    /// <summary>RPT-024: while the sprint runs, outcomes are the tasks' status as at the as-at time; nothing has gone anywhere yet.</summary>
    [Fact]
    public void An_active_sprint_reads_outcomes_from_the_status_now()
    {
        var done = Task(status: TaskItemStatus.Done, completed: Oct(22));
        var cancelled = Task(status: TaskItemStatus.Cancelled);
        var open = Task(status: TaskItemStatus.Blocked);
        var supportDone = NotPlanned(TaskItemStatus.Done, completed: Oct(29));
        var supportDoneLater = NotPlanned(TaskItemStatus.Done, created: Oct(29), completed: Oct(30, 16)); // after the as-at time
        var r = Build(Sprint(), [done, cancelled, open, supportDone, supportDoneLater],
            changes: [Change(supportDoneLater, Oct(30, 16), TaskItemStatus.InProgress, TaskItemStatus.Done)]);

        Assert.Equal(SprintTaskOutcome.Done, Row(r, done).Outcome);
        Assert.Equal(SprintTaskOutcome.Cancelled, Row(r, cancelled).Outcome);
        Assert.Equal(SprintTaskOutcome.LeftOpen, Row(r, open).Outcome);
        Assert.Equal(SprintTaskOutcome.Done, Row(r, supportDone).Outcome);
        Assert.Equal(SprintTaskOutcome.LeftOpen, Row(r, supportDoneLater).Outcome);
        Assert.Null(r.LeftOpenTo);
        Assert.All(r.Rows.SelectMany(g => g.TaskRows), x => Assert.Null(x.LeftOpenTo));
        Assert.All(r.Rows.SelectMany(g => g.TaskRows), x => Assert.False(x.ReopenedSince));
        Assert.Equal(new SprintTaskCounts(3, 1, 1, 1, 0, 0, 1), r.Summary.Planned.Tasks);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 0, 1), r.Summary.NotPlanned.Tasks);
    }

    /// <summary>
    /// RPT-025: once the sprint has ended, the open tasks it handed on are still its own, Left open to where they went, whatever has
    /// become of them since; a task Done at the end and reopened since counts as Done; tasks carried in name the sprint before.
    /// </summary>
    [Fact]
    public void A_completed_sprint_keeps_the_tasks_it_handed_on()
    {
        var completedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var handedOn = Task(sprintId: NextSprintId, status: TaskItemStatus.Done, completed: Oct(31, 12)); // done in the next sprint
        var stillOpen = Task(sprintId: NextSprintId);
        var reopened = Task(status: TaskItemStatus.InProgress);
        var carriedIn = Task(status: TaskItemStatus.Done, completed: Oct(27));
        var unrelated = Task(sprintId: NextSprintId); // planned into the next sprint, never in this one
        SprintCompletion[] completions =
        [
            new(SprintId, completedAt, NextSprintId, [handedOn.Task.Id, stillOpen.Task.Id]),
            new(PreviousSprintId, Oct(19, 7), SprintId, [carriedIn.Task.Id])
        ];
        var r = Build(Sprint(SprintStatus.Completed, completedAt), [handedOn, stillOpen, reopened, carriedIn, unrelated],
            completions: completions, asAt: completedAt,
            changes:
            [
                Change(reopened, Oct(24), TaskItemStatus.InProgress, TaskItemStatus.Done),
                Change(reopened, Oct(31, 12), TaskItemStatus.Done, TaskItemStatus.InProgress),
                Change(handedOn, Oct(31, 12), TaskItemStatus.InProgress, TaskItemStatus.Done)
            ]);

        Assert.Equal("Sprint 15", r.LeftOpenTo);
        Assert.Equal(SprintTaskOutcome.LeftOpen, Row(r, handedOn).Outcome);
        Assert.Equal("Sprint 15", Row(r, handedOn).LeftOpenTo);
        Assert.Equal(SprintTaskOutcome.LeftOpen, Row(r, stillOpen).Outcome);
        var reopenedRow = Row(r, reopened);
        Assert.Equal(SprintTaskOutcome.Done, reopenedRow.Outcome);
        Assert.True(reopenedRow.ReopenedSince);
        Assert.Equal("Sprint 13", Row(r, carriedIn).CarriedInFrom);
        Assert.DoesNotContain(r.Rows.SelectMany(g => g.TaskRows), x => x.TaskId == unrelated.Task.Id);
        Assert.Equal(new SprintTaskCounts(4, 2, 2, 0, 1, 0, 2), r.Summary.Planned.Tasks);

        // Completed without a successor: the open tasks went back to the Backlog.
        var returned = Task(planned: false, inBacklog: true);
        r = Build(Sprint(SprintStatus.Completed, completedAt), [returned],
            completions: [new(SprintId, completedAt, null, [returned.Task.Id])], asAt: completedAt);
        Assert.Equal(SprintReportRules.Backlog, r.LeftOpenTo);
        Assert.Equal(SprintReportRules.Backlog, Row(r, returned).LeftOpenTo);
    }

    /// <summary>
    /// RPT-025: a completed sprint's outcomes are each task's status when it ended, read back through the changes since, so later
    /// changes don't rewrite them: cancelled then reopened stays Cancelled, Done then cancelled stays Done, and Not Planned work open
    /// then and cancelled since stays Left open, or done in the window and reopened since stays Done. A task closed then and open now
    /// is marked Reopened since.
    /// </summary>
    [Fact]
    public void Later_changes_do_not_rewrite_a_completed_sprint()
    {
        var completedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var cancelledThenReopened = Task(status: TaskItemStatus.InProgress);
        var doneThenCancelled = Task(status: TaskItemStatus.Cancelled);
        var supportCancelledSince = NotPlanned(TaskItemStatus.Cancelled, created: Oct(22));
        var supportReopened = NotPlanned(TaskItemStatus.Todo, created: Oct(1)); // counted only through its change to Done
        var after = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var r = Build(Sprint(SprintStatus.Completed, completedAt),
            [cancelledThenReopened, doneThenCancelled, supportCancelledSince, supportReopened],
            completions: [new(SprintId, completedAt, NextSprintId, [])], asAt: completedAt,
            changes:
            [
                Change(cancelledThenReopened, Oct(27), TaskItemStatus.InProgress, TaskItemStatus.Cancelled),
                Change(cancelledThenReopened, after, TaskItemStatus.Cancelled, TaskItemStatus.InProgress),
                Change(doneThenCancelled, Oct(28), TaskItemStatus.InProgress, TaskItemStatus.Done),
                Change(doneThenCancelled, after, TaskItemStatus.Done, TaskItemStatus.Cancelled),
                Change(supportCancelledSince, after, TaskItemStatus.InProgress, TaskItemStatus.Cancelled),
                Change(supportReopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Done),
                Change(supportReopened, after, TaskItemStatus.Done, TaskItemStatus.Todo)
            ]);

        var cancelled = Row(r, cancelledThenReopened);
        Assert.Equal(SprintTaskOutcome.Cancelled, cancelled.Outcome);
        Assert.True(cancelled.ReopenedSince);
        Assert.Equal(TaskItemStatus.InProgress, cancelled.Status); // the chip shows the status now
        var done = Row(r, doneThenCancelled);
        Assert.Equal(SprintTaskOutcome.Done, done.Outcome);
        Assert.False(done.ReopenedSince);
        Assert.Equal(SprintTaskOutcome.LeftOpen, Row(r, supportCancelledSince).Outcome);
        var reopened = Row(r, supportReopened);
        Assert.Equal(SprintTaskOutcome.Done, reopened.Outcome);
        Assert.True(reopened.ReopenedSince);
        Assert.Equal(new SprintTaskCounts(2, 1, 0, 1, 0, 0, 0), r.Summary.Planned.Tasks);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 0, 1), r.Summary.NotPlanned.Tasks);
    }

    /// <summary>
    /// RPT-033: a task the sprint handed on and that has been cancelled since is Left open everywhere - the group's counts and the
    /// estimate left, its person's row, the people's planned work and the estimates - so the rows add up to the totals.
    /// </summary>
    [Fact]
    public void A_task_handed_on_and_cancelled_since_is_left_open_throughout()
    {
        var completedAt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var handedOn = Task(sprintId: NextSprintId, status: TaskItemStatus.Cancelled, project: Alpha, assignees: [Alice], estimate: 240, total: 60);
        var done = Task(status: TaskItemStatus.Done, project: Alpha, assignees: [Alice], estimate: 60, total: 60, completed: Oct(24));
        var r = Build(Sprint(SprintStatus.Completed, completedAt), [handedOn, done],
            completions: [new(SprintId, completedAt, NextSprintId, [handedOn.Task.Id])], asAt: completedAt,
            changes: [Change(handedOn, new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc), TaskItemStatus.InProgress, TaskItemStatus.Cancelled)]);

        var row = Row(r, handedOn);
        Assert.Equal(SprintTaskOutcome.LeftOpen, row.Outcome);
        Assert.False(row.IsCancelled);
        Assert.Equal("Left open → Sprint 15", ReportText.Note(row));
        var alpha = Assert.Single(r.Rows);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 180, 0), alpha.Tasks);
        Assert.Equal(new SprintPersonRow(Alice, "Alice", 2, 1, 1, 2, 300, 0, 0), Assert.Single(alpha.People));
        Assert.Equal(new SprintImpactRow(Alice, "Alice", 1, 1, new(2, 0, 0, 0), new(0, 0, 0, 0)), Assert.Single(r.People));
        Assert.Equal(new EstimateComparison(2, 300, 120, 0), r.Summary.Planned.Estimate);
        Assert.Equal(r.Summary.Planned.Tasks.Total - r.Summary.Planned.Tasks.Cancelled, r.People.Sum(p => p.PlannedTasks));
        Assert.Equal(r.Summary.Planned.Tasks.LeftOpen, r.People.Sum(p => p.PlannedLeftOpen));
    }

    /// <summary>
    /// RPT-034: a task's status at a moment is what the first change after it changed from, or its status now; the sprints offered
    /// are the active one, then the completed ones latest completed first - one with no completion time by the end of its window, not
    /// first - and never a planned one, the first of them the default.
    /// </summary>
    [Fact]
    public void Status_at_a_moment_and_the_sprints_offered()
    {
        var t = Task(status: TaskItemStatus.Done);
        SprintStatusChange[] changes =
        [
            Change(t, Oct(25), TaskItemStatus.InProgress, TaskItemStatus.Done), Change(t, Oct(20), TaskItemStatus.Todo, TaskItemStatus.InProgress)
        ];
        Assert.Equal(TaskItemStatus.Todo, SprintReportRules.StatusAt(TaskItemStatus.Done, changes, Oct(19)));
        Assert.Equal(TaskItemStatus.InProgress, SprintReportRules.StatusAt(TaskItemStatus.Done, changes, Oct(22)));
        Assert.Equal(TaskItemStatus.Done, SprintReportRules.StatusAt(TaskItemStatus.Done, changes, Oct(26)));
        Assert.Equal(TaskItemStatus.Blocked, SprintReportRules.StatusAt(TaskItemStatus.Blocked, [], Oct(26)));

        static SprintFacts S(string name, SprintStatus status, DateOnly end, DateTime? completedAt = null) =>
            new(Guid.NewGuid(), name, null, end.AddDays(-11), end, status, completedAt);
        var planned = S("Sprint 16", SprintStatus.Planned, new DateOnly(2026, 11, 13));
        var active = S("Sprint 15", SprintStatus.Active, new DateOnly(2026, 10, 30));
        var latest = S("Sprint 14", SprintStatus.Completed, new DateOnly(2026, 10, 16), new DateTime(2026, 10, 19, 7, 0, 0, DateTimeKind.Utc));
        var earlier = S("Sprint 13", SprintStatus.Completed, new DateOnly(2026, 10, 2), new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc));
        var legacy = S("Sprint 9", SprintStatus.Completed, new DateOnly(2026, 8, 7)); // no completion time recorded

        Assert.Equal([active, latest, earlier, legacy], SprintReportRules.Offered([legacy, earlier, planned, latest, active]));
        Assert.Equal(latest, SprintReportRules.Offered([legacy, earlier, planned, latest])[0]);
        Assert.Empty(SprintReportRules.Offered([planned]));
    }

    /// <summary>
    /// RPT-035: the time read once, by task, person and day, splits into each task's time in the window and to date, each person's
    /// on each task, and everyone's on each day of the window.
    /// </summary>
    [Fact]
    public void Time_splits_into_tasks_people_and_days()
    {
        var a = Task(total: 999, sprint: 999); // the time read replaces what the facts held
        var b = Task();
        SprintTimeEntry[] entries =
        [
            new(a.Task.Id, Alice, Start, 60), new(a.Task.Id, Alice, Start.AddDays(-1), 30),
            new(a.Task.Id, Bob, End, 45), new(a.Task.Id, Bob, End.AddDays(1), 15)
        ];
        var (tasks, time, days) = SprintReportRules.WithTime([a, b], entries, Sprint());

        Assert.Equal((105, 150), (tasks[0].SprintMinutes, tasks[0].Task.TotalMinutes));
        Assert.Equal((0, 0), (tasks[1].SprintMinutes, tasks[1].Task.TotalMinutes));
        Assert.Equal([new SprintPersonTime(a.Task.Id, Alice, 60, 90), new SprintPersonTime(a.Task.Id, Bob, 45, 60)],
            time.OrderBy(x => x.UserId == Bob));
        Assert.Equal([new SprintDayTime(a.Task.Id, Start, 60), new SprintDayTime(a.Task.Id, End, 45)], days.OrderBy(d => d.Date));
    }

    /// <summary>
    /// RPT-026: one row per project, by department then name, then the tasks on no project; a project's row carries its status now,
    /// for the chip beside its name (decision 131); a Not Planned task counts on its own project, giving it a row and a project
    /// option, and is marked Not Planned.
    /// </summary>
    [Fact]
    public void Rows_are_the_projects_then_no_project()
    {
        var onAlpha = Task(project: Alpha);
        var onBeta = Task(project: Beta);
        var standalone = Task();
        var support = NotPlanned(project: Gamma);
        var r = Build(Sprint(), [support, standalone, onAlpha, onBeta]);

        Assert.Equal([Beta, Alpha, Gamma, null], r.Rows.Select(g => g.Project));
        Assert.Equal(["Beta", "Alpha", "Gamma", "No project"], r.Rows.Select(g => g.Name));
        Assert.Equal([ProjectStatus.OnHold, ProjectStatus.Active, ProjectStatus.Active], r.Rows.Take(3).Select(g => g.Project!.Status));
        Assert.Equal([Beta, Alpha, Gamma], r.ProjectOptions);
        var supportRow = Assert.Single(r.Rows[2].TaskRows);
        Assert.Equal((support.Task.Id, SprintWorkKind.NotPlanned, "Not Planned · No estimate"),
            (supportRow.TaskId, supportRow.Work, ReportText.Note(supportRow)));
        Assert.Equal(Alpha.Id.ToString(), r.Rows[1].Key);
        Assert.Equal("NoProject", r.Rows[3].Key);
    }

    /// <summary>
    /// RPT-043: a project's row holds its planned tasks, then the Not Planned, Backlog and other sprints' tasks worked on on it, each
    /// part by outcome; its figures cover them all and its unplanned part is set apart, so the rows add up to all the work. A project
    /// with only unplanned work has a row and a project option, and unplanned work on no project is on the No project row, last. The
    /// summary still splits the work by kind.
    /// </summary>
    [Fact]
    public void A_project_row_holds_its_planned_tasks_then_its_unplanned_ones()
    {
        var plannedDone = Task(project: Alpha, status: TaskItemStatus.Done, estimate: 60, sprint: 60, total: 60, completed: Oct(21));
        var plannedOpen = Task(project: Alpha, estimate: 120, sprint: 30, total: 30);
        var fromBacklog = Backlog(TaskItemStatus.Done, project: Alpha, sprint: 45, total: 45, completed: Oct(24));
        var ahead = Ahead(project: Alpha, estimate: 90, sprint: 15, total: 15);
        var support = NotPlanned(project: Alpha, sprint: 20, total: 20);
        var gammaOnly = Backlog(project: Gamma, sprint: 10, total: 10);
        var standaloneAhead = Ahead(sprint: 5, total: 5);
        SprintTaskFacts[] tasks = [support, ahead, gammaOnly, fromBacklog, plannedOpen, standaloneAhead, plannedDone];
        Guid[] later = [NextSprintId];
        var r = Build(Sprint(), tasks, laterSprintIds: later);

        Assert.Equal([Alpha, Gamma, null], r.Rows.Select(g => g.Project));
        Assert.Equal(["Alpha", "Gamma", "No project"], r.Rows.Select(g => g.Name));
        Assert.Equal([Alpha, Gamma], r.ProjectOptions);

        // Alpha: its planned tasks by outcome, then its Not Planned, Backlog and other-sprint ones by outcome.
        var alpha = r.Rows[0];
        Assert.Equal(
            [
                (plannedDone.Task.Id, SprintWorkKind.Planned), (plannedOpen.Task.Id, SprintWorkKind.Planned),
                (fromBacklog.Task.Id, SprintWorkKind.Backlog), (ahead.Task.Id, SprintWorkKind.OtherSprint),
                (support.Task.Id, SprintWorkKind.NotPlanned)
            ],
            alpha.TaskRows.Select(x => (x.TaskId, x.Work)));
        Assert.Equal(new SprintTaskCounts(5, 2, 3, 0, 0, 90 + 75, 1), alpha.Tasks);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 90, 0), alpha.PlannedTasks);
        Assert.Equal(new SprintTaskCounts(3, 1, 2, 0, 0, 75, 1), alpha.UnplannedTasks);
        Assert.Equal([plannedDone.Task.Id, plannedOpen.Task.Id], alpha.PlannedTaskRows.Select(x => x.TaskId));
        Assert.Equal([fromBacklog.Task.Id, ahead.Task.Id, support.Task.Id], alpha.UnplannedTaskRows.Select(x => x.TaskId));
        Assert.Equal(new SprintKindCounts(90, 20, 45, 15), alpha.Minutes);
        Assert.Equal((170, 170, 80), (alpha.SprintMinutes, alpha.ToDateMinutes, alpha.UnplannedSprintMinutes));
        Assert.Equal(new EstimateComparison(3, 270, 105, 0), alpha.Estimate);
        Assert.Equal("Planned 1/2 · unplanned 1/3", ReportText.PlannedSplit(alpha));
        // Of its 3 left open, 2 weren't planned (decision 130): the other sprint's, 75m of its estimate left, and the unestimated
        // Not Planned one.
        Assert.Equal("2 unplanned (1h 15m left)", ReportText.UnplannedLeftOpen(alpha));
        Assert.Equal("From the Backlog · No estimate", ReportText.Note(Row(r, fromBacklog)));
        Assert.Equal("Not Planned · No estimate", ReportText.Note(Row(r, support)));

        // Gamma has only Backlog work: a row of its own, all of it unplanned.
        var gamma = r.Rows[1];
        Assert.Equal(gamma.Tasks, gamma.UnplannedTasks);
        Assert.Equal("No planned tasks · unplanned 0/1", ReportText.PlannedSplit(gamma));
        Assert.Equal("1 unplanned", ReportText.UnplannedLeftOpen(gamma)); // unestimated

        // Another sprint's task on no project is on the No project row, last.
        var standaloneRow = Assert.Single(r.Rows[2].TaskRows);
        Assert.Equal((standaloneAhead.Task.Id, SprintWorkKind.OtherSprint, "Planned for Sprint 15 · No estimate"),
            (standaloneRow.TaskId, standaloneRow.Work, ReportText.Note(standaloneRow)));

        // The summary splits the work by kind as before, and the rows add up to all of it.
        var s = r.Summary;
        Assert.Equal((2, 1, 2, 2), (s.Planned.Tasks.Total, s.NotPlanned.Tasks.Total, s.Backlog.Tasks.Total, s.OtherSprint.Tasks.Total));
        Assert.Equal(s.All, r.Rows.Aggregate(SprintTaskCounts.None, (sum, g) => sum.Plus(g.Tasks)));
        Assert.Equal(s.SprintMinutes, r.Rows.Sum(g => g.SprintMinutes));
        Assert.Equal(s.UnplannedSprintMinutes, r.Rows.Sum(g => g.UnplannedSprintMinutes));

        // Picking Alpha picks every kind of task on it, all on its one row.
        var picked = Build(Sprint(), tasks, projectId: Alpha.Id, laterSprintIds: later);
        Assert.Equal(5, Assert.Single(picked.Rows).TaskRows.Count());
    }

    /// <summary>
    /// RPT-042: the projects charts draw the By project rows (decision 130), which count every kind of work on its own project - a
    /// project's planned tasks with its Not Planned, Backlog and other sprints' ones, by outcome, and its time on each kind - so a
    /// project with only unplanned work has a bar. In the projects' order, the tasks on no project last; only the tasks the filters
    /// pick.
    /// </summary>
    [Fact]
    public void Project_work_counts_every_kind_of_work_on_its_project()
    {
        var alphaPlanned = Task(project: Alpha, status: TaskItemStatus.Done, sprint: 60, completed: Oct(21));
        var alphaSupport = NotPlanned(project: Alpha, sprint: 30);
        var gammaBacklog = Backlog(project: Gamma, sprint: 45);
        var betaPlanned = Task(project: Beta, status: TaskItemStatus.Cancelled);
        var betaAhead = Ahead(project: Beta, sprint: 15);
        var standalone = Task(sprint: 20);
        var support = NotPlanned(sprint: 10);
        SprintTaskFacts[] tasks = [support, gammaBacklog, alphaSupport, standalone, betaAhead, alphaPlanned, betaPlanned];
        var r = Build(Sprint(), tasks);

        Assert.Equal([Beta, Alpha, Gamma, null], r.Rows.Select(g => g.Project));
        Assert.Equal(
            [new SprintKindCounts(0, 0, 0, 15), new SprintKindCounts(60, 30, 0, 0), new SprintKindCounts(0, 0, 45, 0),
             new SprintKindCounts(20, 10, 0, 0)],
            r.Rows.Select(g => g.Minutes));
        Assert.Equal(
            [(2, 0, 1, 1), (2, 1, 1, 0), (1, 0, 1, 0), (2, 0, 2, 0)],
            r.Rows.Select(g => (g.Tasks.Total, g.Tasks.Done, g.Tasks.LeftOpen, g.Tasks.Cancelled)));
        Assert.Equal(r.Summary.SprintMinutes, r.Rows.Sum(g => g.SprintMinutes)); // every minute on one bar
        // Alpha's Not Planned task is on Alpha's row (RPT-043).
        Assert.Contains(r.Rows.Single(g => g.Project == Alpha).TaskRows, x => x.TaskId == alphaSupport.Task.Id);

        Assert.Equal([Alpha], Build(Sprint(), tasks, projectId: Alpha.Id).Rows.Select(g => g.Project));
    }

    /// <summary>
    /// RPT-027: the summary counts each task once, splits time in the sprint and to date between planned and Not Planned work, and
    /// compares estimates over the estimated tasks that aren't Cancelled.
    /// </summary>
    [Fact]
    public void The_summary_counts_planned_and_not_planned_work_apart()
    {
        var shared = Task(status: TaskItemStatus.Done, assignees: [Alice, Bob], estimate: 60, total: 90, sprint: 90, completed: Oct(23));
        var open = Task(assignees: [Alice], estimate: 120, total: 150, sprint: 30);
        var cancelled = Task(status: TaskItemStatus.Cancelled, assignees: [Bob], estimate: 30, total: 10, sprint: 10);
        var support = NotPlanned(TaskItemStatus.Done, assignees: [Bob], total: 60, sprint: 60, completed: Oct(28));
        var r = Build(Sprint(), [shared, open, cancelled, support]);
        var s = r.Summary;

        Assert.Equal(new SprintTaskCounts(3, 1, 1, 1, 0, 0, 0), s.Planned.Tasks);
        Assert.Equal(new SprintTaskCounts(1, 1, 0, 0, 0, 0, 0), s.NotPlanned.Tasks);
        Assert.Equal(4, s.All.Total);
        Assert.Equal(130, s.Planned.SprintMinutes);
        Assert.Equal(60, s.NotPlanned.SprintMinutes);
        Assert.Equal(250, s.Planned.ToDateMinutes);
        Assert.Equal(60, s.NotPlanned.ToDateMinutes);
        Assert.Equal(32, s.UnplannedPercent); // 60 of 190
        Assert.Equal(new EstimateComparison(2, 180, 240, 2), s.Planned.Estimate);
        Assert.Equal(EstimateComparison.None, s.NotPlanned.Estimate);
        Assert.Equal(new EstimateComparison(2, 180, 240, 2), s.AllEstimate);
        Assert.Equal(33, s.Planned.Tasks.PercentDone);

        Assert.Null(Build(Sprint(), [Task()]).Summary.UnplannedPercent); // nothing logged
    }

    /// <summary>
    /// RPT-027: the PDF's share row splits the unplanned share over its kinds so they add up to it, and planned takes what is left:
    /// the row adds up to 100%, where rounding each kind on its own needn't.
    /// </summary>
    [Fact]
    public void The_unplanned_share_is_split_over_its_kinds_without_losing_a_point()
    {
        // 300 minutes, a third on each unplanned kind: 33% each would make 99%.
        Assert.Equal(100, SprintReportShare.Percent(300, 300));
        Assert.Equal([34, 33, 33], SprintReportShare.Apportion(100, [100, 100, 100]));
        // 15 of 1000 minutes, 5 on each: 2% unplanned, which 0% each would lose.
        Assert.Equal(2, SprintReportShare.Percent(15, 1000));
        Assert.Equal([1, 1, 0], SprintReportShare.Apportion(2, [5, 5, 5]));
        // In proportion, largest remainder first: 70% over 50/30/20 minutes is 35/21/14.
        Assert.Equal([35, 21, 14], SprintReportShare.Apportion(70, [50, 30, 20]));
        Assert.Equal([0, 3, 0], SprintReportShare.Apportion(3, [0, 7, 0]));
        Assert.Equal([0, 0], SprintReportShare.Apportion(0, [0, 0]));
        Assert.Equal([0, 0], SprintReportShare.Apportion(5, [0, 0]));
        Assert.Empty(SprintReportShare.Apportion(0, []));
    }

    /// <summary>
    /// RPT-028: a group's people are its assignees and whoever logged time on it - a shared task whole under each, an Unassigned row
    /// last - and the people rows set each person's planned work against their Not Planned work.
    /// </summary>
    [Fact]
    public void People_rows_set_planned_against_not_planned_work()
    {
        var shared = Task(project: Alpha, status: TaskItemStatus.Done, assignees: [Alice, Bob], estimate: 60, completed: Oct(23));
        var alices = Task(project: Alpha, assignees: [Alice], estimate: 30);
        var nobodys = Task(project: Alpha);
        var support = NotPlanned(assignees: [Bob]);
        SprintPersonTime[] time =
        [
            new(shared.Task.Id, Alice, 60, 90), new(shared.Task.Id, Carol, 30, 30), new(alices.Task.Id, Alice, 20, 20),
            new(support.Task.Id, Bob, 120, 120)
        ];
        var r = Build(Sprint(), [shared, alices, nobodys, support], time);

        var alpha = r.Rows[0];
        Assert.Equal(
            [
                new SprintPersonRow(Alice, "Alice", 2, 1, 1, 2, 90, 80, 110),
                new SprintPersonRow(Carol, "Carol", 0, 0, 0, 0, 0, 30, 30),
                new SprintPersonRow(Bob, "Bob", 1, 1, 0, 1, 60, 0, 0),
                new SprintPersonRow(null, "Unassigned", 1, 0, 1, 0, 0, 0, 0)
            ],
            alpha.People);

        Assert.Equal(
            [
                new SprintImpactRow(Bob, "Bob", 1, 0, new(1, 1, 0, 0), new(0, 120, 0, 0)),
                new SprintImpactRow(Alice, "Alice", 1, 1, new(2, 0, 0, 0), new(80, 0, 0, 0)),
                new SprintImpactRow(Carol, "Carol", 0, 0, new(0, 0, 0, 0), new(30, 0, 0, 0)),
                new SprintImpactRow(null, "Unassigned", 0, 1, new(1, 0, 0, 0), new(0, 0, 0, 0))
            ],
            r.People);
        Assert.Equal(100, r.People[0].UnplannedPercent);
        Assert.Equal(0, r.People[1].UnplannedPercent);
        Assert.Null(r.People[3].UnplannedPercent);
    }

    /// <summary>RPT-029: the project and assignee filters pick tasks, planned and Not Planned alike, after the options are worked out.</summary>
    [Fact]
    public void Filters_pick_tasks_after_the_options_are_worked_out()
    {
        var alpha = Task(project: Alpha, assignees: [Alice]);
        var beta = Task(project: Beta, assignees: [Bob]);
        var shared = Task(project: Beta, assignees: [Alice, Bob]);
        var supportOnAlpha = NotPlanned(project: Alpha, assignees: [Carol]);
        var support = NotPlanned(assignees: [Bob]);
        SprintTaskFacts[] tasks = [alpha, beta, shared, supportOnAlpha, support];

        var byProject = Build(Sprint(), tasks, projectId: Alpha.Id);
        Assert.Equal([alpha.Task.Id, supportOnAlpha.Task.Id], byProject.Rows.SelectMany(g => g.TaskRows).Select(x => x.TaskId));
        Assert.Equal([Beta, Alpha], byProject.ProjectOptions);
        Assert.Equal(["Alice", "Bob", "Carol"], byProject.AssigneeOptions.Select(p => p.Name));

        var byPerson = Build(Sprint(), tasks, assigneeId: Bob);
        Assert.Equal([beta.Task.Id, shared.Task.Id, support.Task.Id], byPerson.Rows.SelectMany(g => g.TaskRows).Select(x => x.TaskId));
        Assert.Equal(3, byPerson.Summary.All.Total);
        Assert.Equal(["Alice", "Bob", "Carol"], byPerson.AssigneeOptions.Select(p => p.Name));
    }

    /// <summary>RPT-030: the estimate left on the left-open tasks is never negative, and the unestimated ones are counted apart.</summary>
    [Fact]
    public void The_estimate_left_on_left_open_tasks_is_never_negative()
    {
        Assert.Equal(90, SprintReportRules.Remaining(120, 30));
        Assert.Equal(0, SprintReportRules.Remaining(60, 90));

        var r = Build(Sprint(),
        [
            Task(estimate: 120, total: 30), Task(estimate: 60, total: 90), Task(), Task(),
            Task(status: TaskItemStatus.Done, estimate: 500, completed: Oct(20))
        ]);
        Assert.Equal(90, r.Summary.Planned.Tasks.LeftOpenRemainingMinutes);
        Assert.Equal(2, r.Summary.Planned.Tasks.LeftOpenUnestimated);
        Assert.Equal(4, r.Summary.Planned.Tasks.LeftOpen);
    }

    /// <summary>RPT-031: one day row for every day of the sprint, empty ones too, planned and Not Planned apart, for the tasks picked.</summary>
    [Fact]
    public void Day_rows_cover_every_day_of_the_sprint()
    {
        var planned = Task(assignees: [Alice]);
        var other = Task(assignees: [Bob]);
        var support = NotPlanned(assignees: [Alice]);
        SprintDayTime[] days =
        [
            new(planned.Task.Id, Start, 60), new(planned.Task.Id, Start, 30), new(support.Task.Id, Start, 45),
            new(other.Task.Id, Start.AddDays(1), 120), new(support.Task.Id, End, 15),
            new(planned.Task.Id, End.AddDays(1), 999) // after the sprint
        ];
        var r = Build(Sprint(), [planned, other, support], days: days);
        Assert.Equal(12, r.Days.Count);
        Assert.Equal(Start, r.Days[0].Date);
        Assert.Equal(End, r.Days[^1].Date);
        Assert.Equal((Start, new SprintKindCounts(90, 45, 0, 0)), (r.Days[0].Date, r.Days[0].Minutes));
        Assert.Equal((Start.AddDays(1), new SprintKindCounts(120, 0, 0, 0)), (r.Days[1].Date, r.Days[1].Minutes));
        Assert.Equal((End, new SprintKindCounts(0, 15, 0, 0)), (r.Days[^1].Date, r.Days[^1].Minutes));
        Assert.Equal(0, r.Days[5].Minutes.Total);

        var alices = Build(Sprint(), [planned, other, support], days: days, assigneeId: Alice);
        Assert.Equal((Start.AddDays(1), new SprintKindCounts(0, 0, 0, 0)), (alices.Days[1].Date, alices.Days[1].Minutes));
    }
}
