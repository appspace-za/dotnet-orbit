using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application.Availability;

/// <summary>A new person's work pattern (spec §6.22): every way a user is created gives them this one.</summary>
public static class WorkPatterns
{
    /// <summary>The organisation's working days at <paramref name="hoursPerDay"/> hours each, in effect from <paramref name="effectiveFrom"/>.</summary>
    public static WorkPattern Default(Guid userId, IReadOnlySet<DayOfWeek> workingDays, int hoursPerDay, DateOnly effectiveFrom)
    {
        var minutes = Math.Clamp(hoursPerDay, 0, 24) * 60;
        int On(DayOfWeek d) => workingDays.Contains(d) ? minutes : 0;
        return new WorkPattern
        {
            UserId = userId,
            EffectiveFrom = effectiveFrom,
            Week = new WeekMinutes(On(DayOfWeek.Monday), On(DayOfWeek.Tuesday), On(DayOfWeek.Wednesday), On(DayOfWeek.Thursday),
                On(DayOfWeek.Friday), On(DayOfWeek.Saturday), On(DayOfWeek.Sunday))
        };
    }
}
