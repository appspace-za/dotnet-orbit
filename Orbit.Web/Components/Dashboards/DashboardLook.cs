using System.Globalization;
using MudBlazor;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Components.Dashboards;

/// <summary>One entry of a dashboard chart's legend: its label, count, colour and, when the Tasks list can show it, a link.</summary>
public sealed record DashboardSlice(string Label, int Count, string Colour, string? Href);

/// <summary>A donut's MudChart inputs and the legend beside it, built once per load.</summary>
public sealed record DonutChartData(IReadOnlyList<DashboardSlice> Slices, List<ChartSeries<double>> Series, string[] Labels, DonutChartOptions Options)
{
    public int Total => Slices.Sum(s => s.Count);
    /// <summary>The chart read out for a screen reader: "Critical 2, High 5, ...".</summary>
    public string Summary => string.Join(", ", Slices.Select(s => $"{s.Label} {s.Count}"));
}

/// <summary>A stacked bar chart's MudChart inputs (one series per open status) and its legend.</summary>
public sealed record BarChartData(List<ChartSeries<double>> Series, string[] Labels, StackedBarChartOptions Options, IReadOnlyList<DashboardSlice> Legend, string Summary)
{
    public bool IsEmpty => Labels.Length == 0;
}

/// <summary>The Last 14 days line chart's MudChart inputs and its legend of totals.</summary>
public sealed record TrendChartData(List<ChartSeries<double>> Series, string[] Labels, LineChartOptions Options, IReadOnlyList<DashboardSlice> Legend,
    DateOnly From, DateOnly To, string Summary)
{
    public bool IsEmpty => Legend.All(s => s.Count == 0);
}

/// <summary>
/// Every chart one view of the dashboard draws, built from its model once per load rather than in the markup, and only the ones it
/// draws: <see cref="Today"/> for someone with Today's plan, <see cref="Load"/> - open work by assignee on the Department view, by
/// department on the Company view - on those two views.
/// </summary>
public sealed record DashboardCharts(DonutChartData Priority, DonutChartData Type, DonutChartData? Today, BarChartData? Load, TrendChartData Trend)
{
    public static DashboardCharts For(DashboardModel m) => new(
        DashboardLook.Priorities(m.ByPriority, m.Links),
        DashboardLook.Types(m.ByType, m.Links),
        m.TodayPlan is { } plan ? DashboardLook.TodayPlan(plan, m.Links) : null,
        m.View switch
        {
            DashboardView.Department => DashboardLook.Bars(m.OpenByAssignee),
            DashboardView.Company => DashboardLook.Bars(m.OpenByDepartment),
            _ => null
        },
        DashboardLook.Trend(m.Trend));
}

/// <summary>
/// How the dashboard draws (spec §6.9, decision 134): one colour per status, priority and type, used by every chart, legend and
/// tile, and the MudChart inputs. A colour is a CSS variable (app.css, light and dark): MudChart writes each palette entry into its
/// SVG's fill or stroke as given, so the marks follow the theme toggle. MudChart colours by position, so a chart that leaves out
/// its empty entries builds its palette from the entries it keeps - each keeps its own colour.
/// </summary>
public static class DashboardLook
{
    /// <summary>The statuses of open work, in the order the bars stack them and the tiles show them.</summary>
    public static readonly IReadOnlyList<TaskItemStatus> OpenStatuses =
        [TaskItemStatus.Todo, TaskItemStatus.InProgress, TaskItemStatus.Waiting, TaskItemStatus.Blocked];

    /// <summary>Today's plan, closed first so the donut fills clockwise as the day goes.</summary>
    public static readonly IReadOnlyList<TaskItemStatus> PlanOrder =
        [TaskItemStatus.Done, TaskItemStatus.Cancelled, TaskItemStatus.InProgress, TaskItemStatus.Waiting, TaskItemStatus.Blocked, TaskItemStatus.Todo];

    /// <summary>The Last 14 days chart's new tasks: the In progress indigo, beside Done's green.</summary>
    public const string NewTasks = "var(--orbit-status-in-progress)";

    /// <summary>
    /// The bar and line charts' height. In pixels, because MudChart sizes a percentage height from its own 350px fallback rather
    /// than its box, and fixed, so a card keeps its size however many bars there are.
    /// </summary>
    public const string PlotHeight = "240px";

    public static string Status(TaskItemStatus status) => status switch
    {
        TaskItemStatus.Todo => "var(--orbit-status-todo)",
        TaskItemStatus.InProgress => "var(--orbit-status-in-progress)",
        TaskItemStatus.Waiting => "var(--orbit-status-waiting)",
        TaskItemStatus.Blocked => "var(--orbit-status-blocked)",
        TaskItemStatus.Done => "var(--orbit-status-done)",
        _ => "var(--orbit-status-cancelled)"
    };

    /// <summary>A bar's or point's tooltip: "In Progress: 3".</summary>
    private const string Tooltip = "{{SERIES_NAME}}: {{Y_VALUE}}";

    public static string Priority(TaskPriority priority) => $"var(--orbit-priority-{priority.ToString().ToLowerInvariant()})";

