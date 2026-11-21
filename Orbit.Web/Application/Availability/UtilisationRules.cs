using Orbit.Application.Models;

namespace Orbit.Application.Availability;

/// <summary>
/// Logged time against available time (spec §6.22, §12 Time utilisation), shared by My Time and the report. A percentage is whole,
/// rounded away from zero and never capped - someone who logged more than they were available reads over 100%.
/// </summary>
public static class UtilisationRules
{
    /// <summary>Logged as a percentage of available; null without availability, as there is nothing to measure against.</summary>
    public static int? Percent(int logged, int available) =>
        available <= 0 ? null : (int)Math.Round(logged * 100.0 / available, MidpointRounding.AwayFromZero);

    /// <summary>Below the target's share of a real availability.</summary>
    public static bool UnderTarget(int logged, int available, int targetPercent) =>
        available > 0 && logged * 100L < (long)targetPercent * available;

    /// <summary>My Time's line: <c>Logged 32h of 40h available · 80%</c>, or <c>Logged 3h · no availability in this range</c>.</summary>
    public static string MyTimeLine(int logged, int available) =>
        Percent(logged, available) is int p
            ? $"Logged {TimeFormat.Minutes(logged)} of {TimeFormat.Minutes(available)} available · {p}%"
            : $"Logged {TimeFormat.Minutes(logged)} · no availability in this range";
}
