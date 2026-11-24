using Orbit.Application.Models;

namespace Orbit.Application.Availability;

/// <summary>
/// The Availability page's team grid (spec §6.22): which days a week or month view covers, and what each cell says. Pure. The cells
/// may name reasons, since only the person and the people managing their availability open the grid.
/// </summary>
public static class AvailabilityGridRules
{
    /// <summary>The week (Monday to Sunday) or calendar month holding <paramref name="anchor"/>.</summary>
    public static (DateOnly From, DateOnly To) Range(AvailabilityView view, DateOnly anchor)
    {
        if (view == AvailabilityView.Month)
        {
            var first = new DateOnly(anchor.Year, anchor.Month, 1);
            return (first, first.AddMonths(1).AddDays(-1));
        }
        var monday = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
        return (monday, monday.AddDays(6));
    }

    /// <summary>The start of the period <paramref name="direction"/> periods away (-1 the one before, 1 the one after).</summary>
    public static DateOnly Step(AvailabilityView view, DateOnly from, int direction)
    {
        var start = Range(view, from).From;
        return view == AvailabilityView.Month ? start.AddMonths(direction) : start.AddDays(7 * direction);
    }

    /// <summary>A day's cell: the hours left, shaded when unavailable time reduced it, with the reasons and a line saying why.</summary>
    public static AvailabilityCell Cell(AvailabilityDay day)
    {
        if (!day.Employed) return new AvailabilityCell(day.Date, "", false, day.Holiday is not null, true, [], "Not employed");
        if (day.Holiday is not null)
            return new AvailabilityCell(day.Date, AvailabilityInputRules.FormatHours(0), false, true, false, [], $"Public holiday: {day.Holiday}");
        if (day.PatternMinutes == 0) return new AvailabilityCell(day.Date, "-", false, false, false, [], "Not a working day");

        var reasons = day.Removed.Select(r => r.Reason).Distinct().ToList();
        var parts = day.Removed.GroupBy(r => r.Reason).Select(g => $"{g.Key} {AvailabilityInputRules.FormatHours(g.Sum(r => r.Minutes))}");
        var available = AvailabilityInputRules.FormatHours(day.AvailableMinutes);
        var hover = $"Pattern {AvailabilityInputRules.FormatHours(day.PatternMinutes)} · " +
                    (day.Removed.Count == 0 ? $"{available} available" : $"{string.Join(" · ", parts)} → {available} available");
        return new AvailabilityCell(day.Date, available, day.UnavailableMinutes > 0, false, false,
            reasons.Select(AvailabilityInputRules.ShortReason).ToList(), hover);
    }
}
