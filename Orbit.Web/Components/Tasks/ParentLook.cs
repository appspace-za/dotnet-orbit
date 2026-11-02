using Orbit.Application;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Components.Tasks;

/// <summary>A range in a parent's cell: its start, and its end (null for one value), which a narrow screen puts on a line of its own.</summary>
public readonly record struct CellRange(string Start, string? End);

/// <summary>
/// The texts of a parent's row in the task tables (spec §6.15, design spec 2026-11-02 §4): its status chips, due range, sprint range
/// and subtask count, from the <see cref="ParentSummary"/> of its subtasks. The Blazor twin of <see cref="TaskLook"/> for parents.
/// </summary>
public static class ParentLook
{
    /// <summary>SUB-012: one chip per status among the bottom-level subtasks, "3 Done".</summary>
    public static string StatusChip(StatusCount c) => $"{c.Count} {c.Status.Label()}";

    /// <summary>SUB-013: the next due date of an open subtask, then the last due date (the parent's own); one date when they agree or nothing is open.</summary>
    public static CellRange Due(ParentSummary s, DateOnly? lastDue)
    {
        var (from, to) = ParentDisplayRules.DueRange(s.NextDue, lastDue);
        return from is null ? new(Ui.Day(to), null) : new(Ui.Day(from), Ui.Day(to));
    }

    /// <summary>SUB-014: the first and last sprint of the open subtasks, one name when they share one; null when none is in a sprint.</summary>
    public static CellRange? Sprints(ParentSummary s) =>
        s.FirstSprint is null ? null : new(s.FirstSprint, s.LastSprint is null || s.LastSprint == s.FirstSprint ? null : s.LastSprint);

    /// <summary>SUB-014: "+2 unplanned" after a sprint range, for the open subtasks in no sprint. Without a range every open one is unplanned, so nothing.</summary>
    public static string? Unplanned(ParentSummary s) => s.FirstSprint is null || s.Unplanned == 0 ? null : $"+{s.Unplanned} unplanned";

    /// <summary>The "(3)" beside a parent's title: its direct subtasks.</summary>
    public static string SubtaskCount(ParentSummary s) => $"({s.SubtaskCount})";

    public static string SubtaskCountTitle(ParentSummary s) => $"{s.SubtaskCount} subtask{(s.SubtaskCount == 1 ? "" : "s")}";
}
