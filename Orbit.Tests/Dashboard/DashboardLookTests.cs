using MudBlazor;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Components.Dashboards;
using Orbit.Data.Entities;

namespace Orbit.Tests.Dashboard;

/// <summary>How the dashboard's charts are drawn (spec §6.9, decision 134), over <see cref="DashboardLook"/>.</summary>
public class DashboardLookTests
{
    private static readonly DashboardLinks Links = new(null, null);

    private static DashboardModel Model(DashboardView view, StatusCounts? todayPlan) => new()
    {
        Actor = Access.TestActors.Member(Guid.NewGuid()), View = view, Views = [view], Today = new DateOnly(2026, 10, 31), Links = Links,
        ScopeLabel = "", TodayPlan = todayPlan,
        OpenByAssignee = [new LoadBar("Ann", new StatusCounts(1, 0, 0, 0, 0, 0))],
        OpenByDepartment = [new LoadBar("IT", new StatusCounts(2, 0, 0, 0, 0, 0)), new LoadBar("HR", new StatusCounts(1, 0, 0, 0, 0, 0))]
    };

    /// <summary>
    /// DSH-009: a donut leaves out its empty entries and builds its palette from the ones it keeps, so each keeps its own colour;
    /// Today's plan runs closed first; every chart's options are the kind MudChart casts its type's options to; and a view builds
    /// only the charts it draws - the by-assignee bars on Department, the by-department bars on Company, Today's plan with a plan.
    /// </summary>
    [Fact]
    public void Charts_keep_each_entrys_colour_and_pair_options_with_their_type()
    {
        var types = DashboardLook.Types(
            [new EnumCount<TaskType>(TaskType.Audit, 0), new EnumCount<TaskType>(TaskType.Change, 2), new EnumCount<TaskType>(TaskType.Problem, 1)], Links);
        Assert.Equal(new[] { "Change", "Problem" }, types.Slices.Select(s => s.Label));
        Assert.Equal(new[] { "var(--orbit-type-change)", "var(--orbit-type-problem)" }, types.Options.ChartPalette);
        Assert.Equal(types.Labels, types.Slices.Select(s => s.Label));
        Assert.Equal(new double[] { 2, 1 }, types.Series.Single().Data.Values);
        Assert.Equal(3, types.Total);
        Assert.Equal("/Tasks?OpenOnly=true&Type=Change", types.Slices[0].Href);
        Assert.False(types.Options.ShowLegend);

        var priorities = DashboardLook.Priorities(Enum.GetValues<TaskPriority>().Select(p => new EnumCount<TaskPriority>(p, 1)).ToList(), Links);
        Assert.Equal(new[] { "Critical", "High", "Medium", "Low" }, priorities.Slices.Select(s => s.Label));

        var today = DashboardLook.TodayPlan(new StatusCounts(1, 2, 3, 4, 5, 6), Links);
        Assert.Equal(new[] { "Done", "Cancelled", "In Progress", "Waiting", "Blocked", "Todo" }, today.Slices.Select(s => s.Label));
        Assert.Equal("var(--orbit-status-done)", today.Options.ChartPalette[0]);
        Assert.Equal(0, DashboardLook.TodayPlan(StatusCounts.None, Links).Total);

        var bars = DashboardLook.Bars([new LoadBar("Ann", new StatusCounts(1, 2, 0, 1, 0, 0)), new LoadBar("Unassigned", new StatusCounts(3, 0, 0, 0, 0, 0))]);
        Assert.Equal(DashboardLook.OpenStatuses.Count, bars.Series.Count);
        Assert.Equal(new[] { "Ann", "Unassigned" }, bars.Labels);
        Assert.Equal(new double[] { 1, 3 }, bars.Series[0].Data.Values);
        Assert.Equal(DashboardLook.OpenStatuses.Select(DashboardLook.Status), bars.Options.ChartPalette);
        Assert.True(DashboardLook.Bars([]).IsEmpty);

        var trend = DashboardLook.Trend(DashboardRules.Daily([], [], new DateOnly(2026, 10, 31)));
        Assert.True(trend.IsEmpty);
        Assert.Equal(14, trend.Labels.Length);
        Assert.Equal("18", trend.Labels[0]);

        // MudChart casts its options to its type's own (ChartType.Donut to DonutChartOptions, ...): a mismatch throws.
        Assert.IsType<DonutChartOptions>(types.Options);
        Assert.IsType<StackedBarChartOptions>(bars.Options);
        Assert.IsType<LineChartOptions>(trend.Options);

        var mine = DashboardCharts.For(Model(DashboardView.Mine, null));
        Assert.Null(mine.Load);
        Assert.Null(mine.Today);
        Assert.Equal(new[] { "Ann" }, DashboardCharts.For(Model(DashboardView.Department, StatusCounts.None)).Load!.Labels);
        var company = DashboardCharts.For(Model(DashboardView.Company, new StatusCounts(1, 0, 0, 0, 1, 0)));
        Assert.Equal(new[] { "IT", "HR" }, company.Load!.Labels);
        Assert.Equal(2, company.Today!.Total);
    }
}
