using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Components.Planning;
using Orbit.Data.Entities;
using Orbit.Tests.Access;

namespace Orbit.Tests.Sprints;

/// <summary>How the Planning page shows its panels' Department column and its Assignee filter (spec §6.3, decision 141).</summary>
public class PlanningLookTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();

    private static TaskItem Task(Guid department, Project? project = null, params ApplicationUser[] people) => new()
    {
        Number = "T-1",
        Title = "T-1",
        DepartmentId = department,
        ProjectId = project?.Id,
        Project = project,
        Assignments = Assigned.ToPeople(people)
    };

    private static PlanningGroup Group(Project? project, params TaskItem[] tasks) =>
        new(project, tasks, new PlanningCounts(tasks.Length, PlanningLoad.None, PlanningLoad.None, 0, 0));

    private static SprintPlanning Plan(IReadOnlyList<PlanningPerson> people, params TaskItem[] tasks) =>
        new([], null, new DateOnly(2026, 11, 1), 0, 480, PlanningLoad.None, PlanningLoad.None, people, [Group(null, tasks)],
            new Dictionary<Guid, int>());

    private static UserSummary Summary(ApplicationUser u) =>
        new(u.Id, u.DisplayName, "", RoleRef.None, false, It, "IT", true, false, DateTime.UtcNow);

    /// <summary>
    /// PLN-009: a project's panel shows the Department column only when a task is filed outside the project's department, whoever
    /// is looking, since its header names the project's department; the <i>No project</i> panel names none, so it follows the task
    /// lists' rule (always for a viewer who sees every department, otherwise when a task is filed outside theirs).
    /// </summary>
    [Fact]
    public void The_department_column_shows_when_the_header_doesnt_say_it()
    {
        var website = new Project { Name = "Website", Number = "P-26-001", DepartmentId = Finance };
        var admin = TestActors.SystemAdmin(It);
        var member = TestActors.Member(It);

        Assert.False(PlanningLook.ShowsDepartment(Group(website, Task(Finance, website)), admin));
        Assert.True(PlanningLook.ShowsDepartment(Group(website, Task(It, website)), admin)); // all filed in IT, the project is Finance's
        Assert.True(PlanningLook.ShowsDepartment(Group(website, Task(Finance, website), Task(It, website)), member));
        Assert.False(PlanningLook.ShowsDepartment(Group(null, Task(It)), member));
        Assert.True(PlanningLook.ShowsDepartment(Group(null, Task(Finance)), member));
        Assert.True(PlanningLook.ShowsDepartment(Group(null, Task(It)), admin));
    }

    /// <summary>
    /// PLN-010: the Assignee filter offers the people the viewer's lookups give, anyone in <i>Load per person</i> they leave out,
    /// and the person chosen, named from a shown task; by name. A name clicked in <i>Load per person</i>, or a remembered filter,
    /// then never shows a bare id. Unassigned isn't a person, and someone no list or task names can't be named.
    /// </summary>
    [Fact]
    public void The_assignee_filter_names_everyone_it_can_be_set_to()
    {
        var ann = Assigned.Person(Guid.NewGuid(), "Ann");
        var zed = Assigned.Person(Guid.NewGuid(), "Zed", active: false);
        var cara = Assigned.Person(Guid.NewGuid(), "Cara", Finance);
        UserSummary[] directory = [Summary(ann)];
        var plan = Plan([new PlanningPerson(zed.Id, "Zed", PlanningLoad.None, false), new PlanningPerson(null, "Unassigned", PlanningLoad.None, false)],
            Task(Finance, null, cara));

        Assert.Equal(new[] { (ann.Id.ToString(), "Ann"), (zed.Id.ToString(), "Zed") }, PlanningLook.AssigneeOptions(directory, plan, null));
        Assert.Equal(new[] { (ann.Id.ToString(), "Ann"), (cara.Id.ToString(), "Cara"), (zed.Id.ToString(), "Zed") },
            PlanningLook.AssigneeOptions(directory, plan, cara.Id));
        Assert.Equal(2, PlanningLook.AssigneeOptions(directory, plan, Guid.NewGuid()).Count);
    }
}
