using Orbit.Application;
using Orbit.Components.Tasks;
using Orbit.Data.Entities;

namespace Orbit.Tests.Tasks;

/// <summary>What a parent's row says in the task tables (design spec 2026-11-02 §4, §6; orbit-spec §6.15), over <see cref="ParentLook"/>.</summary>
public class ParentLookTests
{
    private static ParentSummary S(DateOnly? nextDue = null, bool overdue = false, string? first = null, string? last = null,
        int unplanned = 0, int subtasks = 2, params StatusCount[] statuses) =>
        new(subtasks, statuses, nextDue, overdue, first, last, unplanned, []);

    /// <summary>SUB-012: a status chip reads its count and the status's label.</summary>
    [Fact]
    public void Status_chips_read_count_and_label()
    {
        Assert.Equal("3 Done", ParentLook.StatusChip(new StatusCount(TaskItemStatus.Done, 3)));
        Assert.Equal("1 In Progress", ParentLook.StatusChip(new StatusCount(TaskItemStatus.InProgress, 1)));
    }

    /// <summary>SUB-013: the due cell runs from the next open due date to the last; one date when they agree or nothing is open.</summary>
    [Fact]
    public void Due_cell_is_a_range_or_one_date()
    {
        var last = new DateOnly(2026, 12, 15);
        Assert.Equal(new CellRange("2026-11-10", "2026-12-15"), ParentLook.Due(S(new DateOnly(2026, 11, 10)), last));
        Assert.Equal(new CellRange("2026-12-15", null), ParentLook.Due(S(last), last));
        Assert.Equal(new CellRange("2026-12-15", null), ParentLook.Due(S(), last));
        Assert.Equal(new CellRange("-", null), ParentLook.Due(S(), null));
    }

    /// <summary>SUB-014: the sprint cell runs from the first open sprint to the last, one name when they share one, none without one.</summary>
    [Fact]
    public void Sprint_cell_is_a_range_or_one_name()
    {
        Assert.Equal(new CellRange("Sprint 12", "Sprint 14"), ParentLook.Sprints(S(first: "Sprint 12", last: "Sprint 14")));
        Assert.Equal(new CellRange("Sprint 12", null), ParentLook.Sprints(S(first: "Sprint 12", last: "Sprint 12")));
        Assert.Null(ParentLook.Sprints(S()));
    }

    /// <summary>SUB-014: "+N unplanned" follows a sprint range only; with no open subtask in a sprint the placement says it all.</summary>
    [Fact]
    public void Unplanned_follows_a_sprint_range()
    {
        Assert.Equal("+2 unplanned", ParentLook.Unplanned(S(first: "Sprint 12", last: "Sprint 12", unplanned: 2)));
        Assert.Null(ParentLook.Unplanned(S(first: "Sprint 12", last: "Sprint 12")));
        Assert.Null(ParentLook.Unplanned(S(unplanned: 3)));
    }

    /// <summary>The "(n)" beside a parent's title counts its direct subtasks.</summary>
    [Fact]
    public void Subtask_count_beside_the_title()
    {
        Assert.Equal("(3)", ParentLook.SubtaskCount(S(subtasks: 3)));
        Assert.Equal("1 subtask", ParentLook.SubtaskCountTitle(S(subtasks: 1)));
        Assert.Equal("3 subtasks", ParentLook.SubtaskCountTitle(S(subtasks: 3)));
    }
}
