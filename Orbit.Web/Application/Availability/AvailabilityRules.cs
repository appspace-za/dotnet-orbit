using Orbit.Application.Models;

namespace Orbit.Application.Availability;

/// <summary>
/// A person's available time, day by day (spec §6.22). Pure, so it is unit-tested; the team grid, the person page, My Time and the
/// Time utilisation report all read it. For each day, in order:
/// <list type="number">
/// <item><b>Employment:</b> nothing outside the periods the account existed and was active (<see cref="EmploymentRules"/>).</item>
/// <item><b>Pattern:</b> the minutes for the weekday of the pattern in effect - the latest dated on or before the day, or the
/// person's first when the day is earlier than all of them. The organisation's working week plays no part.</item>
/// <item><b>Holiday:</b> a public holiday or shutdown day in the organisation calendar leaves nothing available.</item>
/// <item><b>Unavailable time:</b> each entry covering the day, in the order they were recorded, takes all of what is left (all day) or
/// up to its hours, never below nothing.</item>
/// </list>
/// System accounts (the Claude user) never have availability.
/// </summary>
public static class AvailabilityRules
{
    /// <summary>One <see cref="AvailabilityDay"/> per date from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    /// <param name="holidays">The organisation calendar's non-working exceptions in or around the range, by name.</param>
    public static IReadOnlyList<AvailabilityDay> Days(
        PersonAvailabilityFacts person, IReadOnlyDictionary<DateOnly, string> holidays, DateOnly from, DateOnly to)
    {
        var patterns = person.Patterns.OrderBy(p => p.EffectiveFrom).ToList();
        var entries = person.Unavailable.Where(u => u.To >= from && u.From <= to).OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).ToList();
        var days = new List<AvailabilityDay>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var holiday = holidays.TryGetValue(day, out var name) ? name : null;
            var employed = !person.IsSystemAccount && person.Employment.Any(e => e.Covers(day));
            if (!employed)
            {
                days.Add(new AvailabilityDay(day, false, 0, 0, [], holiday));
                continue;
            }
            var pattern = Sorted(patterns, day)?.Week.For(day.DayOfWeek) ?? 0;
            if (holiday is not null)
            {
                days.Add(new AvailabilityDay(day, true, pattern, 0, [], holiday));
                continue;
            }
            var left = pattern;
            var removed = new List<RemovedPart>();
            foreach (var entry in entries.Where(e => e.From <= day && e.To >= day))
            {
                var take = Math.Min(left, entry.MinutesPerDay ?? left);
                if (take <= 0) continue;
                removed.Add(new RemovedPart(entry.Id, entry.Reason, take));
                left -= take;
            }
            days.Add(new AvailabilityDay(day, true, pattern, left, removed, null));
        }
        return days;
    }

    /// <summary>The pattern in effect on <paramref name="day"/>: the latest dated on or before it, else the earliest; null with none.</summary>
    public static PatternFacts? PatternOn(IReadOnlyList<PatternFacts> patterns, DateOnly day) =>
        Sorted(patterns.OrderBy(p => p.EffectiveFrom).ToList(), day);

    private static PatternFacts? Sorted(List<PatternFacts> byDate, DateOnly day) =>
        byDate.Count == 0 ? null : byDate.LastOrDefault(p => p.EffectiveFrom <= day) ?? byDate[0];
}
