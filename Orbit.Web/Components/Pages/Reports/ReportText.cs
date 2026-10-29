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
    public static string Estimated(EstimateComparison c) => Estimated(c.EstimatedTasks, c.EstimatedMinutes);
    public static string Actual(EstimateComparison c) => c.EstimatedTasks == 0 ? "-" : TimeFormat.Minutes(c.ActualMinutes);

    /// <summary>"+1h 30m (+23%)", or "-" when nothing is estimated.</summary>
    public static string Variance(EstimateComparison c) =>
        c.VariancePercent is double p ? $"{TimeFormat.Signed(c.VarianceMinutes)} ({p:+0;-0;0}%)" : "-";

    public static string Estimate(int? minutes) => minutes is int m ? TimeFormat.Minutes(m) : "-";

    /// <summary>The note beside a task line: Cancelled, No estimate, Over estimate, or nothing.</summary>
    public static string Marker(TaskItemStatus status, int? estimate, bool isOver) =>
        status == TaskItemStatus.Cancelled ? "Cancelled" : EstimateNote(estimate, isOver);

    /// <summary>The estimate's note on a task that isn't cancelled: No estimate, Over estimate, or nothing.</summary>
    public static string EstimateNote(int? estimate, bool isOver) => estimate is null ? "No estimate" : isOver ? "Over estimate" : "";

    /// <summary>"12 projects: 8 Active · 1 On Hold · 3 Completed (2 in the period)", by status at the end of the period.</summary>
    public static string StatusCounts(ProjectStatusReport r) =>
        $"{r.Rows.Count} {(r.Rows.Count == 1 ? "project" : "projects")}: " + string.Join(" · ", r.StatusCounts.Select(c =>
            $"{c.Count} {c.Status.Label()}" + (c.ChangedInPeriod > 0 ? $" ({c.ChangedInPeriod} in the period)" : "")));

    /// <summary>"Active → Completed 2026-09-12".</summary>
    public static string Change(ProjectStatusChange c) => $"{c.From.Label()} → {c.To.Label()} {c.At:yyyy-MM-dd}";

    public static string Progress(ProjectTaskCounts t) => Progress(t.Done, t.Total);

    public static string Date(DateOnly? d) => d is DateOnly x ? x.ToString("yyyy-MM-dd") : "-";

    /// <summary>The people's estimate reads "-" when none of their tasks has one.</summary>
    public static string Estimated(ProjectPersonRow p) => Estimated(p.EstimatedTasks, p.EstimatedMinutes);

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

    // Sprint status.

    public static string Progress(SprintTaskCounts t) => Progress(t.Done, t.Total);

    public static string Label(SprintTaskOutcome o) => o switch
    {
        SprintTaskOutcome.Done => "Done",
        SprintTaskOutcome.LeftOpen => "Left open",
        _ => "Cancelled"
    };

    /// <summary>
    /// The note beside a task: where it came in from and went on to, a reopening, and - unless it was cancelled when the sprint ended -
    /// its estimate's note. The page shows Over estimate as a chip of its own, so it leaves it out here (<paramref name="withOver"/>).
    /// </summary>
    public static string Note(SprintTaskRow t, bool withOver = true)
    {
        var notes = new List<string>();
        if (t.CarriedInFrom is { } from) notes.Add($"Carried in from {from}");
        if (t.LeftOpenTo is { } to) notes.Add($"Left open → {to}");
        if (t.ReopenedSince) notes.Add("Reopened since");
        if (!t.IsCancelled && EstimateNote(t.EstimateMinutes, t.IsOver && withOver) is { Length: > 0 } marker) notes.Add(marker);
        return string.Join(" · ", notes);
    }

    /// <summary>
    /// The estimate left on the left-open tasks: "6h 30m (2 unestimated)", "none estimated" when none of them has an estimate, and
    /// "-" when none was left open.
    /// </summary>
    public static string Remaining(SprintTaskCounts c)
    {
        if (c.LeftOpen == 0) return "-";
        if (c.LeftOpenUnestimated == c.LeftOpen) return "none estimated";
        var left = TimeFormat.Minutes(c.LeftOpenRemainingMinutes);
        return c.LeftOpenUnestimated > 0 ? $"{left} ({c.LeftOpenUnestimated} unestimated)" : left;
    }

    public static string Percent(int? p) => p is int x ? $"{x}%" : "-";

    /// <summary>The people's estimate reads "-" when none of their tasks has one.</summary>
    public static string Estimated(SprintPersonRow p) => Estimated(p.EstimatedTasks, p.EstimatedMinutes);

    /// <summary>When the outcomes are read, and where the open tasks went: the line under the sprint's name.</summary>
    public static string AsAt(SprintStatusReport r) => r.Sprint switch
    {
        null => "",
        { Status: SprintStatus.Completed } s => $"Completed {Day(s.CompletedAt)}" + (r.LeftOpenTo is { } to ? $": the open tasks went to {to}." : "."),
        { Status: SprintStatus.Active } => "Active: outcomes are as things stand now, and the open tasks haven't moved on yet.",
        _ => "Not started yet."
    };

    /// <summary>"8/12 done", or "No tasks".</summary>
    private static string Progress(int done, int total) => total == 0 ? "No tasks" : $"{done}/{total} done";

    /// <summary>An estimate total, or "-" when none of the tasks has an estimate.</summary>
    private static string Estimated(int estimatedTasks, int minutes) => estimatedTasks == 0 ? "-" : TimeFormat.Minutes(minutes);

    // On screen only.

    /// <summary>A variance's colour: amber over the estimate, green under it.</summary>
    public static string VarianceClass(int minutes) => minutes > 0 ? "orbit-warn" : minutes < 0 ? "orbit-ok" : "";

    /// <summary>A bar's length, as a percentage of the longest.</summary>
    public static int Share(double value, double max) => max == 0 ? 0 : (int)Math.Round(value * 100.0 / max);
}
