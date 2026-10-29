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

    private static readonly SprintProjectFacts Alpha = new(Guid.NewGuid(), "P-26-00001", "Alpha", "Ops");
    private static readonly SprintProjectFacts Beta = new(Guid.NewGuid(), "P-26-00002", "Beta", "Dev");
    private static readonly SprintProjectFacts Gamma = new(Guid.NewGuid(), "P-26-00003", "Gamma", "Ops");

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

    private static SprintStatusReport Build(
        SprintFacts sprint, SprintTaskFacts[] tasks, SprintPersonTime[]? time = null, SprintDayTime[]? days = null,
        SprintCompletion[]? completions = null, Guid? projectId = null, Guid? assigneeId = null, DateTime? asAt = null,
        SprintStatusChange[]? changes = null) =>
        SprintReportRules.Build(sprint, tasks, time ?? [], days ?? [], changes ?? [], Projects, completions ?? [], SprintNames, People,
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
        Assert.True(SprintReportRules.CountsAsNotPlanned(NotPlanned(created: Oct(19, 0)), sprint));
        Assert.True(SprintReportRules.CountsAsNotPlanned(NotPlanned(created: new DateTime(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc)), sprint));
        Assert.True(SprintReportRules.CountsAsNotPlanned(
            NotPlanned(TaskItemStatus.Done, created: Oct(1), completed: Oct(25)), sprint));
        Assert.True(SprintReportRules.CountsAsNotPlanned(NotPlanned(created: Oct(1), sprint: 30), sprint));

        Assert.False(SprintReportRules.CountsAsNotPlanned(NotPlanned(created: Oct(1)), sprint)); // nothing in the window
        Assert.False(SprintReportRules.CountsAsNotPlanned(NotPlanned(created: Oct(31, 0)), sprint)); // the day after it
        Assert.False(SprintReportRules.CountsAsNotPlanned(NotPlanned(created: Oct(18, 23)), sprint)); // the day before it
        Assert.False(SprintReportRules.CountsAsNotPlanned(
            NotPlanned(TaskItemStatus.Done, created: Oct(1), completed: Oct(31)), sprint)); // done after it
        Assert.False(SprintReportRules.CountsAsNotPlanned(
            NotPlanned(TaskItemStatus.Cancelled, created: Oct(1), completed: null), sprint));
        // Set to Done in the window and reopened since: CompletedAt is cleared, the change to Done still says so.
        var reopened = NotPlanned(created: Oct(1));
        Assert.True(SprintReportRules.CountsAsNotPlanned(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Done), Change(reopened, Oct(31), TaskItemStatus.Done, TaskItemStatus.Todo)]));
        Assert.False(SprintReportRules.CountsAsNotPlanned(reopened, sprint,
            [Change(reopened, Oct(31), TaskItemStatus.Todo, TaskItemStatus.Done), Change(reopened, Oct(31, 12), TaskItemStatus.Done, TaskItemStatus.Todo)]));
        Assert.False(SprintReportRules.CountsAsNotPlanned(reopened, sprint,
            [Change(reopened, Oct(24), TaskItemStatus.Todo, TaskItemStatus.Cancelled)])); // closed, but not Done
        // On the Backlog, or in another sprint, work is never Not Planned, whatever happened to it in the window.
        Assert.False(SprintReportRules.CountsAsNotPlanned(Task(false, inBacklog: true, created: Oct(20), sprint: 60), sprint));
        Assert.False(SprintReportRules.CountsAsNotPlanned(Task(false, OtherSprintId, created: Oct(20), sprint: 60), sprint));

        var counted = NotPlanned(created: Oct(22));
        var backlog = Task(false, inBacklog: true, created: Oct(22), sprint: 30);
        var elsewhere = Task(false, OtherSprintId, sprint: 30);
        var quiet = NotPlanned(created: Oct(1));
        var r = Build(sprint, [counted, backlog, elsewhere, quiet]);
        var group = Assert.Single(r.Rows);
        Assert.Equal(SprintGroupKind.NotPlanned, group.Kind);
        Assert.Equal(counted.Task.Id, Assert.Single(group.TaskRows).TaskId);
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
        Assert.Equal(new SprintTaskCounts(3, 1, 1, 1, 0, 0, 1), r.Summary.Planned);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 0, 1), r.Summary.NotPlanned);
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
        Assert.Equal(new SprintTaskCounts(4, 2, 2, 0, 1, 0, 2), r.Summary.Planned);

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
        Assert.Equal(new SprintTaskCounts(2, 1, 0, 1, 0, 0, 0), r.Summary.Planned);
        Assert.Equal(new SprintTaskCounts(2, 1, 1, 0, 0, 0, 1), r.Summary.NotPlanned);
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
        Assert.Equal(new SprintImpactRow(Alice, "Alice", 2, 1, 1, 0, 0, 0), Assert.Single(r.People));
        Assert.Equal(new EstimateComparison(2, 300, 120, 0), r.Summary.PlannedEstimate);
        Assert.Equal(r.Summary.Planned.Total - r.Summary.Planned.Cancelled, r.People.Sum(p => p.PlannedTasks));
        Assert.Equal(r.Summary.Planned.LeftOpen, r.People.Sum(p => p.PlannedLeftOpen));
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
    /// RPT-026: one row per project with planned work, by department then name, then the planned tasks on no project, then the Not
    /// Planned work; a Not Planned task's project neither adds a row nor joins the project options.
    /// </summary>
    [Fact]
    public void Rows_are_the_sprint_projects_then_no_project_then_not_planned()
    {
        var onAlpha = Task(project: Alpha);
        var onBeta = Task(project: Beta);
        var standalone = Task();
        var support = NotPlanned(project: Gamma);
        var r = Build(Sprint(), [support, standalone, onAlpha, onBeta]);

        Assert.Equal(
            [(SprintGroupKind.Project, "Beta"), (SprintGroupKind.Project, "Alpha"), (SprintGroupKind.NoProject, "No project"),
             (SprintGroupKind.NotPlanned, "Not Planned")],
            r.Rows.Select(g => (g.Kind, g.Name)).ToList());
        Assert.Equal([Beta, Alpha], r.ProjectOptions);
        Assert.Equal("P-26-00003 Gamma", Row(r, support).ProjectLabel);
        Assert.Null(Row(r, onAlpha).ProjectLabel);
        Assert.Equal(Alpha.Id.ToString(), r.Rows[1].Key);
        Assert.Equal("NotPlanned", r.Rows[3].Key);
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

        Assert.Equal(new SprintTaskCounts(3, 1, 1, 1, 0, 0, 0), s.Planned);
        Assert.Equal(new SprintTaskCounts(1, 1, 0, 0, 0, 0, 0), s.NotPlanned);
        Assert.Equal(4, s.All.Total);
        Assert.Equal(130, s.PlannedSprintMinutes);
        Assert.Equal(60, s.NotPlannedSprintMinutes);
        Assert.Equal(250, s.PlannedToDateMinutes);
        Assert.Equal(60, s.NotPlannedToDateMinutes);
        Assert.Equal(32, s.NotPlannedPercent); // 60 of 190
        Assert.Equal(new EstimateComparison(2, 180, 240, 2), s.PlannedEstimate);
        Assert.Equal(EstimateComparison.None, s.NotPlannedEstimate);
        Assert.Equal(new EstimateComparison(2, 180, 240, 2), s.AllEstimate);
        Assert.Equal(33, s.Planned.PercentDone);

        Assert.Null(Build(Sprint(), [Task()]).Summary.NotPlannedPercent); // nothing logged
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
                new SprintImpactRow(Bob, "Bob", 1, 1, 0, 1, 0, 120),
                new SprintImpactRow(Alice, "Alice", 2, 1, 1, 0, 80, 0),
                new SprintImpactRow(Carol, "Carol", 0, 0, 0, 0, 30, 0),
                new SprintImpactRow(null, "Unassigned", 1, 0, 1, 0, 0, 0)
            ],
            r.People);
        Assert.Equal(100, r.People[0].NotPlannedPercent);
        Assert.Equal(0, r.People[1].NotPlannedPercent);
        Assert.Null(r.People[3].NotPlannedPercent);
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
        Assert.Equal(90, r.Summary.Planned.LeftOpenRemainingMinutes);
        Assert.Equal(2, r.Summary.Planned.LeftOpenUnestimated);
        Assert.Equal(4, r.Summary.Planned.LeftOpen);
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
        Assert.Equal(new SprintDayRow(Start, 90, 45), r.Days[0]);
        Assert.Equal(new SprintDayRow(Start.AddDays(1), 120, 0), r.Days[1]);
        Assert.Equal(new SprintDayRow(End, 0, 15), r.Days[^1]);
        Assert.Equal(0, r.Days[5].Minutes);

        var alices = Build(Sprint(), [planned, other, support], days: days, assigneeId: Alice);
        Assert.Equal(new SprintDayRow(Start.AddDays(1), 0, 0), alices.Days[1]);
    }
}
