using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;
using Orbit.Tests.Access;

namespace Orbit.Tests.Dashboard;

/// <summary>The dashboard's views and the figures behind its charts (spec §6.9, decision 134), over the pure <see cref="DashboardRules"/>.</summary>
public class DashboardRulesTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 31);

    /// <summary>DSH-001: which views a role reaches and which it starts on; the shipped roles are in DefaultRolesTests.</summary>
    [Fact]
    public void Views_follow_the_task_grants()
    {
        var editsOwnSeesAll = TestActors.Grants(It, (Permission.TasksView, PermissionScope.All), (Permission.TasksEdit, PermissionScope.Own));
        Assert.Equal(new[] { DashboardView.Mine, DashboardView.Department, DashboardView.Company }, DashboardRules.Views(editsOwnSeesAll));
        Assert.Equal(DashboardView.Mine, DashboardRules.DefaultView(editsOwnSeesAll));

        // Seeing a department needs one to see; seeing every department doesn't.
        var noDepartment = TestActors.Grants(null, (Permission.TasksView, PermissionScope.Department), (Permission.TasksEdit, PermissionScope.Own));
        Assert.Equal(new[] { DashboardView.Mine }, DashboardRules.Views(noDepartment));
        var seesAllNoDepartment = TestActors.Grants(null, (Permission.TasksView, PermissionScope.All));
        Assert.Equal(new[] { DashboardView.Department, DashboardView.Company }, DashboardRules.Views(seesAllNoDepartment));

        // View-only at Department with no department sees only their own tasks (Scoping.Tasks), so it gets My work, not nothing.
        var viewsOwnByDefault = TestActors.Grants(null, (Permission.TasksView, PermissionScope.Department));
        Assert.Equal(new[] { DashboardView.Mine }, DashboardRules.Views(viewsOwnByDefault));
        Assert.Equal(DashboardView.Mine, DashboardRules.DefaultView(viewsOwnByDefault));
        Assert.Equal(new[] { DashboardView.Mine }, DashboardRules.Views(TestActors.Grants(It, (Permission.TasksView, PermissionScope.Own))));

        // Edit without view sees nothing, so has no views.
        Assert.Empty(DashboardRules.Views(TestActors.Grants(It, (Permission.TasksEdit, PermissionScope.Own))));
    }

    /// <summary>DSH-002: the address's view is used when the viewer reaches it, by name in any case; anything else is the default.</summary>
    [Fact]
    public void Resolve_falls_back_to_the_default_view()
    {
        var member = TestActors.Member(It);
        Assert.Equal(DashboardView.Department, DashboardRules.Resolve("department", member));
        Assert.Equal(DashboardView.Department, DashboardRules.Resolve(" Department ", member));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve(null, member));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("", member));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("everything", member));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("7", member));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("company", member)); // not reached: a stale bookmark falls back

        // Only a view's name counts: an enum's number or a list of names is no view.
        var admin = TestActors.Grants(It, (Permission.TasksView, PermissionScope.All), (Permission.TasksEdit, PermissionScope.All));
        Assert.Equal(DashboardView.Company, DashboardRules.Resolve("COMPANY", admin));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("1", admin));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("2", admin));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("mine,company", admin));
        Assert.Equal(DashboardView.Mine, DashboardRules.Resolve("department, company", admin));
        Assert.Null(DashboardRules.Resolve("mine", TestActors.Nobody(It)));
        Assert.Equal("department", DashboardRules.Key(DashboardView.Department));
    }

    /// <summary>DSH-003: status counts sum repeated statuses and leave missing ones at zero; the facets give priorities and types in order.</summary>
    [Fact]
    public void Status_priority_and_type_counts_come_from_the_facets()
    {
        var counts = StatusCounts.From([(TaskItemStatus.Todo, 2), (TaskItemStatus.Todo, 3), (TaskItemStatus.Blocked, 1), (TaskItemStatus.Done, 4), (TaskItemStatus.Cancelled, 1)]);
        Assert.Equal(new StatusCounts(5, 0, 0, 1, 4, 1), counts);
        Assert.Equal(6, counts.Open);
        Assert.Equal(5, counts.Closed);
        Assert.Equal(11, counts.Total);
        Assert.Equal(StatusCounts.None, StatusCounts.From([]));
        Assert.Equal(new StatusCounts(1, 2, 3, 4, 5, 6), new StatusCounts(1, 1, 1, 1, 1, 1).Plus(new StatusCounts(0, 1, 2, 3, 4, 5)));

        OpenFacet[] facets =
        [
            new(TaskItemStatus.Todo, TaskPriority.High, TaskType.Change, 2),
            new(TaskItemStatus.InProgress, TaskPriority.High, TaskType.Problem, 3),
            new(TaskItemStatus.Waiting, TaskPriority.Low, TaskType.Change, 1)
        ];
        Assert.Equal(new StatusCounts(2, 3, 1, 0, 0, 0), DashboardRules.Statuses(facets));
        var priorities = DashboardRules.Priorities(facets);
        Assert.Equal(new[] { TaskPriority.Low, TaskPriority.Medium, TaskPriority.High, TaskPriority.Critical }, priorities.Select(p => p.Value));
        Assert.Equal(new[] { 1, 0, 5, 0 }, priorities.Select(p => p.Count));
        var types = DashboardRules.Types(facets);
        Assert.Equal(Enum.GetValues<TaskType>(), types.Select(t => t.Value));
        Assert.Equal(3, types.Single(t => t.Value == TaskType.Change).Count);
        Assert.Equal(6, priorities.Sum(p => p.Count));
        Assert.Equal(6, types.Sum(t => t.Count));
    }

    private static PersonStatusCount Person(Guid id, string name, int todo, TaskItemStatus status = TaskItemStatus.Todo) => new(id, name, status, todo);

    /// <summary>
    /// DSH-004: one bar per person by open work, labelled apart (same names numbered), the rest folded into "N others", Unassigned
    /// last and only when there is some.
    /// </summary>
    [Fact]
    public void People_bars_merge_by_person_fold_the_rest_and_end_with_unassigned()
    {
        var sam1 = Guid.NewGuid();
        var sam2 = Guid.NewGuid();
        var ann = Guid.NewGuid();
        var bars = DashboardRules.People(
            [Person(sam1, "Sam", 1), Person(sam2, "Sam", 2), Person(ann, "Ann", 2), Person(sam1, "Sam", 2, TaskItemStatus.Blocked)],
            new StatusCounts(9, 0, 0, 0, 0, 0));
        // Two people called Sam stay apart and are told apart; most open work first, then by name; Unassigned last however large.
        Assert.Equal(new[] { "Sam", "Ann", "Sam (2)", "Unassigned" }, bars.Select(b => b.Name));
        Assert.Equal(new[] { 3, 2, 2, 9 }, bars.Select(b => b.Counts.Open));
        Assert.Equal(new StatusCounts(1, 0, 0, 2, 0, 0), bars[0].Counts);

        Assert.DoesNotContain(DashboardRules.People([Person(ann, "Ann", 1)], StatusCounts.None), b => b.Name == "Unassigned");

        // Ten people: seven bars and "3 others" summing the rest, so never more than eight bars of people.
        var ten = Enumerable.Range(1, 10).Select(i => Person(Guid.NewGuid(), $"P{i:00}", i)).ToList();
        var folded = DashboardRules.People(ten, StatusCounts.None, max: 8);
        Assert.Equal(8, folded.Count);
        Assert.Equal("P10", folded[0].Name);
        Assert.Equal("3 others", folded[^1].Name);
        Assert.Equal(1 + 2 + 3, folded[^1].Counts.Open);

        // Eight people fit: no Others bar.
        Assert.DoesNotContain(DashboardRules.People(ten.Take(8), StatusCounts.None, max: 8), b => b.Name.EndsWith(" others"));

        Assert.Equal("Bartholomew S…", DashboardRules.People([Person(ann, "Bartholomew Smith", 1)], StatusCounts.None)[0].Name);

        // Names that shorten alike are numbered too, still within the label's length; a third of one name is (3).
        var alike = DashboardRules.Labels(["Bartholomew Smith", "Bartholomew Smythe", "sam", "Sam", "Sam"]);
        Assert.Equal(new[] { "Bartholomew S…", "Bartholom… (2)", "sam", "Sam (2)", "Sam (3)" }, alike);
        Assert.All(alike, l => Assert.True(l.Length <= DashboardRules.MaxLabel));
    }

    /// <summary>
    /// DSH-005: one bar per department with open work, by name, its statuses merged and its label apart from the others'; the
    /// sprint's progress per department leaves cancelled tasks out, and a department with nothing but cancelled ones.
    /// </summary>
    [Fact]
    public void Department_bars_are_by_name()
    {
        var finance = Guid.NewGuid();
        var it = Guid.NewGuid();
        var bars = DashboardRules.Departments(
        [
            new DepartmentStatusCount(it, "IT", TaskItemStatus.Todo, 4),
            new DepartmentStatusCount(finance, "Finance", TaskItemStatus.Waiting, 1),
            new DepartmentStatusCount(it, "IT", TaskItemStatus.Blocked, 2)
        ]);
        Assert.Equal(new[] { "Finance", "IT" }, bars.Select(b => b.Name));
        Assert.Equal(new StatusCounts(4, 0, 0, 2, 0, 0), bars[1].Counts);

        var north = DashboardRules.Departments(
        [
            new DepartmentStatusCount(Guid.NewGuid(), "Infrastructure North", TaskItemStatus.Todo, 1),
            new DepartmentStatusCount(Guid.NewGuid(), "Infrastructure South", TaskItemStatus.Todo, 1)
        ]);
        Assert.Equal(new[] { "Infrastructur…", "Infrastru… (2)" }, north.Select(b => b.Name));

        var hr = Guid.NewGuid();
        var sprint = DashboardRules.SprintDepartments(
        [
            new DepartmentStatusCount(it, "IT", TaskItemStatus.Done, 8),
            new DepartmentStatusCount(it, "IT", TaskItemStatus.Cancelled, 2),
            new DepartmentStatusCount(finance, "Finance", TaskItemStatus.Todo, 3),
            new DepartmentStatusCount(finance, "Finance", TaskItemStatus.Done, 1),
            new DepartmentStatusCount(hr, "HR", TaskItemStatus.Cancelled, 4)
        ]);
        Assert.Equal(new[] { new DepartmentSprintProgress("Finance", 4, 1), new DepartmentSprintProgress("IT", 8, 8) }, sprint);
    }

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>DSH-006: fourteen days ending today, every day present, each timestamp on its UTC date, anything outside ignored.</summary>
    [Fact]
    public void Daily_counts_are_zero_filled_and_bucketed_by_utc_day()
    {
        var days = DashboardRules.Daily(
            createdUtc: [Utc(10, 30, 23, 59), Utc(10, 31, 0, 1), Utc(10, 31, 9), Utc(10, 17, 23, 59), Utc(10, 18, 0)],
            doneUtc: [Utc(10, 31, 10), Utc(11, 1, 0, 30)],
            Today);
        Assert.Equal(14, days.Count);
        Assert.Equal(new DateOnly(2026, 10, 18), days[0].Day);
        Assert.Equal(Today, days[^1].Day);
        Assert.Equal(1, days[0].Created);                        // 18 Oct 00:00 in; 17 Oct 23:59 out
        Assert.Equal(1, days.Single(d => d.Day == new DateOnly(2026, 10, 30)).Created);
        Assert.Equal(2, days[^1].Created);
        Assert.Equal(1, days[^1].Done);                         // created and done the same day counts in both
        Assert.Equal(0, days[5].Created);
        Assert.Equal(5 - 1, days.Sum(d => d.Created));
        Assert.Equal(1, days.Sum(d => d.Done));                 // tomorrow's is outside
        Assert.Equal(new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc), DashboardRules.TrendStartUtc(Today));
    }

    /// <summary>DSH-007: how far through a sprint today is, and whole-number percentages.</summary>
    [Fact]
    public void Sprint_day_and_percentages()
    {
        var start = new DateOnly(2026, 10, 26);
        var end = new DateOnly(2026, 11, 6);
        Assert.Equal(new SprintDay(1, 12), DashboardRules.SprintDayOf(start, end, start));
        Assert.Equal(new SprintDay(12, 12), DashboardRules.SprintDayOf(start, end, end));
        Assert.Equal(100, DashboardRules.SprintDayOf(start, end, end).Percent);
        Assert.Equal(new SprintDay(0, 12), DashboardRules.SprintDayOf(start, end, start.AddDays(-3)));
        Assert.Equal(new SprintDay(12, 12), DashboardRules.SprintDayOf(start, end, end.AddDays(5)));
        Assert.Equal(new SprintDay(1, 1), DashboardRules.SprintDayOf(start, start, start));

        Assert.Equal(0, Percentage.Of(0, 0));
        Assert.Equal(0, Percentage.Of(3, 0));
        Assert.Equal(33, Percentage.Of(1, 3));
        Assert.Equal(67, Percentage.Of(2, 3));
        Assert.Equal(50, Percentage.Of(1, 2));
        Assert.Equal(100, Percentage.Of(5, 4));

        // Work done leaves the cancelled tasks out: 8 done and 2 cancelled, with nothing open, is all of it.
        var sprint = new StatusCounts(0, 0, 0, 0, 8, 2);
        Assert.Equal(8, sprint.NotCancelled);
        Assert.Equal(100, Percentage.Of(sprint.Done, sprint.NotCancelled));
        Assert.Equal(9, new StatusCounts(1, 0, 0, 0, 8, 2).NotCancelled);
    }

    /// <summary>
    /// DSH-008: each figure's link is the Tasks list of exactly what it counts, narrowed only as the view is, in the list's own keys
    /// (TaskListKeys) and with no date in it: the due figures use the Due filter, counted from today when the list loads.
    /// </summary>
    [Fact]
    public void Links_open_the_list_of_what_each_figure_counts()
    {
        var plain = new DashboardLinks(null, null);
        Assert.Equal("/Tasks?OpenOnly=true&Due=Overdue", plain.Overdue);
        Assert.Equal("/Tasks?OpenOnly=true&Due=Soon", plain.DueSoon);
        Assert.Equal("/Tasks?Status=Blocked", plain.Status(TaskItemStatus.Blocked));
        Assert.Equal("/Tasks?OpenOnly=true&Priority=Critical", plain.Priority(TaskPriority.Critical));
        Assert.Equal("/Tasks?OpenOnly=true&Type=Problem", plain.Type(TaskType.Problem));
        Assert.Equal("/Tasks?PlannedToday=true&Status=Done", plain.Planned(TaskItemStatus.Done));
        Assert.Equal("/Tasks?PlannedToday=true", plain.PlannedAll);                       // the Today page's figures
        Assert.Equal("/Tasks?OpenOnly=true&PlannedToday=true", plain.PlannedOpen);
        Assert.Equal("/Tasks?OpenOnly=true&Due=Overdue&PlannedToday=true", plain.PlannedOverdue);
        Assert.Equal("/Tasks?OpenOnly=true", plain.AllOpen);
        Assert.Equal("/Today", plain.TodayPlan);

        var me = Guid.NewGuid();
        var mine = new DashboardLinks(me, null);
        foreach (var link in new[] { mine.Overdue, mine.DueSoon, mine.Status(TaskItemStatus.Todo), mine.Priority(TaskPriority.Low), mine.Planned(TaskItemStatus.Done), mine.AllOpen })
            Assert.StartsWith($"/Tasks?AssigneeId={me}&", link);
        Assert.Equal($"/Tasks?AssigneeId={me}&PlannedToday=true", mine.TodayPlan);
        Assert.Equal(Navigation.MyTasksToday(me), mine.TodayPlan);    // the Tasks list's Today filter
        Assert.Equal($"/Tasks?AssigneeId={me}&OpenOnly=true", mine.AllOpen);
        Assert.Equal(Navigation.MyTasks(me), mine.AllOpen);           // and its My tasks filter

        var dept = Guid.NewGuid();
        var picked = new DashboardLinks(null, dept);
        Assert.Equal($"/Tasks?DepartmentId={dept}&Status=Todo", picked.Status(TaskItemStatus.Todo));
        Assert.Equal($"/Today?DepartmentId={dept}", picked.TodayPlan);
        Assert.Equal($"/Tasks?DepartmentId={dept}&PlannedToday=true", picked.PlannedAll);
        var sprint = Guid.NewGuid();
        Assert.Equal($"/Tasks?DepartmentId={dept}&Sprint={sprint}&Status=Waiting", picked.InSprint(sprint, TaskItemStatus.Waiting));

        // Up for grabs is never narrowed to the viewer; it names the department only for someone who sees every department.
        Assert.Equal("/Tasks?OpenOnly=true&AssigneeId=none", DashboardLinks.UpForGrabs(null));
        Assert.Equal($"/Tasks?DepartmentId={dept}&OpenOnly=true&AssigneeId=none", DashboardLinks.UpForGrabs(dept));
        Assert.DoesNotContain("DepartmentId", plain.AllOpen + mine.AllOpen);

        // Every key of every link to the Tasks list is one the list reads and remembers, so renaming a key fails here; no link has a date.
        var links = new[] { plain, mine, picked }
            .SelectMany(l => new[] { l.Overdue, l.DueSoon, l.AllOpen, l.TodayPlan, l.PlannedAll, l.PlannedOpen, l.PlannedOverdue }
                .Concat(Enum.GetValues<TaskItemStatus>().SelectMany(s => new[] { l.Status(s), l.Planned(s), l.InSprint(sprint, s) }))
                .Concat(Enum.GetValues<TaskPriority>().Select(l.Priority))
                .Concat(Enum.GetValues<TaskType>().Select(l.Type)))
            .Append(DashboardLinks.UpForGrabs(null)).Append(DashboardLinks.UpForGrabs(dept))
            .Where(link => link.StartsWith(TaskListKeys.Path + "?"))
            .ToList();
        Assert.True(links.Count > 50);
        foreach (var link in links)
            foreach (var key in QueryKeys(link))
                Assert.Contains(key, TaskListKeys.Remembered);
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", string.Join(" ", links));
    }

    private static IEnumerable<string> QueryKeys(string link) =>
        link[(link.IndexOf('?') + 1)..].Split('&').Select(pair => Uri.UnescapeDataString(pair[..pair.IndexOf('=')]));

    /// <summary>
    /// DSH-010: the Tasks list's Due filter, which the dashboard's due figures count and link to, is counted from today with both ends
    /// inclusive: Overdue is due before today, Soon is due today to seven days ahead; a task with no due date is in neither.
    /// </summary>
    [Fact]
    public void Due_windows_are_counted_from_today()
    {
        Assert.Equal((null, new DateOnly(2026, 10, 30)), DueWindowRules.Range(DueWindow.Overdue, Today));
        Assert.Equal((Today, new DateOnly(2026, 11, 7)), DueWindowRules.Range(DueWindow.Soon, Today));

        var tasks = new int?[] { -30, -1, 0, 7, 8, null }
            .Select(d => new TaskItem { DueDate = d is int days ? Today.AddDays(days) : null })
            .AsQueryable();
        int? Offset(TaskItem t) => t.DueDate!.Value.DayNumber - Today.DayNumber;
        Assert.Equal(new int?[] { -30, -1 }, DueWindowRules.Within(tasks, DueWindow.Overdue, Today).AsEnumerable().Select(Offset));
        Assert.Equal(new int?[] { 0, 7 }, DueWindowRules.Within(tasks, DueWindow.Soon, Today).AsEnumerable().Select(Offset));
        // The task's own overdue flag agrees with the window.
        Assert.All(DueWindowRules.Within(tasks, DueWindow.Overdue, Today), t => Assert.True(t.IsOverdue(Today)));

        Assert.Equal("Overdue", DueWindowRules.Label(DueWindow.Overdue));
        Assert.Equal("Due in 7 days", DueWindowRules.Label(DueWindow.Soon));
    }
}
