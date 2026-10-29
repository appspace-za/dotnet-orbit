using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Sprints;

/// <summary>Where a task sits for sprint planning: a sprint, the Backlog or Not Planned (spec §6.3, §13 item 116).</summary>
public class SprintPlacementTests
{
    private static readonly Guid SprintId = Guid.Parse("8f0c2a4e-1b3d-4c5e-9f60-718293a4b5c6");

    /// <summary>SPR-001: a task's placement - its sprint wins over the flag; with none, the flag picks the Backlog or Not Planned.</summary>
    [Fact]
    public void Of_reads_the_three_placements()
    {
        Assert.Equal(SprintPlacement.In(SprintId), SprintPlacement.Of(new TaskItem { SprintId = SprintId }));
        Assert.Equal(SprintPlacement.In(SprintId), SprintPlacement.Of(new TaskItem { SprintId = SprintId, InBacklog = true }));
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.Of(new TaskItem { InBacklog = true }));
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.Of(new TaskItem()));

        Assert.True(SprintPlacement.NotPlanned.IsNotPlanned);
        Assert.Equal(SprintPlacement.NotPlanned, default);
        Assert.False(SprintPlacement.Backlog.IsNotPlanned);
        Assert.False(SprintPlacement.In(SprintId).IsBacklog);
        Assert.True(SprintPlacement.In(SprintId).IsSprint);
        Assert.Throws<ValidationException>(() => SprintPlacement.In(Guid.Empty));
    }

    /// <summary>SPR-002: applying a placement writes both fields - a sprint clears the backlog flag, and leaving one clears the sprint.</summary>
    [Fact]
    public void ApplyTo_writes_both_fields()
    {
        var task = new TaskItem { InBacklog = true };
        SprintPlacement.In(SprintId).ApplyTo(task);
        Assert.Equal(SprintId, task.SprintId);
        Assert.False(task.InBacklog);

        SprintPlacement.Backlog.ApplyTo(task);
        Assert.Null(task.SprintId);
        Assert.True(task.InBacklog);

        SprintPlacement.In(SprintId).ApplyTo(task);
        SprintPlacement.NotPlanned.ApplyTo(task);
        Assert.Null(task.SprintId);
        Assert.False(task.InBacklog);
    }

    /// <summary>
    /// SPR-003: a new task starts on the Backlog on a project and Not Planned without one; a save's sprint wins, then the flag it
    /// gives, then the fallback.
    /// </summary>
    [Fact]
    public void Defaults_and_choice()
    {
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.DefaultFor(Guid.NewGuid()));
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.DefaultFor(null));

        Assert.Equal(SprintPlacement.In(SprintId), SprintPlacement.Choose(SprintId, true, SprintPlacement.Backlog));
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.Choose(null, true, SprintPlacement.NotPlanned));
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.Choose(null, false, SprintPlacement.Backlog));
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.Choose(null, null, SprintPlacement.Backlog));
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.Choose(null, null, SprintPlacement.NotPlanned));
    }

    /// <summary>SPR-004: "none" is Not Planned, "backlog" the Backlog and a GUID that sprint, in any case and with spaces; nothing else is a placement.</summary>
    [Fact]
    public void TryParse_reads_the_address_and_mcp_spellings()
    {
        Assert.True(SprintPlacement.TryParse(" None ", out var notPlanned));
        Assert.Equal(SprintPlacement.NotPlanned, notPlanned);
        Assert.True(SprintPlacement.TryParse("BACKLOG", out var backlog));
        Assert.Equal(SprintPlacement.Backlog, backlog);
        Assert.True(SprintPlacement.TryParse($"  {SprintId.ToString().ToUpperInvariant()} ", out var sprint));
        Assert.Equal(SprintPlacement.In(SprintId), sprint);

        foreach (var bad in new[] { null, "", "   ", "notplanned", "sprint", "123", Guid.Empty.ToString() })
            Assert.False(SprintPlacement.TryParse(bad, out _), $"\"{bad}\" isn't a placement");
    }

    /// <summary>SPR-005: a placement's value reads back as the same placement.</summary>
    [Fact]
    public void Value_round_trips()
    {
        foreach (var placement in new[] { SprintPlacement.NotPlanned, SprintPlacement.Backlog, SprintPlacement.In(SprintId) })
        {
            Assert.True(SprintPlacement.TryParse(placement.Value, out var read));
            Assert.Equal(placement, read);
        }
        Assert.Equal("none", SprintPlacement.NotPlanned.Value);
        Assert.Equal("backlog", SprintPlacement.Backlog.Value);
        Assert.Equal(SprintId.ToString(), SprintPlacement.In(SprintId).Value);
    }

    /// <summary>
    /// SPR-007: a save that takes a task out of its sprint without naming a place puts it on the Backlog, as completing a sprint
    /// does; a task already in no sprint keeps its own place.
    /// </summary>
    [Fact]
    public void WithoutSprint_sends_planned_work_back_to_the_backlog()
    {
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.In(SprintId).WithoutSprint);
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.Backlog.WithoutSprint);
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.NotPlanned.WithoutSprint);

        // UpdateAsync's fallback: no sprint and no flag given.
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.Choose(null, null, SprintPlacement.In(SprintId).WithoutSprint));
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.Choose(null, null, SprintPlacement.NotPlanned.WithoutSprint));
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.Choose(null, false, SprintPlacement.In(SprintId).WithoutSprint));
    }

    /// <summary>SPR-008: Read gives TryParse's placement, or null for a blank or unreadable value.</summary>
    [Fact]
    public void Read_gives_the_placement_or_null()
    {
        Assert.Equal(SprintPlacement.NotPlanned, SprintPlacement.Read("none"));
        Assert.Equal(SprintPlacement.Backlog, SprintPlacement.Read(" Backlog "));
        Assert.Equal(SprintPlacement.In(SprintId), SprintPlacement.Read(SprintId.ToString()));

        foreach (var bad in new[] { null, "", "  ", "sprint", Guid.Empty.ToString() })
            Assert.Null(SprintPlacement.Read(bad));
    }

    /// <summary>SPR-006: the labels the pickers, lists and task page show.</summary>
    [Fact]
    public void Labels()
    {
        Assert.Equal("Not Planned", SprintPlacement.NotPlanned.Label());
        Assert.Equal("Backlog", SprintPlacement.Backlog.Label("ignored"));
        Assert.Equal("Sprint 42", SprintPlacement.In(SprintId).Label("Sprint 42"));
        Assert.Equal(string.Empty, SprintPlacement.In(SprintId).Label());
    }
}
