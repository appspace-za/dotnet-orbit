using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Availability;

/// <summary>A person's available time per day (spec §6.22): pattern, holidays, unavailable time and employment.</summary>
public class AvailabilityRulesTests
{
    private static readonly WeekMinutes FullTime = new(480, 480, 480, 480, 480, 0, 0);
    private static readonly WeekMinutes MonTue = new(480, 480, 0, 0, 0, 0, 0);
    private static readonly IReadOnlyDictionary<DateOnly, string> NoHolidays = new Dictionary<DateOnly, string>();
    private static readonly EmploymentPeriod Always = new(new DateOnly(2020, 1, 1), null);

    private static PersonAvailabilityFacts Person(
        PatternFacts[] patterns, UnavailableFacts[]? off = null, EmploymentPeriod[]? employment = null, bool system = false) =>
        new(Guid.NewGuid(), system, patterns, off ?? [], employment ?? [Always]);

    private static PatternFacts P(int y, int m, int d, WeekMinutes w) => new(Guid.NewGuid(), new DateOnly(y, m, d), w);

    private static UnavailableFacts Off(DateOnly from, DateOnly to, int? minutes, UnavailableReason reason, int order = 0) =>
        new(Guid.NewGuid(), from, to, minutes, reason, new DateTime(2026, 1, 1, 0, 0, order, DateTimeKind.Utc));

    private static AvailabilityDay On(IReadOnlyList<AvailabilityDay> days, int y, int m, int d) =>
        days.Single(x => x.Date == new DateOnly(y, m, d));

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>AVL-001: the pattern in effect is the latest one dated on or before the day; the first covers earlier days.</summary>
    [Fact]
    public void The_pattern_in_effect_is_the_latest_on_or_before_the_day()
    {
        PatternFacts[] patterns = [P(2026, 1, 1, FullTime), P(2026, 3, 1, MonTue)];
        var days = AvailabilityRules.Days(Person(patterns), NoHolidays, D(2025, 12, 29), D(2026, 3, 4));

        Assert.Equal(480, On(days, 2025, 12, 31).AvailableMinutes);
        Assert.Equal(480, On(days, 2026, 2, 25).AvailableMinutes);
        Assert.Equal(480, On(days, 2026, 3, 3).AvailableMinutes);
        Assert.Equal(0, On(days, 2026, 3, 4).AvailableMinutes);
        Assert.Equal(D(2026, 3, 1), AvailabilityRules.PatternOn(patterns, D(2026, 3, 4))!.EffectiveFrom);
        Assert.Equal(D(2026, 1, 1), AvailabilityRules.PatternOn(patterns, D(2025, 6, 1))!.EffectiveFrom);
        Assert.Null(AvailabilityRules.PatternOn([], D(2026, 3, 4)));
    }

    /// <summary>AVL-001: the person's own pattern decides the weekdays, so a Saturday worker has Saturday hours.</summary>
    [Fact]
    public void A_saturday_worker_has_saturday_hours()
    {
        var days = AvailabilityRules.Days(Person([P(2026, 1, 1, new WeekMinutes(0, 0, 0, 0, 0, 240, 0))]), NoHolidays, D(2026, 11, 23), D(2026, 11, 29));

        Assert.Equal(240, On(days, 2026, 11, 28).AvailableMinutes);
        Assert.Equal(0, On(days, 2026, 11, 23).AvailableMinutes);
    }

    /// <summary>
    /// AVL-001 (Review Focus 4): with the pattern in effect removed, the previous one carries on; with the earliest removed, the one
    /// left covers the days before it.
    /// </summary>
    [Fact]
    public void Removing_the_pattern_in_effect_falls_back()
    {
        var withoutLater = AvailabilityRules.Days(Person([P(2026, 1, 1, FullTime)]), NoHolidays, D(2026, 2, 23), D(2026, 3, 6));
        Assert.Equal(480, On(withoutLater, 2026, 3, 4).AvailableMinutes);

        var withoutEarlier = AvailabilityRules.Days(Person([P(2026, 3, 1, MonTue)]), NoHolidays, D(2026, 2, 23), D(2026, 3, 6));
        Assert.Equal(0, On(withoutEarlier, 2026, 2, 25).AvailableMinutes);
        Assert.Equal(480, On(withoutEarlier, 2026, 2, 24).AvailableMinutes);
    }

    /// <summary>AVL-002: a public holiday or shutdown day takes the day to nothing and is named; the day before is untouched.</summary>
    [Fact]
    public void A_holiday_is_zero_and_named()
    {
        var holidays = new Dictionary<DateOnly, string> { [D(2026, 9, 24)] = "Heritage Day" };
        var days = AvailabilityRules.Days(Person([P(2026, 1, 1, FullTime)]), holidays, D(2026, 9, 21), D(2026, 9, 25));

        var holiday = On(days, 2026, 9, 24);
        Assert.Equal((480, 0, 0, "Heritage Day"), (holiday.PatternMinutes, holiday.AvailableMinutes, holiday.UnavailableMinutes, holiday.Holiday));
        var before = On(days, 2026, 9, 23);
        Assert.Equal(480, before.AvailableMinutes);
        Assert.Null(before.Holiday);
    }