    public static string Type(TaskType type) => $"var(--orbit-type-{type.ToString().ToLowerInvariant()})";

    /// <summary>Open tasks by priority, most urgent first, each linking to the list of them.</summary>
    public static DonutChartData Priorities(IReadOnlyList<EnumCount<TaskPriority>> counts, DashboardLinks links) =>
        Donut(counts.OrderByDescending(c => c.Value).Select(c => new DashboardSlice(c.Value.ToString(), c.Count, Priority(c.Value), links.Priority(c.Value))));

    /// <summary>Open tasks by type, in the type list's order, each linking to the list of them.</summary>
    public static DonutChartData Types(IReadOnlyList<EnumCount<TaskType>> counts, DashboardLinks links) =>
        Donut(counts.Select(c => new DashboardSlice(c.Value.ToString(), c.Count, Type(c.Value), links.Type(c.Value))));

    /// <summary>Today's plan by status (<see cref="PlanOrder"/>), each linking to the planned tasks in it.</summary>
    public static DonutChartData TodayPlan(StatusCounts plan, DashboardLinks links) =>
        Donut(PlanOrder.Select(s => new DashboardSlice(s.Label(), plan.Of(s), Status(s), links.Planned(s))));

    /// <summary>A donut of the entries with something in them; the palette follows the entries kept.</summary>
    public static DonutChartData Donut(IEnumerable<DashboardSlice> slices)
    {
        var kept = slices.Where(s => s.Count > 0).ToList();
        var series = new List<ChartSeries<double>>
        {
            new() { Name = "Tasks", Data = kept.Select(s => (double)s.Count).ToArray() }
        };
        // No tooltips: in a ring this small MudChart's tooltip is too small to read, and the legend beside it has every figure.
        var options = new DonutChartOptions
        {
            DonutRingRatio = 0.32,
            ShowLegend = false,
            ShowToolTips = false,
            ChartPalette = kept.Select(s => s.Colour).ToArray()
        };
        return new DonutChartData(kept, series, kept.Select(s => s.Label).ToArray(), options);
    }

    /// <summary>Stacked bars of open work, one series per open status, for the by-assignee and by-department charts.</summary>
    public static BarChartData Bars(IReadOnlyList<LoadBar> bars)
    {
        var series = OpenStatuses.Select(s => new ChartSeries<double>
        {
            Name = s.Label(),
            Data = bars.Select(b => (double)b.Counts.Of(s)).ToArray(),
            TooltipTitleFormat = Tooltip,
            TooltipSubtitleFormat = "{{X_VALUE}}",
            TooltipYValueFormat = "0"
        }).ToList();
        var options = new StackedBarChartOptions
        {
            ShowLegend = false,
            ShowValues = true,
            ShowZeroValues = false,
            YAxisTicks = 1,
            MaxNumYAxisTicks = 5,
            YAxisFormat = "0",
            XAxisLabelRotation = bars.Count > 3 ? 35 : 0, // names side by side run together on a phone
            ChartPalette = OpenStatuses.Select(Status).ToArray()
        };
        // MudChart sizes a bar by the width it has, so one or two bars would fill the card; a few keep a bar's width.
        if (bars.Count <= 3) options.FixedBarWidth = 56;
        var legend = OpenStatuses.Select(s => new DashboardSlice(s.Label(), 0, Status(s), null)).ToList();
        var summary = string.Join(", ", bars.Select(b => $"{b.Name} {b.Counts.Open} open"));
        return new BarChartData(series, bars.Select(b => b.Name).ToArray(), options, legend, summary);
    }

    /// <summary>The Last 14 days line chart: tasks created and tasks done per day, labelled by day of the month.</summary>
    public static TrendChartData Trend(IReadOnlyList<DayCount> days)
    {
        var done = Status(TaskItemStatus.Done);
        var series = new List<ChartSeries<double>>
        {
            new() { Name = "New", Data = days.Select(d => (double)d.Created).ToArray(), TooltipTitleFormat = Tooltip, TooltipYValueFormat = "0" },
            new() { Name = "Done", Data = days.Select(d => (double)d.Done).ToArray(), TooltipTitleFormat = Tooltip, TooltipYValueFormat = "0" }
        };
        var options = new LineChartOptions
        {
            ShowLegend = false,
            InterpolationOption = InterpolationOption.Straight,
            LineStrokeWidth = 2,
            ShowDataMarkers = true,
            YAxisRequireZeroPoint = true,
            YAxisTicks = 1,
            MaxNumYAxisTicks = 5,
            YAxisFormat = "0",
            ChartPalette = [NewTasks, done]
        };
        var created = days.Sum(d => d.Created);
        var closed = days.Sum(d => d.Done);
        var legend = new List<DashboardSlice> { new("New", created, NewTasks, null), new("Done", closed, done, null) };
        var labels = days.Select(d => d.Day.Day.ToString(CultureInfo.InvariantCulture)).ToArray();
        var from = days.Count > 0 ? days[0].Day : default;
        var to = days.Count > 0 ? days[^1].Day : default;
        return new TrendChartData(series, labels, options, legend, from, to, $"{created} tasks created and {closed} done in the last {days.Count} days");
    }
}
