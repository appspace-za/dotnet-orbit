using Orbit.Application;
using Orbit.Application.Services;

namespace Orbit.Tests.Access;

/// <summary>The navbar follows the actor's grants (spec §6.5, §6.9): a link only to a page the person can use.</summary>
public class NavigationTests
{
    private static readonly Guid Finance = Guid.NewGuid();

    private static List<string> Menu(Actor a) => Navigation.Visible(a).Select(i => i.Label).ToList();

    /// <summary>NAV-001: the shipped roles keep the menu they always had; the built-in role also gets every Admin item.</summary>
    [Fact]
    public void Shipped_roles_see_their_full_menus()
    {
        string[] full = ["Dashboard", "Today", "Tasks", "Projects", "Backlog", "Sprints", "Recurring", "My Time", "Assets", "Requests"];
        Assert.Equal(full, Menu(TestActors.Member(Finance)));
        Assert.Equal(full, Menu(TestActors.DepartmentAdmin(Finance)));
        Assert.Equal([.. full, "Reports"], Menu(TestActors.SystemAdmin()));
        Assert.Equal(Navigation.Admin.Count, Navigation.AdminVisible(TestActors.SystemAdmin()).Count);
        Assert.Empty(Navigation.AdminVisible(TestActors.Member(Finance)));
        // A Department Admin configures the department's request flows, which are in the Admin menu.
        Assert.Equal(["Request flows"], Navigation.AdminVisible(TestActors.DepartmentAdmin(Finance)).Select(i => i.Label));
    }

    /// <summary>NAV-001: each Admin-menu permission on its own opens exactly one Admin item - the menu and the catalogue's list agree.</summary>
    [Fact]
    public void Each_admin_permission_opens_one_admin_item()
    {
        foreach (var key in PermissionCatalog.AdminPermissions)
        {
            var scope = PermissionCatalog.ByKey[key].AllowedScopes.Max();
            Assert.Single(Navigation.AdminVisible(TestActors.Grants(Finance, (key, scope))));
        }
        Assert.Equal(PermissionCatalog.AdminPermissions.Count, Navigation.Admin.Count);
    }

    /// <summary>NAV-002: each item needs its permission - here a requester, a view-only auditor and a role with nothing.</summary>
    [Fact]
    public void Items_follow_their_permissions()
    {
        Assert.Equal(["Dashboard", "Requests"], Menu(TestActors.Grants(Finance, (Permission.RequestsSubmit, PermissionScope.Own))));
        var auditor = TestActors.Grants(null, (Permission.TasksView, PermissionScope.All), (Permission.ProjectsView, PermissionScope.All));
        Assert.Equal(["Dashboard", "Tasks"], Menu(auditor));
        Assert.Equal(["Dashboard"], Menu(TestActors.Nobody(Finance)));
        // Configuring request flows and the action library are in the Admin menu, not the main one.
        var configurer = TestActors.Grants(Finance, (Permission.RequestsConfigure, PermissionScope.Department));
        Assert.Equal(["Dashboard"], Menu(configurer));
        Assert.Equal("/RequestCatalogue", Navigation.AdminVisible(configurer).Single().Href);
        var scripter = TestActors.Grants(null, (Permission.ActionsCreate, PermissionScope.All));
        Assert.Equal(["Dashboard"], Menu(scripter));
        Assert.Equal("/Actions", Navigation.AdminVisible(scripter).Single().Href);
    }

    /// <summary>NAV-003: taking one permission away takes its items - planning takes Backlog and Sprints, editing Today.</summary>
    [Fact]
    public void Removing_a_permission_removes_its_items()
    {
        var noPlan = new Dictionary<string, PermissionScope>(DefaultRoles.MemberGrants);
        noPlan.Remove(Permission.TasksPlan);
        var menu = Menu(TestActors.With(noPlan, Finance));
        Assert.DoesNotContain("Backlog", menu);
        Assert.DoesNotContain("Sprints", menu);
        Assert.Contains("Today", menu);

        var noEdit = new Dictionary<string, PermissionScope>(DefaultRoles.MemberGrants);
        noEdit.Remove(Permission.TasksEdit);
        noEdit.Remove(Permission.ProjectsEdit);
        noEdit.Remove(Permission.TimeLog);
        noEdit.Remove(Permission.TasksCreate);
        menu = Menu(TestActors.With(noEdit, Finance));
        Assert.DoesNotContain("Today", menu);
        Assert.DoesNotContain("Projects", menu);
        Assert.DoesNotContain("My Time", menu);
        Assert.DoesNotContain("Recurring", menu);
        Assert.Contains("Tasks", menu);
        Assert.Contains("Dashboard", menu);
    }

