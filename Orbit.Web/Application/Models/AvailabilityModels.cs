using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>A work pattern's minutes for each weekday (spec §6.22). The weekly total is their sum, never stored.</summary>
public sealed record WeekMinutes(int Monday, int Tuesday, int Wednesday, int Thursday, int Friday, int Saturday, int Sunday)
{
    public static readonly WeekMinutes None = new(0, 0, 0, 0, 0, 0, 0);

    public int For(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Monday,
        DayOfWeek.Tuesday => Tuesday,
        DayOfWeek.Wednesday => Wednesday,
        DayOfWeek.Thursday => Thursday,
        DayOfWeek.Friday => Friday,
        DayOfWeek.Saturday => Saturday,
        _ => Sunday
    };

    public int Total => Monday + Tuesday + Wednesday + Thursday + Friday + Saturday + Sunday;
}

/// <summary>A work pattern in effect from a date until the next one.</summary>
public sealed record PatternFacts(Guid Id, DateOnly EffectiveFrom, WeekMinutes Week);

/// <summary>A stretch of unavailable time, both days included: all day when <see cref="MinutesPerDay"/> is null.</summary>
public sealed record UnavailableFacts(Guid Id, DateOnly From, DateOnly To, int? MinutesPerDay, UnavailableReason Reason, DateTime CreatedAt);

/// <summary>Days a person's account existed and was active, both ends included; <see cref="To"/> null while it still is.</summary>
public sealed record EmploymentPeriod(DateOnly From, DateOnly? To)
{
    public bool Covers(DateOnly day) => day >= From && (To is not DateOnly to || day <= to);
}

/// <summary>A change to an account's active flag, from its audit trail: <c>Deactivated</c> is false, <c>Reactivated</c> true.</summary>
public sealed record EmploymentEvent(DateTime At, bool Active);

/// <summary>Everything the availability rule needs to know about one person.</summary>
public sealed record PersonAvailabilityFacts(
    Guid UserId, bool IsSystemAccount, IReadOnlyList<PatternFacts> Patterns, IReadOnlyList<UnavailableFacts> Unavailable,
    IReadOnlyList<EmploymentPeriod> Employment);

/// <summary>What one unavailable entry actually took off one day, after the pattern, holidays and the entries before it.</summary>
public sealed record RemovedPart(Guid EntryId, UnavailableReason Reason, int Minutes);

/// <summary>
/// One person's day (spec §6.22): whether they were employed, their pattern's minutes for the weekday, what was left available after
/// a holiday and their unavailable time, what each entry took, and the holiday's name when the organisation calendar has one.
/// </summary>
public sealed record AvailabilityDay(
    DateOnly Date, bool Employed, int PatternMinutes, int AvailableMinutes, IReadOnlyList<RemovedPart> Removed, string? Holiday)
{
    public int UnavailableMinutes => Removed.Sum(r => r.Minutes);

    public IReadOnlyDictionary<UnavailableReason, int> UnavailableByReason =>
        Removed.GroupBy(r => r.Reason).ToDictionary(g => g.Key, g => g.Sum(r => r.Minutes));
}
