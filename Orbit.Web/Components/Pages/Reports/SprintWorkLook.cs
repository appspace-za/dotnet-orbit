using Orbit.Application.Models;
using Orbit.Reporting;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// How Sprint status (spec §12) shows one kind of work, on the page, in the PDF and on the charts alike: its name in the tables and
/// on the charts, its chart series, the note under its group, and the By person table's two columns for it with their tooltips.
/// One entry per kind (<see cref="Of"/>), so adding a kind is one place to change. The charts call the Backlog and other-sprint
/// groups plain "Backlog" and "Other sprint"; the tables say "From the Backlog" and "From another sprint".
/// </summary>
public sealed record SprintWorkLook(
    SprintWorkKind Kind, string Name, string ChartName, SvgCharts.Series Series, string GroupNote,
    string TasksColumn, string TasksTitle, string TimeColumn, string TimeTitle)
{
    private static readonly SprintWorkLook Planned = new(SprintWorkKind.Planned, "Planned work", "Planned", SprintCharts.Planned,
        "Planned tasks on no project",
        "Planned tasks", "Their planned tasks (cancelled ones aside). A task several people share counts for each of them, and once in the total.",
        "On planned", "Their own time on planned tasks, dated in the sprint");

    private static readonly SprintWorkLook NotPlanned = new(SprintWorkKind.NotPlanned, SprintGroupRow.NotPlannedName,
        SprintGroupRow.NotPlannedName, SprintCharts.NotPlanned, "Work that came up during the sprint",
        "Not Planned tasks", "Their Not Planned tasks (cancelled ones aside)",
        "On Not Planned", "Their own time on Not Planned tasks, dated in the sprint");

    private static readonly SprintWorkLook Backlog = new(SprintWorkKind.Backlog, SprintGroupRow.BacklogName,
        SprintCharts.BacklogLabel, SprintCharts.Backlog, "Backlog tasks worked on during the sprint",
        "Backlog tasks", "Their Backlog tasks worked on during the sprint (cancelled ones aside)",
        "On Backlog", "Their own time on Backlog tasks, dated in the sprint");

    private static readonly SprintWorkLook OtherSprint = new(SprintWorkKind.OtherSprint, SprintGroupRow.OtherSprintName,
        SprintCharts.OtherSprintLabel, SprintCharts.OtherSprint, "Tasks of other sprints worked on during this one",
        "Other sprint tasks", "Their tasks of other sprints worked on during this one (cancelled ones aside)",
        "On other sprints", "Their own time on other sprints' tasks, dated in the sprint");

    public static SprintWorkLook Of(SprintWorkKind kind) => kind switch
    {
        SprintWorkKind.Planned => Planned,
        SprintWorkKind.NotPlanned => NotPlanned,
        SprintWorkKind.Backlog => Backlog,
        SprintWorkKind.OtherSprint => OtherSprint,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
