using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Scheduling;
using Orbit.Data.Entities;

namespace Orbit.Tests.Sprints;

/// <summary>The Planning page: which tasks it shows, the sprint being planned, capacity, panels and totals (spec §6.3, decision 140).</summary>
public class SprintPlanningRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 1); // a Sunday

    private static readonly Department It = new() { Name = "IT" };
    private static readonly Department Finance = new() { Name = "Finance" };

    private static readonly Sprint Active = new() { Name = "Sprint 13", Status = SprintStatus.Active, StartDate = new(2026, 10, 19), EndDate = new(2026, 10, 30) };
    private static readonly Sprint Next = new() { Name = "Sprint 14", Status = SprintStatus.Planned, StartDate = new(2026, 11, 2), EndDate = new(2026, 11, 13) };
    private static readonly Sprint Later = new() { Name = "Sprint 15", Status = SprintStatus.Planned, StartDate = new(2026, 11, 16), EndDate = new(2026, 11, 27) };
    private static readonly Sprint Done = new() { Name = "Sprint 12", Status = SprintStatus.Completed, StartDate = new(2026, 10, 5), EndDate = new(2026, 10, 16) };

    private static Project Project(string name, string number, Department department) =>
        new() { Name = name, Number = number, DepartmentId = department.Id, Department = department };

    private static TaskItem Task(
        string number, Project? project = null, Sprint? sprint = null, bool backlog = false, int? estimate = null,
        TaskPriority priority = TaskPriority.Medium, DateOnly? due = null, TaskItemStatus status = TaskItemStatus.Todo,
        params ApplicationUser[] people) => new()
    {
        Number = number,
        Title = number,
        ProjectId = project?.Id,
        Project = project,
        DepartmentId = project?.DepartmentId ?? It.Id,
        SprintId = sprint?.Id,
        Sprint = sprint,
        InBacklog = backlog && sprint is null,
        EstimateMinutes = estimate,
        Priority = priority,
        DueDate = due,
        Status = status,
        Assignments = Assigned.ToPeople(people)
    };

    /// <summary>The page's plan; the sprint's own tasks, for each person's load, are the shown tasks unless the test filters them.</summary>
    private static SprintPlanning Build(Sprint? target, IEnumerable<TaskItem> tasks, IReadOnlyDictionary<Guid, int>? logged = null,
        int workingDays = 10, int workingDayMinutes = 480, IEnumerable<TaskItem>? sprintTasks = null)
    {
        var shown = tasks.ToList();
        return SprintPlanningRules.Build([Active, Next, Later], target, shown, sprintTasks ?? shown, logged ?? new Dictionary<Guid, int>(),
            Today, workingDays, workingDayMinutes);
    }

    /// <summary>
    /// PLN-001: the sprint being planned is the one asked for while it is open; otherwise the planned sprint that starts first,
    /// then the active one, then none. A completed or unknown sprint asked for falls back to the default.
    /// </summary>
    [Fact]
    public void The_sprint_being_planned_defaults_to_the_next_one()
    {
        IReadOnlyList<Sprint> open = [Active, Later, Next];

        Assert.Same(Next, SprintPlanningRules.DefaultTarget(open, null));
        Assert.Same(Later, SprintPlanningRules.DefaultTarget(open, Later.Id));
        Assert.Same(Active, SprintPlanningRules.DefaultTarget(open, Active.Id));
        Assert.Same(Next, SprintPlanningRules.DefaultTarget(open, Guid.NewGuid()));
        Assert.Same(Next, SprintPlanningRules.DefaultTarget([.. open, Done], Done.Id));
        Assert.Same(Active, SprintPlanningRules.DefaultTarget([Active], null));
        Assert.Null(SprintPlanningRules.DefaultTarget([], null));
        Assert.Null(SprintPlanningRules.DefaultTarget([Done], Done.Id));
    }

    /// <summary>
    /// PLN-002: every open task on a project is shown, wherever it sits; a standalone task only on the Backlog or in a sprint, so
    /// support work (standalone and Not Planned) stays out; a closed task never.
    /// </summary>
    [Fact]
    public void Shows_open_project_tasks_and_planned_standalone_tasks()
    {
        var project = Project("Website", "P-26-001", It);
        TaskItem[] tasks =
        [
            Task("project backlog", project, backlog: true),
            Task("project sprint", project, Next),
            Task("project not planned", project),
            Task("project in progress", project, status: TaskItemStatus.InProgress),
            Task("project waiting", project, Done, status: TaskItemStatus.Waiting),
            Task("project blocked", project, backlog: true, status: TaskItemStatus.Blocked),
            Task("standalone backlog", backlog: true),
            Task("standalone sprint", sprint: Active),
            Task("standalone not planned"),
            Task("project done", project, backlog: true, status: TaskItemStatus.Done),
            Task("standalone cancelled", sprint: Next, status: TaskItemStatus.Cancelled)
        ];

        Assert.Equal(
            ["project backlog", "project sprint", "project not planned", "project in progress", "project waiting", "project blocked",
             "standalone backlog", "standalone sprint"],
            SprintPlanningRules.Shown(tasks.AsQueryable()).Select(t => t.Title).ToList());
    }

    /// <summary>
    /// PLN-003: a row's button. On the Backlog or Not Planned it plans the task into the sprint being planned; in that sprint it
    /// takes it back to the Backlog; in another sprint, a completed one included, it moves it here.
    /// </summary>
    [Fact]
    public void The_button_plans_removes_or_moves()
    {
        var target = Next.Id;
        Assert.Equal(PlanningAction.Plan, SprintPlanningRules.ActionFor(Task("a", backlog: true), target));
        Assert.Equal(PlanningAction.Plan, SprintPlanningRules.ActionFor(Task("b"), target));
        Assert.Equal(PlanningAction.Remove, SprintPlanningRules.ActionFor(Task("c", sprint: Next), target));
        Assert.Equal(PlanningAction.MoveHere, SprintPlanningRules.ActionFor(Task("d", sprint: Active), target));
        Assert.Equal(PlanningAction.MoveHere, SprintPlanningRules.ActionFor(Task("e", sprint: Done), target));

        Assert.Equal(SprintPlacement.In(target), SprintPlanningRules.PlacementFor(PlanningAction.Plan, target));
        Assert.Equal(SprintPlacement.In(target), SprintPlanningRules.PlacementFor(PlanningAction.MoveHere, target));
        Assert.Equal(SprintPlacement.Backlog, SprintPlanningRules.PlacementFor(PlanningAction.Remove, target));
    }

    /// <summary>
    /// PLN-004: a sprint's working days still to come, both ends included: the whole window before it starts, what is left of it
    /// once it has, none after its end; holidays from the working calendar are skipped.
    /// </summary>
    [Fact]
    public void Working_days_are_those_still_to_come()
    {
        var week = WorkDayCalendar.Default;
        Assert.Equal(10, SprintPlanningRules.WorkingDays(week, Next, Today));                  // Mon 2 to Fri 13 November
        Assert.Equal(10, SprintPlanningRules.WorkingDays(week, Next, new DateOnly(2026, 11, 2)));
        Assert.Equal(8, SprintPlanningRules.WorkingDays(week, Next, new DateOnly(2026, 11, 4))); // from Wednesday
        Assert.Equal(1, SprintPlanningRules.WorkingDays(week, Next, new DateOnly(2026, 11, 13)));
        Assert.Equal(0, SprintPlanningRules.WorkingDays(week, Next, new DateOnly(2026, 11, 14)));
        Assert.Equal(0, SprintPlanningRules.WorkingDays(week, Active, Today));

        var holiday = new WorkDayCalendar(week.WorkingWeek, [new CalendarDay(new DateOnly(2026, 11, 10), false, "Holiday")]);
        Assert.Equal(9, SprintPlanningRules.WorkingDays(holiday, Next, Today));

        var planning = Build(Next, [], workingDays: 9, workingDayMinutes: 480);
        Assert.Equal(9 * 480, planning.CapacityMinutes);
    }

    /// <summary>
    /// PLN-005: a panel per project, by department, then name, then number, and the standalone tasks last. Inside a panel the
    /// tasks are by priority, then due date (none last), then number, so a row keeps its place when it is planned.
    /// </summary>
    [Fact]
    public void Panels_are_projects_in_order_with_standalone_tasks_last()
    {
        var website = Project("Website", "P-26-003", It);
        var alpha = Project("Alpha", "P-26-002", It);
        var ledger = Project("Ledger", "P-26-001", Finance);
        var low = Task("T-5", website, backlog: true, priority: TaskPriority.Low);
        var undated = Task("T-4", website, backlog: true, priority: TaskPriority.High);
        var later = Task("T-3", website, backlog: true, priority: TaskPriority.High, due: new DateOnly(2026, 12, 1));
        var sooner = Task("T-2", website, backlog: true, priority: TaskPriority.High, due: new DateOnly(2026, 11, 20));
        var critical = Task("T-1", website, backlog: true, priority: TaskPriority.Critical);
        TaskItem[] tasks = [Task("T-9", backlog: true), low, Task("T-8", alpha, backlog: true), undated, later, Task("T-7", ledger), sooner, critical,
            Task("T-6", backlog: true)];

        var planning = Build(Next, tasks);

        Assert.Equal(["Ledger", "Alpha", "Website", null], planning.Groups.Select(g => g.Project?.Name).ToList());
        Assert.Equal(Guid.Empty, planning.Groups[^1].Key);
        string[] order = ["T-1", "T-2", "T-3", "T-4", "T-5"];
        Assert.Equal(order, planning.Groups[2].Tasks.Select(t => t.Number).ToList());
        Assert.Equal(["T-6", "T-9"], planning.Groups[3].Tasks.Select(t => t.Number).ToList());

        // Planning a task changes its placement, not its place.
        SprintPlacement.In(Next.Id).ApplyTo(later);
        Assert.Equal(order, Build(Next, tasks).Groups[2].Tasks.Select(t => t.Number).ToList());
    }

    /// <summary>
    /// PLN-006: each panel counts its open tasks, those in the sprint being planned and on the Backlog (how many, how many have
    /// no estimate, the estimates and what is left of them, never below nothing), those in other sprints and Not Planned; the
    /// totals add up the sprint being planned and the Backlog across the panels.
    /// </summary>
    [Fact]
    public void Panels_and_totals_count_the_sprint_and_the_backlog()
    {
        var project = Project("Website", "P-26-001", It);
        var partly = Task("T-1", project, Next, estimate: 120);
        var over = Task("T-3", project, Next, estimate: 60);
        TaskItem[] tasks =
        [
            partly,
            Task("T-2", project, Next),
            over,
            Task("T-4", project, backlog: true, estimate: 240),
            Task("T-5", project, backlog: true),
            Task("T-6", project, Active, estimate: 999),
            Task("T-7", project, estimate: 999),
            Task("T-8", sprint: Next, estimate: 30)
        ];
        var logged = new Dictionary<Guid, int> { [partly.Id] = 30, [over.Id] = 100 };

        var planning = Build(Next, tasks, logged);

        var counts = planning.Groups[0].Counts;
        Assert.Equal(7, counts.Open);
        Assert.Equal(new PlanningLoad(3, 1, 180, 90), counts.Target);
        Assert.Equal(new PlanningLoad(2, 1, 240, 240), counts.Backlog);
        Assert.Equal(1, counts.OtherSprints);
        Assert.Equal(1, counts.NotPlanned);
        Assert.Equal(new PlanningLoad(1, 0, 30, 30), planning.Groups[1].Counts.Target);

        Assert.Equal(new PlanningLoad(4, 1, 210, 120), planning.TargetTotals);
        Assert.True(planning.TargetTotals.HasLogged);
        Assert.Equal(new PlanningLoad(2, 1, 240, 240), planning.BacklogTotals);
        Assert.False(planning.BacklogTotals.HasLogged);
        Assert.Equal(30, planning.Logged(partly.Id));

        // With no sprint to plan, nothing is in it and every sprint's task is in another sprint.
        var none = Build(null, tasks, logged);
        Assert.Equal(PlanningLoad.None, none.TargetTotals);
        Assert.Equal(4, none.Groups[0].Counts.OtherSprints);
        Assert.Empty(none.People);
    }

    /// <summary>
    /// PLN-007: each person's share of the sprint being planned. A shared task counts in full for each assignee; a person is
    /// overloaded when what is left of their estimates is more than one person's capacity, not when it is equal; Unassigned comes
    /// last and is never overloaded; tasks elsewhere don't count.
    /// </summary>
    [Fact]
    public void People_are_loaded_against_their_capacity()
    {
        var ann = Assigned.Person(Guid.NewGuid(), "Ann");
        var bob = Assigned.Person(Guid.NewGuid(), "Bob");
        var cara = Assigned.Person(Guid.NewGuid(), "Cara");
        var project = Project("Website", "P-26-001", It);
        TaskItem[] tasks =
        [
            Task("T-1", project, Next, estimate: 120, people: [ann, bob]),
            Task("T-2", project, Next, estimate: 60, people: [ann]),
            Task("T-3", project, Next, people: [bob]),
            Task("T-4", project, Next, estimate: 500),
            Task("T-5", project, backlog: true, estimate: 600, people: [cara]),
            Task("T-6", project, Active, estimate: 600, people: [bob])
        ];

        var planning = Build(Next, tasks, workingDays: 2, workingDayMinutes: 60);

        Assert.Equal(["Ann", "Bob", "Unassigned"], planning.People.Select(p => p.Name).ToList());
        var (a, b, nobody) = (planning.People[0], planning.People[1], planning.People[2]);
        Assert.Equal(new PlanningLoad(2, 0, 180, 180), a.Load);
        Assert.True(a.Overloaded);
        Assert.Equal(new PlanningLoad(2, 1, 120, 120), b.Load);
        Assert.False(b.Overloaded); // exactly the capacity
        Assert.Null(nobody.UserId);
        Assert.Equal(new PlanningLoad(1, 0, 500, 500), nobody.Load);
        Assert.False(nobody.Overloaded);
        Assert.Equal(1, planning.OverloadedCount);
    }

    /// <summary>
    /// PLN-008: a person's share is all of their work in the sprint the viewer may see, whatever the filters (decision 141). With
    /// the rows filtered to Ann's, Bob still shows his whole load and is still overloaded, and the tasks nobody is assigned to still
    /// count; the sprint's totals are those of the rows shown.
    /// </summary>
    [Fact]
    public void A_persons_load_ignores_the_filters()
    {
        var ann = Assigned.Person(Guid.NewGuid(), "Ann");
        var bob = Assigned.Person(Guid.NewGuid(), "Bob");
        var project = Project("Website", "P-26-001", It);
        var shared = Task("T-1", project, Next, estimate: 60, people: [ann, bob]);
        var bobs = Task("T-2", project, Next, estimate: 600, people: [bob]);
        var nobodys = Task("T-3", project, Next, estimate: 30);
        var logged = new Dictionary<Guid, int> { [bobs.Id] = 100 };

        var planning = Build(Next, [shared], logged, workingDays: 1, workingDayMinutes: 480, sprintTasks: [shared, bobs, nobodys]);

        Assert.Equal(new PlanningLoad(1, 0, 60, 60), planning.TargetTotals);
        Assert.Equal(["Bob", "Ann", "Unassigned"], planning.People.Select(p => p.Name).ToList());
        Assert.Equal(new PlanningLoad(2, 0, 660, 560), planning.People[0].Load);
        Assert.True(planning.People[0].Overloaded);
        Assert.Equal(new PlanningLoad(1, 0, 60, 60), planning.People[1].Load);
        Assert.Equal(new PlanningLoad(1, 0, 30, 30), planning.People[2].Load);
        Assert.Equal(1, planning.OverloadedCount);
    }
}
