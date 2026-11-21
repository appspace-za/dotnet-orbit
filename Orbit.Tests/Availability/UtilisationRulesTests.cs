using Orbit.Application.Availability;

namespace Orbit.Tests.Availability;

/// <summary>Logged against available (spec §6.22, §12 Time utilisation).</summary>
public class UtilisationRulesTests
{
    /// <summary>AVL-009: a whole percentage rounded away from zero and never capped; none without availability.</summary>
    [Fact]
    public void Utilisation_is_a_whole_percentage_of_the_available_time()
    {
        Assert.Equal(80, UtilisationRules.Percent(1920, 2400));
        Assert.Equal(113, UtilisationRules.Percent(2700, 2400));
        Assert.Null(UtilisationRules.Percent(60, 0));
        Assert.Null(UtilisationRules.Percent(0, 0));
    }

    /// <summary>AVL-009: under target only below the target's share of real availability.</summary>
    [Fact]
    public void Under_target_needs_availability()
    {
        Assert.True(UtilisationRules.UnderTarget(1919, 2400, 80));
        Assert.False(UtilisationRules.UnderTarget(1920, 2400, 80));
        Assert.False(UtilisationRules.UnderTarget(0, 0, 80));
    }

    /// <summary>AVL-009: My Time's line.</summary>
    [Fact]
    public void My_time_says_logged_of_available()
    {
        Assert.Equal("Logged 32h of 40h available · 80%", UtilisationRules.MyTimeLine(1920, 2400));
        Assert.Equal("Logged 3h · no availability in this range", UtilisationRules.MyTimeLine(180, 0));
    }
}
