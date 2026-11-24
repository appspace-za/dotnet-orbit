using Orbit.Application;
using Orbit.Application.Availability;
using Orbit.Application.Models;

namespace Orbit.Tests.Availability;

/// <summary>A new person's work pattern and the utilisation target's setting (spec §6.22).</summary>
public class WorkPatternsTests
{
    /// <summary>AVL-001 (§11): a new person's pattern is the organisation's working days times a working day, dated from their account.</summary>
    [Fact]
    public void The_default_pattern_is_the_working_week_times_a_working_day()
    {
        var id = Guid.NewGuid();
        var p = WorkPatterns.Default(id,
            new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday }, 7, new DateOnly(2026, 11, 21));

        Assert.Equal((id, new DateOnly(2026, 11, 21)), (p.UserId, p.EffectiveFrom));
        Assert.Equal(new WeekMinutes(420, 420, 420, 420, 0, 0, 0), p.Week);
    }

    /// <summary>§2.3: the target is 1 to 100; anything else falls back to 80.</summary>
    [Theory]
    [InlineData(0, 80)]
    [InlineData(101, 80)]
    [InlineData(-5, 80)]
    [InlineData(1, 1)]
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    public void The_target_falls_back_to_80_out_of_range(int configured, int effective) =>
        Assert.Equal(effective, new ReportOptions { UtilisationTargetPercent = configured }.EffectiveTarget);
}
