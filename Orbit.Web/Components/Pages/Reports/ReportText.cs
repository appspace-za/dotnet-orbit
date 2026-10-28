using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// How the reports (spec §12) write their figures, on screen and in the PDF (<see cref="ReportPdfEndpoint"/>), so the two read
/// the same.
/// </summary>
public static class ReportText
{
    // The estimate columns show "-" rather than "0m" when none of the tasks has an estimate.
    public static string Estimated(EstimateComparison c) => c.EstimatedTasks == 0 ? "-" : TimeFormat.Minutes(c.EstimatedMinutes);
    public static string Actual(EstimateComparison c) => c.EstimatedTasks == 0 ? "-" : TimeFormat.Minutes(c.ActualMinutes);

    /// <summary>"+1h 30m (+23%)", or "-" when nothing is estimated.</summary>
    public static string Variance(EstimateComparison c) =>
        c.VariancePercent is double p ? $"{TimeFormat.Signed(c.VarianceMinutes)} ({p:+0;-0;0}%)" : "-";

    public static string Estimate(int? minutes) => minutes is int m ? TimeFormat.Minutes(m) : "-";

    /// <summary>The note beside a task line: Cancelled, No estimate, Over estimate, or nothing.</summary>
    public static string Marker(TaskItemStatus status, int? estimate, bool isOver) =>
        status == TaskItemStatus.Cancelled ? "Cancelled" : estimate is null ? "No estimate" : isOver ? "Over estimate" : "";

    /// <summary>"12 projects: 8 Active · 1 On Hold · 3 Completed (2 in the period)", by status at the end of the period.</summary>
    public static string StatusCounts(ProjectStatusReport r) =>
        $"{r.Rows.Count} {(r.Rows.Count == 1 ? "project" : "projects")}: " + string.Join(" · ", r.StatusCounts.Select(c =>
            $"{c.Count} {c.Status.Label()}" + (c.ChangedInPeriod > 0 ? $" ({c.ChangedInPeriod} in the period)" : "")));

    /// <summary>"Active → Completed 2026-09-12".</summary>
    public static string Change(ProjectStatusChange c) => $"{c.From.Label()} → {c.To.Label()} {c.At:yyyy-MM-dd}";

    public static string Progress(ProjectTaskCounts t) => t.Total == 0 ? "No tasks" : $"{t.Done}/{t.Total} done";

    public static string Date(DateOnly? d) => d is DateOnly x ? x.ToString("yyyy-MM-dd") : "-";

    /// <summary>The people's estimate reads "-" when none of their tasks has one.</summary>
    public static string Estimated(ProjectPersonRow p) => p.EstimatedTasks == 0 ? "-" : TimeFormat.Minutes(p.EstimatedMinutes);

    /// <summary>
    /// "139 assets: 122 Active · 3 Damaged (2 in the period) · 5 Disposed (5 in the period) · 6 registered in the period", by
    /// status at the end of the period.
    /// </summary>
    public static string AssetStatusCounts(AssetStatusReport r)
    {
        var total = r.Total.Total;
        var text = $"{total} {(total == 1 ? "asset" : "assets")}: " + string.Join(" · ", r.StatusCounts.Select(c =>
            $"{c.Count} {c.Status.Label()}" + (c.ChangedInPeriod > 0 ? $" ({c.ChangedInPeriod} in the period)" : "")));
        return r.Total.Registered > 0 ? $"{text} · {r.Total.Registered} registered in the period" : text;
    }

    /// <summary>"35 of 40": the assets held at the end of the period that were checked in it.</summary>
    public static string Checked(AssetGroupCounts c) => c.Held == 0 ? "-" : $"{c.Checked} of {c.Held}";

    /// <summary>The value at cost of the assets held at the end of the period; "-" when none of them has a value.</summary>
    public static string HeldValue(AssetGroupCounts c) => c.HeldUnvalued == c.Held ? "-" : Ui.Money(c.HeldValue);

    public static string AssetLabel(AssetWorkRow a) => AssetRules.Label(a.AssetNumber, a.Name);

    public static string Day(DateTime? utc) => utc is DateTime d ? d.ToString("yyyy-MM-dd") : "-";

    // On screen only.

    /// <summary>A variance's colour: amber over the estimate, green under it.</summary>
    public static string VarianceClass(int minutes) => minutes > 0 ? "orbit-warn" : minutes < 0 ? "orbit-ok" : "";

    /// <summary>A bar's length, as a percentage of the longest.</summary>
    public static int Share(double value, double max) => max == 0 ? 0 : (int)Math.Round(value * 100.0 / max);
}
