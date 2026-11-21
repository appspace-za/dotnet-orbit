using Orbit.Application.Availability;
using Orbit.Application.Models;

namespace Orbit.Tests.Availability;

/// <summary>When a person counted as employed (spec §6.22): from their account's creation, paused while deactivated.</summary>
public class EmploymentRulesTests
{
    private static readonly DateTime Created = new(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int m, int d, int h = 9) => new(2026, m, d, h, 0, 0, DateTimeKind.Utc);

    /// <summary>AVL-004: a deactivation ends a period on its day, a reactivation opens one on its day.</summary>
    [Fact]
    public void Periods_follow_deactivation_and_reactivation()
    {
        var periods = EmploymentRules.Periods(Created, true, [new EmploymentEvent(At(5, 15), false), new EmploymentEvent(At(6, 1, 8), true)]);

        Assert.Equal([new EmploymentPeriod(new DateOnly(2026, 3, 10), new DateOnly(2026, 5, 15)), new EmploymentPeriod(new DateOnly(2026, 6, 1), null)], periods);
    }

    /// <summary>AVL-004: events out of order are read in time order.</summary>
    [Fact]
    public void Events_are_read_in_time_order()
    {
        var periods = EmploymentRules.Periods(Created, true, [new EmploymentEvent(At(6, 1, 8), true), new EmploymentEvent(At(5, 15), false)]);

        Assert.Equal(2, periods.Count);
        Assert.Equal(new DateOnly(2026, 5, 15), periods[0].To);
    }

    /// <summary>AVL-004: an account inactive now whose trail doesn't say when counts as deactivated on the day it was created.</summary>
    [Fact]
    public void An_inactive_account_with_no_deactivation_ends_on_its_creation_day()
    {
        Assert.Equal([new EmploymentPeriod(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10))], EmploymentRules.Periods(Created, false, []));
    }

    /// <summary>AVL-004: deactivated and reactivated on the same day is one unbroken period.</summary>
    [Fact]
    public void Deactivated_and_reactivated_the_same_day_is_one_period()
    {
        var periods = EmploymentRules.Periods(Created, true, [new EmploymentEvent(At(5, 15, 9), false), new EmploymentEvent(At(5, 15, 11), true)]);

        Assert.Equal([new EmploymentPeriod(new DateOnly(2026, 3, 10), null)], periods);
    }

    /// <summary>AVL-004: a deactivation while inactive and a reactivation while active change nothing.</summary>
    [Fact]
    public void Repeated_events_are_ignored()
    {
        var periods = EmploymentRules.Periods(Created, true,
        [
            new EmploymentEvent(At(4, 1), true),
            new EmploymentEvent(At(5, 15), false),
            new EmploymentEvent(At(5, 20), false),
            new EmploymentEvent(At(6, 1), true)
        ]);

        Assert.Equal([new EmploymentPeriod(new DateOnly(2026, 3, 10), new DateOnly(2026, 5, 15)), new EmploymentPeriod(new DateOnly(2026, 6, 1), null)], periods);
    }
}
