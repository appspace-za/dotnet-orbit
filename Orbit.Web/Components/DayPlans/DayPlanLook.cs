using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Components.Dashboards;
using Orbit.Data.Entities;

namespace Orbit.Components.DayPlans;

/// <summary>
/// The Today page Summary's two donuts (spec §6.12), built once per load like the dashboard's charts: today's plan by status, each
/// status opening its planned tasks, and what became of the last plan.
/// </summary>
public sealed record DayPlanCharts(DonutChartData Today, StatusCounts TodayCounts, DonutChartData LastPlan, int LastPlanDone)
{
    public static DayPlanCharts For(DayPlan plan, DashboardLinks links)
    {
        var counts = StatusCounts.From(plan.Planned.GroupBy(t => t.Status).Select(g => (g.Key, g.Count())));
        var last = DayPlanRules.LastPlan(plan);
        return new DayPlanCharts(DashboardLook.TodayPlan(counts, links), counts, DayPlanLook.LastPlan(last), last.Done);
    }
}

/// <summary>How the Today page draws (spec §6.12): the last plan's outcome in the dashboard's colours, and a load in words.</summary>
public static class DayPlanLook
{
    /// <summary>Not finished: the warning colour of the Not finished card.</summary>
    public const string NotFinished = "var(--mud-palette-warning)";

    /// <summary>
    /// The last plan's tasks by outcome: done, cancelled, carried over to today and still on it - that day's tasks only, as the
    /// Yesterday view's table counts them (<see cref="DayPlanRules.LastPlan"/>).
    /// </summary>
    public static DonutChartData LastPlan(LastPlanOutcome last) => DashboardLook.Donut(
    [
        new DashboardSlice("Done", last.Done, DashboardLook.Status(TaskItemStatus.Done), null),
        new DashboardSlice("Cancelled", last.Closed.Count - last.Done, DashboardLook.Status(TaskItemStatus.Cancelled), null),
        new DashboardSlice("Carried over", last.CarriedOver.Count, DashboardLook.Status(TaskItemStatus.InProgress), null),
        new DashboardSlice("Not finished", last.NotFinished.Count, NotFinished, null)
    ]);

    /// <summary>A person's estimate in words, as the Today page has always put it: "4h 15m still open of 6h 30m estimated".</summary>
    public static string LoadText(DayPlanGroup g) => g.EstimatedMinutes == g.OpenEstimatedMinutes
        ? $"{TimeFormat.Minutes(g.OpenEstimatedMinutes)} estimated"
        : $"{TimeFormat.Minutes(g.OpenEstimatedMinutes)} still open of {TimeFormat.Minutes(g.EstimatedMinutes)} estimated";

    /// <summary>The overloaded chip's tooltip.</summary>
    public static string LoadTitle(int workingDayMinutes) =>
        "Sum of the estimates of the tasks on this person's plan; a task shared with other assignees counts in full for each of them. " +
        $"More than a working day ({TimeFormat.Minutes(workingDayMinutes)}) still open is flagged as overloaded.";
}
