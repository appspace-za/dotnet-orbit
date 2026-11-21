using Microsoft.EntityFrameworkCore;
using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Data;

namespace Orbit.Application.Services;

/// <summary>
/// Reads what <see cref="AvailabilityRules"/> needs (spec §6.22) for a set of people and a range, in four queries: the people, every
/// one of their patterns (an earlier one may be the one in effect), the unavailable time overlapping the range, and the deactivations
/// and reactivations in their audit trails. No access check: the callers make it.
/// </summary>
public sealed class AvailabilityFactsReader(ApplicationDbContext db)
{
    public async Task<IReadOnlyDictionary<Guid, PersonAvailabilityFacts>> ReadAsync(
        IReadOnlyCollection<Guid> userIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, PersonAvailabilityFacts>();

        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.CreatedAt, u.IsActive, u.IsSystemAccount }).ToListAsync(ct);
        var patterns = (await db.WorkPatterns.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToListAsync(ct))
            .ToLookup(p => p.UserId, p => new PatternFacts(p.Id, p.EffectiveFrom, p.Week));
        var off = (await db.UnavailableTimes.AsNoTracking().Where(u => ids.Contains(u.UserId) && u.To >= from && u.From <= to).ToListAsync(ct))
            .ToLookup(u => u.UserId, u => new UnavailableFacts(u.Id, u.From, u.To, u.MinutesPerDay, u.Reason, u.CreatedAt));
        var events = (await db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityType == AuditEntity.User && ids.Contains(a.EntityId)
                            && (a.Action == AuditAction.Deactivated || a.Action == AuditAction.Reactivated))
                .Select(a => new { a.EntityId, a.Timestamp, a.Action }).ToListAsync(ct))
            .ToLookup(a => a.EntityId, a => new EmploymentEvent(a.Timestamp, a.Action == AuditAction.Reactivated));

        return users.ToDictionary(u => u.Id, u => new PersonAvailabilityFacts(
            u.Id, u.IsSystemAccount, patterns[u.Id].ToList(), off[u.Id].ToList(),
            EmploymentRules.Periods(u.CreatedAt, u.IsActive, events[u.Id])));
    }

    /// <summary>The organisation calendar's public holidays and shutdown days in the range, by name (§6.17).</summary>
    public async Task<IReadOnlyDictionary<DateOnly, string>> HolidaysAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await db.WorkingCalendarExceptions.AsNoTracking()
            .Where(e => !e.IsWorking && e.Date >= from && e.Date <= to)
            .ToDictionaryAsync(e => e.Date, e => e.Name, ct);
}
