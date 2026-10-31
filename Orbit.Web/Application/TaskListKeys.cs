namespace Orbit.Application;

/// <summary>
/// The Tasks list's address (spec §6.2): its path and the key of each filter, which are the Razor page's keys so old links and
/// bookmarks still work. The list reads its filters under these keys and every link to it is built from them with
/// <see cref="Helpers.FilterQuery"/> (<see cref="Navigation.MyTasks(Guid)"/>, <see cref="DashboardLinks"/>), so a renamed key
/// can't leave a link silently filtering nothing.
/// </summary>
public static class TaskListKeys
{
    public const string Path = "/Tasks";

    public const string Search = "Search";
    public const string Department = "DepartmentId";
    /// <summary>Empty for all, "none" for the tasks on no project, or a project's id.</summary>
    public const string Project = "ProjectId";
    /// <summary>Empty for all, "none" for Not Planned, "backlog" for the Backlog, or a sprint's id (§6.3).</summary>
    public const string Sprint = "Sprint";
    public const string Status = "Status";
    /// <summary>Empty for all, "none" for the tasks nobody is assigned to, or a person's id.</summary>
    public const string Assignee = "AssigneeId";
    public const string Requestee = "RequesteeId";
    public const string Priority = "Priority";
    public const string Type = "Type";
    public const string Source = "Source";
    public const string DueAfter = "DueAfter";
    public const string DueBefore = "DueBefore";
    /// <summary>A due window counted from today when the list loads (<see cref="DueWindowRules"/>), so a link to it never goes stale.</summary>
    public const string Due = "Due";
    public const string OpenOnly = "OpenOnly";
    public const string PlannedToday = "PlannedToday";
    public const string PlannedFor = "PlannedFor";
    public const string Asset = "AssetId";

    /// <summary>
    /// The filters the list remembers for the browser session, in this order. The page number and the one-off "subtasks of one
    /// task" drill-down are not: a drill-down neither replaces nor is replaced by the remembered filter.
    /// </summary>
    public static readonly IReadOnlyList<string> Remembered =
    [
        Search, Department, Project, Status, Assignee, Priority, Type, Source, Due, DueAfter, DueBefore, OpenOnly, PlannedToday,
        PlannedFor, Asset, Requestee, Sprint
    ];
}