    /// <summary>
    /// AVL-003: unavailable time comes off the pattern: all day takes it all, N hours takes N, several on one day add up in the order
    /// they were recorded, and the day never goes below nothing. The parts by reason add up to what was taken.
    /// </summary>
    [Fact]
    public void Unavailable_time_is_subtracted_and_capped()
    {
        UnavailableFacts[] off =
        [
            Off(D(2026, 11, 24), D(2026, 11, 24), null, UnavailableReason.Leave),
            Off(D(2026, 11, 25), D(2026, 11, 25), 180, UnavailableReason.Sick),
            Off(D(2026, 11, 26), D(2026, 11, 26), 420, UnavailableReason.Other, order: 1),
            Off(D(2026, 11, 26), D(2026, 11, 26), 120, UnavailableReason.Training, order: 0)
        ];
        var days = AvailabilityRules.Days(Person([P(2026, 1, 1, FullTime)], off), NoHolidays, D(2026, 11, 23), D(2026, 11, 27));

        var tue = On(days, 2026, 11, 24);
        Assert.Equal((0, 480), (tue.AvailableMinutes, tue.UnavailableMinutes));
        Assert.Equal(new Dictionary<UnavailableReason, int> { [UnavailableReason.Leave] = 480 }, tue.UnavailableByReason);

        var wed = On(days, 2026, 11, 25);
        Assert.Equal((300, 180), (wed.AvailableMinutes, wed.UnavailableMinutes));
        Assert.Equal(new Dictionary<UnavailableReason, int> { [UnavailableReason.Sick] = 180 }, wed.UnavailableByReason);

        var thu = On(days, 2026, 11, 26);
        Assert.Equal((0, 480), (thu.AvailableMinutes, thu.UnavailableMinutes));
        Assert.Equal(new Dictionary<UnavailableReason, int> { [UnavailableReason.Training] = 120, [UnavailableReason.Other] = 360 }, thu.UnavailableByReason);

        Assert.Equal(480, On(days, 2026, 11, 23).AvailableMinutes);
    }

    /// <summary>AVL-003 (Review Focus 2): leave across a pattern change takes each day's own pattern hours.</summary>
    [Fact]
    public void Leave_across_a_pattern_change_takes_each_days_own_hours()
    {
        var off = Off(D(2026, 2, 27), D(2026, 3, 3), null, UnavailableReason.Leave);
        var days = AvailabilityRules.Days(Person([P(2026, 1, 1, FullTime), P(2026, 3, 1, MonTue)], [off]), NoHolidays, D(2026, 2, 27), D(2026, 3, 3));

        Assert.Equal([480, 0, 0, 480, 480], days.Select(d => d.UnavailableMinutes));
        Assert.Equal(1440, days.Sum(d => d.UnavailableMinutes));
        Assert.All(days, d => Assert.Equal(0, d.AvailableMinutes));
    }

    /// <summary>AVL-004 (Review Focus 3): days outside employment have no pattern, no availability and take nothing off an entry.</summary>
    [Fact]
    public void Days_outside_employment_have_nothing()
    {
        var off = Off(D(2026, 11, 23), D(2026, 11, 27), null, UnavailableReason.Leave);
        var person = Person([P(2026, 1, 1, FullTime)], [off], [new EmploymentPeriod(D(2026, 11, 1), D(2026, 11, 24))]);
        var days = AvailabilityRules.Days(person, NoHolidays, D(2026, 11, 23), D(2026, 11, 27));

        var last = On(days, 2026, 11, 24);
        Assert.True(last.Employed);
        Assert.Equal(480, last.UnavailableMinutes);

        var after = On(days, 2026, 11, 25);
        Assert.False(after.Employed);
        Assert.Equal((0, 0), (after.PatternMinutes, after.AvailableMinutes));
        Assert.Empty(after.Removed);
    }

    /// <summary>AVL-005: system accounts never have availability; a person with no pattern is employed with nothing to offer.</summary>
    [Fact]
    public void System_accounts_and_people_without_a_pattern_have_no_availability()
    {
        var system = AvailabilityRules.Days(Person([P(2026, 1, 1, FullTime)], system: true), NoHolidays, D(2026, 11, 23), D(2026, 11, 27));
        Assert.All(system, d => Assert.Equal((false, 0), (d.Employed, d.AvailableMinutes)));

        var none = AvailabilityRules.Days(Person([]), NoHolidays, D(2026, 11, 23), D(2026, 11, 27));
        Assert.All(none, d => Assert.Equal((true, 0, 0), (d.Employed, d.PatternMinutes, d.AvailableMinutes)));
    }

    /// <summary>AVL-001: a week's minutes by weekday, and their total.</summary>
    [Fact]
    public void A_week_reads_its_days_and_total()
    {
        var w = new WeekMinutes(1, 2, 3, 4, 5, 6, 7);
        Assert.Equal([7, 1, 2, 3, 4, 5, 6], Enum.GetValues<DayOfWeek>().Select(w.For));
        Assert.Equal(28, w.Total);
    }
}
