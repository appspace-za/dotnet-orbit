using System.Globalization;
using System.Text.RegularExpressions;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application.Availability;

/// <summary>
/// What the availability forms accept and how they write hours (spec §6.22). Pure, so the service and the tests share one copy: hours
/// in quarter hours, 0 to 24 a day in a pattern and 15 minutes to 24 hours a day of unavailable time, a span of at most 366 days and a
/// note of at most 500 characters.
/// </summary>
public static partial class AvailabilityInputRules
{
    public const int MaxSpanDays = 366, MaxNoteLength = 500, Step = 15, MaxDayMinutes = 1440;

    /// <summary>Why a stretch of unavailable time can't be saved, or null when it can.</summary>
    public static string? CheckUnavailable(DateOnly from, DateOnly to, int? minutesPerDay, string? note)
    {
        if (to < from) return "The end date can't be before the start date.";
        if (to.DayNumber - from.DayNumber > MaxSpanDays) return $"Unavailable time can cover at most {MaxSpanDays} days.";
        if (minutesPerDay is int m && (m < Step || m > MaxDayMinutes || m % Step != 0))
            return "Hours a day must be between 15 minutes and 24 hours, in quarter hours.";
        if (note?.Length > MaxNoteLength) return $"The note can't exceed {MaxNoteLength} characters.";
        return null;
    }

    /// <summary>Why a pattern can't be saved, or null when it can. A week of nothing is allowed (a sabbatical, say).</summary>
    public static string? CheckPattern(WeekMinutes week) =>
        Enum.GetValues<DayOfWeek>().Select(week.For).All(m => m is >= 0 and <= MaxDayMinutes && m % Step == 0)
            ? null
            : "Each day's hours must be between 0 and 24, in quarter hours.";

    /// <summary>Everyone keeps at least one pattern: the last one can be changed, not removed.</summary>
    public static string? CheckRemovePattern(int patternCount) =>
        patternCount <= 1 ? "A person needs a work pattern: change it instead of removing their only one." : null;

    /// <summary>
    /// Hours as typed - <c>7.5</c> or, in a comma-decimal locale, <c>7,5</c> - into minutes: 0 to 24, in quarter hours, at most two
    /// decimals (so <c>1,000</c> is refused rather than guessed at).
    /// </summary>
    public static bool TryParseHours(string? text, out int minutes)
    {
        minutes = 0;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || !HoursPattern().IsMatch(t)) return false;
        var hours = decimal.Parse(t.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        var m = hours * 60;
        if (m > MaxDayMinutes || m % Step != 0) return false;
        minutes = (int)m;
        return true;
    }

    /// <summary>Minutes as decimal hours: <c>8h</c>, <c>7.5h</c>, <c>7.25h</c>.</summary>
    public static string FormatHours(int minutes) =>
        (minutes / 60m).ToString("0.##", CultureInfo.InvariantCulture) + "h";

    /// <summary>A reason's short form for the team grid.</summary>
    public static string ShortReason(UnavailableReason reason) => reason switch
    {
        UnavailableReason.Leave => "Lve",
        UnavailableReason.Sick => "Sick",
        UnavailableReason.Training => "Trn",
        _ => "Oth"
    };

    [GeneratedRegex(@"^\d{1,2}([.,]\d{1,2})?$")]
    private static partial Regex HoursPattern();
}
