namespace Orbit.Application;

/// <summary>One link in the menu: its label, the URL it opens (where its page answers), and who sees it.</summary>
public sealed record NavItem(string Label, string Href, Func<Actor, bool> Shows);

/// <summary>
/// The navbar (spec §6.5, §6.9): which menu items a person sees, from their grants. A link shows only to someone who can use the page
/// it opens - My Time to someone who may log at least their own time, Backlog to someone who may plan at least their own tasks - but
/// hiding a link blocks nothing: the pages keep their own doors and every service applies the scopes. The Dashboard is everyone's
/// landing page and adapts to what they may do (<see cref="Services.DashboardService"/>). Pure, so the rules are unit-tested; the
/// layout and the dashboard's buttons both read it.
/// </summary>
public static class Navigation
{
    /// <summary>Today and My Tasks - your own work: tasks.edit at Own or wider.</summary>
    public static bool ShowsMyWork(Actor a) => a.Has(Permission.TasksEdit);

    /// <summary>The Tasks list: tasks.view at any scope (without it the list is empty).</summary>
    public static bool ShowsTasks(Actor a) => a.Has(Permission.TasksView);

    /// <summary>Projects: projects.edit at Own or wider.</summary>
    public static bool ShowsProjects(Actor a) => a.Has(Permission.ProjectsEdit);

    /// <summary>Backlog and Sprints: tasks.plan at Own or wider.</summary>
    public static bool ShowsPlanning(Actor a) => a.Has(Permission.TasksPlan);

    /// <summary>Recurring tasks: tasks.create (Department or All).</summary>
    public static bool ShowsRecurring(Actor a) => a.Has(Permission.TasksCreate);

    /// <summary>My Time: time.log at Own or wider.</summary>
    public static bool ShowsMyTime(Actor a) => a.Has(Permission.TimeLog);

    /// <summary>Assets: assets.view at Own or wider.</summary>
    public static bool ShowsAssets(Actor a) => a.Has(Permission.AssetsView);

    /// <summary>The main menu, in order. Configuring request flows is in the Admin menu.</summary>
    public static readonly IReadOnlyList<NavItem> Main =
    [
        new("Dashboard", "/", _ => true),
        new("Today", "/Today", ShowsMyWork),
        new("My Tasks", "/Tasks/My", ShowsMyWork),
        new("Tasks", "/Tasks", ShowsTasks),
        new("Projects", "/Projects", ShowsProjects),
        new("Backlog", "/Backlog", ShowsPlanning),
        new("Sprints", "/Sprints", ShowsPlanning),
        new("Recurring", "/Recurring", ShowsRecurring),
        new("My Time", "/Time/My", ShowsMyTime),
        new("Assets", "/Assets", ShowsAssets),
        new("Requests", "/Requests", AccessPolicy.CanSubmitRequests),
        new("Reports", "/Reports", AccessPolicy.CanViewReports)
    ];

    /// <summary>The Admin dropdown, in order: each item on its own permission (<see cref="PermissionCatalog.AdminPermissions"/>).</summary>
    public static readonly IReadOnlyList<NavItem> Admin =
    [
        new("Users", "/Admin/Users", a => a.Has(Permission.UsersManage)),
        new("Roles", "/Admin/Roles", a => a.Has(Permission.RolesManage)),
        new("Departments", "/Admin/Departments", a => a.Has(Permission.DepartmentsManage)),
        new("API Keys", "/Admin/ApiKeys", a => a.Has(Permission.ApiKeysManage)),
        new("Directory (LDAP)", "/Admin/Directory", a => a.Has(Permission.DirectoryManage)),
        new("Agents", "/Admin/Agents", a => a.Has(Permission.AgentsManage)),
        new("Nagios", "/Admin/Nagios", a => a.Has(Permission.NagiosManage)),
        new("Working Calendar", "/Admin/Calendar", a => a.Has(Permission.CalendarManage)),
        new("Request flows", "/RequestCatalogue", AccessPolicy.CanConfigureRequests),
        new("Actions", "/Actions", a => a.Has(Permission.ActionsCreate)),
        new("Messages", "/Admin/Messages", a => a.Has(Permission.MessagesManage)),
        new("Activity Log", "/Admin/Activity", a => a.Has(Permission.AuditView))
    ];

    public static IReadOnlyList<NavItem> Visible(Actor a) => Main.Where(i => i.Shows(a)).ToList();

    public static IReadOnlyList<NavItem> AdminVisible(Actor a) => Admin.Where(i => i.Shows(a)).ToList();
}
