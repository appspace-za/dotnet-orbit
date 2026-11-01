using MudBlazor;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Components.Tasks;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Components.Planning;

/// <summary>How the Planning page (spec §6.3) shows where a task sits and what its button does, against the sprint being planned.</summary>
public static class PlanningLook
{
    /// <summary>The placement chip's text: the sprint's name (marked when it is completed), Backlog or Not Planned.</summary>
    public static string Placement(TaskItem t) =>
        t.Sprint is { } s ? s.Status == SprintStatus.Completed ? $"{s.Name} (Completed)" : s.Name : SprintPlacement.Of(t).Label();

    /// <summary>A row in the sprint being planned is tinted (<c>orbit-plan-in</c>).</summary>
    public static string? RowClass(TaskItem t, Sprint? target) => target is not null && t.SprintId == target.Id ? "orbit-plan-in" : null;

    /// <summary>The placement chip's colour: filled primary for the sprint being planned, plain otherwise.</summary>
    public static Color PlacementColour(TaskItem t, Sprint? target) =>
        target is not null && t.SprintId == target.Id ? Color.Primary : Color.Default;

    /// <summary>Filled for the sprint being planned, outlined for another sprint or the Backlog, text for Not Planned.</summary>
    public static Variant PlacementVariant(TaskItem t, Sprint? target) =>
        target is not null && t.SprintId == target.Id ? Variant.Filled
        : t.SprintId is null && !t.InBacklog ? Variant.Text
        : Variant.Outlined;

    public static string ActionLabel(PlanningAction action) => action switch
    {
        PlanningAction.Remove => "Remove",
        PlanningAction.MoveHere => "Move here",
        _ => "Plan"
    };

    public static string ActionTitle(PlanningAction action, Sprint target) => action switch
    {
        PlanningAction.Remove => $"Take it out of {target.Name}, back to the Backlog",
        PlanningAction.MoveHere => $"Move it from its sprint into {target.Name}",
        _ => $"Plan it into {target.Name}"
    };

    public static string? ActionIcon(PlanningAction action) => action switch
    {
        PlanningAction.Remove => Icons.Material.Filled.Remove,
        PlanningAction.MoveHere => Icons.Material.Filled.ArrowForward,
        _ => Icons.Material.Filled.Add
    };

    /// <summary>Plan stands out (filled); Remove and Move here are outlined, so a row already in the sprint reads as done.</summary>
    public static Variant ActionVariant(PlanningAction action) => action == PlanningAction.Plan ? Variant.Filled : Variant.Outlined;

    /// <summary>"12 tasks · 30h", with "(3 without an estimate)" when some have none.</summary>
    public static string LoadText(PlanningLoad load) =>
        $"{Plural(load.Tasks, "task")} · {TimeFormat.Minutes(load.EstimatedMinutes)}"
        + (load.Unestimated > 0 ? $" ({load.Unestimated} without an estimate)" : "");

    public static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>
    /// PLN-009: whether a panel's table needs its Department column. A project's panel names the project's department in its
    /// header, so the column shows when a task is filed in another one, a cross-department project (§6.2.1), as on the project page.
    /// The <i>No project</i> panel names none, so it follows the task lists' rule (<see cref="TaskLook.MixesDepartments"/>).
    /// </summary>
    public static bool ShowsDepartment(PlanningGroup group, Actor actor) =>
        group.Project is { } p ? group.Tasks.Any(t => t.DepartmentId != p.DepartmentId) : TaskLook.MixesDepartments(actor, group.Tasks);

    /// <summary>
    /// PLN-010: the Assignee filter's options, by name. The people the viewer's lookups offer (<paramref name="directory"/>), plus
    /// anyone in <i>Load per person</i> who isn't among them (deactivated, or in a department the list leaves out), plus the person
    /// <paramref name="chosen"/>, named from a shown task. Picking a name there, or a remembered filter, then never shows a bare id.
    /// </summary>
    public static IReadOnlyList<(string Value, string Text)> AssigneeOptions(IEnumerable<UserSummary> directory, SprintPlanning plan, Guid? chosen)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var u in directory) names.TryAdd(u.Id, u.DisplayName);
        foreach (var p in plan.People)
            if (p.UserId is Guid id) names.TryAdd(id, p.Name);
        if (chosen is Guid c && !names.ContainsKey(c)
            && plan.Groups.SelectMany(g => g.Tasks).SelectMany(t => t.Assignments).FirstOrDefault(a => a.UserId == c)?.User is { } user)
            names[c] = user.DisplayName;
        return names.OrderBy(n => n.Value, StringComparer.CurrentCultureIgnoreCase).Select(n => (n.Key.ToString(), n.Value)).ToList();
    }
}
