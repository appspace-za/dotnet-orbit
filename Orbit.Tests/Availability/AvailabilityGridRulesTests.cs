using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Availability;

/// <summary>The Availability page's team grid (spec §6.22): its ranges and its cells.</summary>
public class AvailabilityGridRulesTests
{
    private static readonly DateOnly Monday = new(2026, 11, 23);

    private static AvailabilityDay Day(int pattern, int available, RemovedPart[]? removed = null, string? holiday = null, bool employed = true) =>
        new(Monday, employed, pattern, available, removed ?? [], holiday);

    /// <summary>AVL-007: a week runs Monday to Sunday and a month from its first day to its last; each steps a whole period.</summary>
    [Fact]
    public void Ranges_snap_to_the_week_or_month_and_step_by_one()
    {
        Assert.Equal((Monday, new DateOnly(2026, 11, 29)), AvailabilityGridRules.Range(AvailabilityView.Week, new DateOnly(2026, 11, 25)));
        Assert.Equal((Monday, new DateOnly(2026, 11, 29)), AvailabilityGridRules.Range(AvailabilityView.Week, new DateOnly(2026, 11, 29)));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), AvailabilityGridRules.Range(AvailabilityView.Month, new DateOnly(2026, 11, 25)));
        Assert.Equal(new DateOnly(2026, 11, 16), AvailabilityGridRules.Step(AvailabilityView.Week, Monday, -1));
        Assert.Equal(new DateOnly(2026, 12, 1), AvailabilityGridRules.Step(AvailabilityView.Month, new DateOnly(2026, 11, 1), 1));
    }

    /// <summary>AVL-007: a cell shows the hours left, shades a reduced day with its reasons, and explains itself on hover.</summary>
    [Fact]
    public void Cells_show_hours_reasons_and_why()
    {
        var full = AvailabilityGridRules.Cell(Day(480, 480));
        Assert.Equal(("8h", false, "Pattern 8h · 8h available"), (full.Text, full.Reduced, full.Hover));

        var leave = AvailabilityGridRules.Cell(Day(480, 300, [new RemovedPart(Guid.NewGuid(), UnavailableReason.Leave, 180)]));
        Assert.Equal(("5h", true, "Pattern 8h · Leave 3h → 5h available"), (leave.Text, leave.Reduced, leave.Hover));
        Assert.Equal(["Lve"], leave.Reasons);

        var two = AvailabilityGridRules.Cell(Day(480, 0,
        [
            new RemovedPart(Guid.NewGuid(), UnavailableReason.Training, 120),
            new RemovedPart(Guid.NewGuid(), UnavailableReason.Sick, 360)
        ]));
        Assert.Equal(["Trn", "Sick"], two.Reasons);
        Assert.Equal("Pattern 8h · Training 2h · Sick 6h → 0h available", two.Hover);

        var holiday = AvailabilityGridRules.Cell(Day(480, 0, holiday: "Heritage Day"));
        Assert.Equal(("0h", true, "Public holiday: Heritage Day"), (holiday.Text, holiday.Holiday, holiday.Hover));

        var off = AvailabilityGridRules.Cell(Day(0, 0));
        Assert.Equal(("-", "Not a working day"), (off.Text, off.Hover));

        var gone = AvailabilityGridRules.Cell(Day(0, 0, employed: false));
        Assert.Equal((true, "", "Not employed"), (gone.Blank, gone.Text, gone.Hover));
    }
}
