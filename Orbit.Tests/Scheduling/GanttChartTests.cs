using Orbit.Data.Entities;
using Orbit.Helpers;
using static Orbit.Tests.Scheduling.Fixture;

namespace Orbit.Tests.Scheduling;

/// <summary>The Gantt's drawing of parents (spec §6.15, §6.16, decision 146).</summary>
public class GanttChartTests
{
    /// <summary>SUB-025: a parent draws only its summary span, and a link to or from it is an arrow attached to that span, not an undrawn link.</summary>
    [Fact]
    public void Parent_draws_a_summary_span_and_links_attach_to_it()
    {
        var phase1 = Task("Phase 1", "2026-10-05", "2026-10-08");
        phase1.HasSubtasks = true;
        var phase2 = Task("Phase 2", "2026-10-09", "2026-10-12");
        phase2.HasSubtasks = true;
        var a = Task("A", "2026-10-05", "2026-10-08", parentId: phase1.Id);
        var b = Task("B", "2026-10-09", "2026-10-12", parentId: phase2.Id);
        var link = Link(phase1, phase2);

        var chart = GanttChart.Build([phase1, phase2, a, b], [link], D("2026-10-01"), false);

        var row = chart.Rows.Single(r => r.Task.Id == phase1.Id);
        Assert.Null(row.Bar);
        Assert.Null(row.Milestone);
        Assert.NotNull(row.Summary);
        Assert.Empty(chart.UndrawnLinks);
        var arrow = Assert.Single(chart.Arrows);
        Assert.Equal(link.Id, arrow.Link.Id);
        // The path starts on the summary line of the predecessor's row.
        var startY = GanttChart.RowY(row.Index) + GanttChart.SummaryTop + GanttChart.SummaryHeight / 2;
        Assert.Contains($",{startY} ", arrow.Path);
    }
}
