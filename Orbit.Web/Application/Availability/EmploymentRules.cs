using Orbit.Application.Models;

namespace Orbit.Application.Availability;

/// <summary>
/// The days a person counts as employed for their availability (spec §6.22), rebuilt from their account's audit trail as Project status
/// rebuilds a status: from the UTC day the account was created, up to and including the day of a <c>Deactivated</c> row, and again from
/// the day of a <c>Reactivated</c> one. Pure.
/// </summary>
public static class EmploymentRules
{
    /// <summary>
    /// The periods, in date order, periods that touch or overlap merged. An account inactive now whose trail ends active (it was
    /// deactivated before deactivations were recorded) is taken as deactivated on the day it was created, so it never makes anyone
    /// look short.
    /// </summary>
    public static IReadOnlyList<EmploymentPeriod> Periods(DateTime createdAtUtc, bool isActiveNow, IEnumerable<EmploymentEvent> events)
    {
        var created = DateOnly.FromDateTime(createdAtUtc);
        var periods = new List<EmploymentPeriod>();
        DateOnly? openFrom = created;
        foreach (var e in events.OrderBy(e => e.At))
        {
            var day = DateOnly.FromDateTime(e.At);
            if (!e.Active && openFrom is DateOnly start)
            {
                periods.Add(new EmploymentPeriod(start, day < start ? start : day));
                openFrom = null;
            }
            else if (e.Active && openFrom is null)
            {
                openFrom = day < created ? created : day;
            }
        }
        if (openFrom is DateOnly open)
            periods.Add(isActiveNow || periods.Count > 0 ? new EmploymentPeriod(open, null) : new EmploymentPeriod(created, created));
        return Merge(periods);
    }

    private static List<EmploymentPeriod> Merge(List<EmploymentPeriod> periods)
    {
        var merged = new List<EmploymentPeriod>();
        foreach (var p in periods.OrderBy(p => p.From))
        {
            if (merged.Count == 0) { merged.Add(p); continue; }
            var last = merged[^1];
            if (last.To is not DateOnly lastTo) continue; // an open period already covers everything after it
            if (p.From > lastTo.AddDays(1)) { merged.Add(p); continue; }
            merged[^1] = last with { To = p.To is DateOnly to ? (to > lastTo ? to : lastTo) : null };
        }
        return merged;
    }
}