    /// <summary>
    /// NAV-004: a view-only role gets the dashboard views of its view scope, not My work, which it has no work on - unless Own is all
    /// it sees, when My work (the tasks it raised, requested or is assigned) is its one view.
    /// </summary>
    [Fact]
    public void View_only_roles_get_their_view_scope_on_the_dashboard()
    {
        var viewer = TestActors.Grants(Finance, (Permission.TasksView, PermissionScope.Department));
        Assert.Equal(new[] { DashboardView.Department }, DashboardRules.Views(viewer));
        Assert.Equal(DashboardView.Department, DashboardRules.DefaultView(viewer));
        var ownViewer = TestActors.Grants(Finance, (Permission.TasksView, PermissionScope.Own));
        Assert.Equal(new[] { DashboardView.Mine }, DashboardRules.Views(ownViewer));
        Assert.Equal(DashboardView.Mine, DashboardRules.DefaultView(TestActors.Member(Finance)));
        Assert.Empty(DashboardRules.Views(TestActors.Nobody(Finance)));
    }

    /// <summary>
    /// NAV-005: a menu item holds the URL its page answers on, never a page name (§6, decision 111): the dashboard is "/", a
    /// list is its folder, and any other page is its own path.
    /// </summary>
    [Fact]
    public void Menu_links_are_the_urls_the_pages_answer_on()
    {
        var hrefs = Navigation.Main.Concat(Navigation.Admin).ToDictionary(i => i.Label, i => i.Href);
        Assert.Equal("/", hrefs["Dashboard"]);
        Assert.Equal("/Today", hrefs["Today"]);
        Assert.Equal("/Tasks", hrefs["Tasks"]);
        Assert.Equal("/Admin/Users", hrefs["Users"]);
        Assert.All(hrefs.Values, h => Assert.DoesNotContain("/Index", h));
    }

    /// <summary>
    /// NAV-006: My tasks is the Tasks list filtered to the viewer's open tasks, in the list's own keys (§6.2, decision 132) - the
    /// quick filter that replaced the My Tasks page. An actor with no user of its own gets the plain list.
    /// </summary>
    [Fact]
    public void My_tasks_is_the_Tasks_list_filtered_to_your_open_tasks()
    {
        var me = Guid.NewGuid();
        Assert.Equal($"/Tasks?AssigneeId={me}&OpenOnly=true", Navigation.MyTasks(TestActors.With(DefaultRoles.MemberGrants, Finance, userId: me)));
        Assert.Equal("/Tasks", Navigation.MyTasks(Actor.System));
        Assert.DoesNotContain(Navigation.Main, i => i.Href.StartsWith("/Tasks/", StringComparison.Ordinal));
    }

    /// <summary>
    /// NAV-007: Today is the Tasks list filtered to the viewer's tasks on today's plan, done ones included - Open only is left off
    /// (§6.12, decision 133). An actor with no user of its own gets the plain list. The Today page stays in the menu.
    /// </summary>
    [Fact]
    public void Today_is_the_Tasks_list_filtered_to_your_tasks_planned_today()
    {
        var me = Guid.NewGuid();
        Assert.Equal($"/Tasks?AssigneeId={me}&PlannedToday=true", Navigation.MyTasksToday(TestActors.With(DefaultRoles.MemberGrants, Finance, userId: me)));
        Assert.Equal("/Tasks", Navigation.MyTasksToday(Actor.System));
        Assert.Contains(Navigation.Main, i => i.Href == "/Today");
    }
}
