using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Availability;

/// <summary>What the availability forms accept (spec §6.22).</summary>
public class AvailabilityInputRulesTests
{
    /// <summary>AVL-006: unavailable time's dates, hours and note are checked; the messages say what to fix.</summary>
    [Fact]
    public void Unavailable_time_is_checked()
    {
        var d = new DateOnly(2026, 11, 23);
        Assert.Null(AvailabilityInputRules.CheckUnavailable(d, d, null, null));
        Assert.Equal("The end date can't be before the start date.", AvailabilityInputRules.CheckUnavailable(d, d.AddDays(-1), null, null));
        Assert.Null(AvailabilityInputRules.CheckUnavailable(d, d.AddDays(366), null, null));
        Assert.Equal("Unavailable time can cover at most 366 days.", AvailabilityInputRules.CheckUnavailable(d, d.AddDays(367), null, null));
        const string hours = "Hours a day must be between 15 minutes and 24 hours, in quarter hours.";
        Assert.Equal(hours, AvailabilityInputRules.CheckUnavailable(d, d, 0, null));
        Assert.Equal(hours, AvailabilityInputRules.CheckUnavailable(d, d, 50, null));
        Assert.Equal(hours, AvailabilityInputRules.CheckUnavailable(d, d, 1455, null));
        Assert.Null(AvailabilityInputRules.CheckUnavailable(d, d, 15, new string('x', 500)));
        Assert.Equal("The note can't exceed 500 characters.", AvailabilityInputRules.CheckUnavailable(d, d, null, new string('x', 501)));
    }

    /// <summary>AVL-006: a pattern's days are 0 to 24 hours in quarters (a 0-hour week is allowed); the only pattern can't be removed.</summary>
    [Fact]
    public void Patterns_are_checked()
    {
        Assert.Null(AvailabilityInputRules.CheckPattern(WeekMinutes.None));
        Assert.Null(AvailabilityInputRules.CheckPattern(new WeekMinutes(1440, 0, 0, 0, 0, 0, 15)));
        const string message = "Each day's hours must be between 0 and 24, in quarter hours.";
        Assert.Equal(message, AvailabilityInputRules.CheckPattern(new WeekMinutes(470, 0, 0, 0, 0, 0, 0)));
        Assert.Equal(message, AvailabilityInputRules.CheckPattern(new WeekMinutes(0, 0, 0, 0, 0, 0, 1455)));
        Assert.Equal(message, AvailabilityInputRules.CheckPattern(new WeekMinutes(0, -15, 0, 0, 0, 0, 0)));
        Assert.Equal("A person needs a work pattern: change it instead of removing their only one.", AvailabilityInputRules.CheckRemovePattern(1));
        Assert.Null(AvailabilityInputRules.CheckRemovePattern(2));
    }

    /// <summary>AVL-006 and Review Focus 1: hours read with a point or a comma, in quarters, 0 to 24.</summary>
    [Theory]
    [InlineData("7.5", 450)]
    [InlineData("7,5", 450)]
    [InlineData("7.25", 435)]
    [InlineData(" 8 ", 480)]
    [InlineData("0", 0)]
    [InlineData("24", 1440)]
    public void Hours_are_read(string text, int minutes)
    {
        Assert.True(AvailabilityInputRules.TryParseHours(text, out var m));
        Assert.Equal(minutes, m);
    }

    /// <summary>AVL-006: anything else is refused.</summary>
    [Theory]
    [InlineData("7.3")]
    [InlineData("24.25")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1,000")]
    public void Bad_hours_are_refused(string? text) => Assert.False(AvailabilityInputRules.TryParseHours(text, out _));

    /// <summary>AVL-006: hours are written as decimals of an hour, and reasons have a short form for the grid.</summary>
    [Fact]
    public void Hours_and_reasons_are_written()
    {
        Assert.Equal(["8h", "7.5h", "7.25h", "0h"], new[] { 480, 450, 435, 0 }.Select(AvailabilityInputRules.FormatHours));
        Assert.Equal(["Lve", "Sick", "Trn", "Oth"], Enum.GetValues<UnavailableReason>().Select(AvailabilityInputRules.ShortReason));
    }
}
