using Microsoft.EntityFrameworkCore;
using Orbit.Application.Availability;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// People's availability (spec §6.22): the team grid, a person's page, their work patterns and unavailable time, and the viewer's own
/// availability for My Time. <c>availability.manage</c> decides who sees and changes what (<see cref="AccessPolicy.CanManageAvailability"/>,
/// <see cref="AccessPolicy.CanChangeWorkPattern"/>); every change is audited on the person with dates and minutes, never the reason or
/// note, so the activity log doesn't reveal them.
/// </summary>
public sealed class AvailabilityService(ApplicationDbContext db, IActorProvider actors, AuditService audit, AvailabilityFactsReader facts)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// The team grid for the week or month holding <paramref name="anchor"/>: the active people the viewer manages, by name. The
    /// department filter applies only at All scope; at Department the grid is the viewer's own department.
    /// </summary>
    public async Task<AvailabilityGrid> GridAsync(AvailabilityView view, DateOnly anchor, Guid? departmentId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(actor.Has(Permission.AvailabilityManage), "You don't have permission to manage availability.");
        var (from, to) = AvailabilityGridRules.Range(view, anchor);
        var scope = actor.ScopeOf(Permission.AvailabilityManage);

        var q = Scoping.People(db.Users.AsNoTracking(), actor, Permission.AvailabilityManage);
        if (scope == PermissionScope.All && departmentId is Guid d) q = q.Where(u => u.DepartmentId == d);
        var people = await q.OrderBy(u => u.DisplayName)
            .Select(u => new AvailabilityPerson(u.Id, u.DisplayName, u.Department == null ? null : u.Department.Name))
            .ToListAsync(ct);

        var known = await facts.ReadAsync(people.Select(p => p.Id).ToList(), from, to, ct);
        var holidays = await facts.HolidaysAsync(from, to, ct);
        var rows = people.Select(p =>
        {
            var days = AvailabilityRules.Days(known[p.Id], holidays, from, to);
            return new AvailabilityGridRow(p, days.Select(AvailabilityGridRules.Cell).ToList(), days.Sum(x => x.AvailableMinutes));
        }).ToList();
        return new AvailabilityGrid(view, from, to, rows, scope == PermissionScope.All,
            scope == PermissionScope.All ? departmentId : actor.DepartmentId, scope == PermissionScope.Own);
    }

    /// <summary>A person's page: their patterns, the unavailable time the filter picks, and what the viewer may change.</summary>
    public async Task<PersonAvailability> PersonAsync(Guid userId, UnavailableFilter filter, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var person = await RequirePersonAsync(userId, ct);
        AccessPolicy.Require(AccessPolicy.CanManageAvailability(actor, person.Id, person.DepartmentId), "You can't manage this person's availability.");

        var patterns = await db.WorkPatterns.AsNoTracking().Where(p => p.UserId == userId).OrderByDescending(p => p.EffectiveFrom).ToListAsync(ct);
        var setters = patterns.Select(p => p.CreatedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => setters.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var history = patterns.Select(p => new PatternHistoryRow(p.Id, p.EffectiveFrom, p.Week,
            p.CreatedById is Guid by && names.TryGetValue(by, out var n) ? n : "System", p.CreatedAt)).ToList();
        var current = AvailabilityRules.PatternOn(patterns.Select(p => new PatternFacts(p.Id, p.EffectiveFrom, p.Week)).ToList(), Today);

        var today = Today;
        var entries = db.UnavailableTimes.AsNoTracking().Where(u => u.UserId == userId);
        entries = filter switch
        {
            UnavailableFilter.Upcoming => entries.Where(u => u.To >= today).OrderBy(u => u.From).ThenBy(u => u.CreatedAt),
            UnavailableFilter.Past => entries.Where(u => u.To < today).OrderByDescending(u => u.From).ThenByDescending(u => u.CreatedAt),
            _ => entries.OrderByDescending(u => u.From).ThenByDescending(u => u.CreatedAt)
        };
        var listed = await entries.ToListAsync(ct);
        var removed = await RemovedAsync(userId, listed, ct);
        var rows = listed.Select(u => new UnavailableRow(u.Id, u.From, u.To, u.MinutesPerDay, u.Reason, u.Note, removed.GetValueOrDefault(u.Id))).ToList();

        return new PersonAvailability(new AvailabilityPerson(person.Id, person.DisplayName, person.Department?.Name),
            current?.Week.Total ?? 0, current?.Week, history, rows, AccessPolicy.CanChangeWorkPattern(actor, person.Id, person.DepartmentId));
    }

    /// <summary>Sets a person's pattern from a date; a pattern already on that date is replaced, so a mistake is corrected in place.</summary>
    public async Task SetPatternAsync(Guid userId, WorkPatternInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var person = await RequirePersonAsync(userId, ct);
        RequirePattern(actor, person);
        Validate(AvailabilityInputRules.CheckPattern(input.Week));

        var existing = await db.WorkPatterns.FirstOrDefaultAsync(p => p.UserId == userId && p.EffectiveFrom == input.EffectiveFrom, ct);
        var pattern = existing ?? db.WorkPatterns.Add(new WorkPattern { UserId = userId, EffectiveFrom = input.EffectiveFrom }).Entity;
        pattern.Week = input.Week;
        pattern.CreatedAt = DateTime.UtcNow;
        pattern.CreatedById = actor.UserId;
        audit.Add(actor, AuditEntity.User, person.Id, AuditAction.WorkPatternSet, person.DepartmentId, person.DisplayName,
            new { effectiveFrom = input.EffectiveFrom, weeklyMinutes = input.Week.Total, replaced = existing is not null });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a pattern; the one before carries on from its date. A person's only pattern can't be removed.</summary>
    public async Task RemovePatternAsync(Guid patternId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var pattern = await db.WorkPatterns.FirstOrDefaultAsync(p => p.Id == patternId, ct) ?? throw new NotFoundException("Work pattern not found.");
        var person = await RequirePersonAsync(pattern.UserId, ct);
        RequirePattern(actor, person);
        Validate(AvailabilityInputRules.CheckRemovePattern(await db.WorkPatterns.CountAsync(p => p.UserId == pattern.UserId, ct)));

        db.WorkPatterns.Remove(pattern);
        audit.Add(actor, AuditEntity.User, person.Id, AuditAction.WorkPatternRemoved, person.DepartmentId, person.DisplayName,
            new { effectiveFrom = pattern.EffectiveFrom, weeklyMinutes = pattern.Week.Total });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> AddUnavailableAsync(Guid userId, UnavailableInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var person = await RequirePersonAsync(userId, ct);
        RequireManage(actor, person);
        var note = Clean(input.Note);
        Validate(AvailabilityInputRules.CheckUnavailable(input.From, input.To, input.MinutesPerDay, note));

        var entry = db.UnavailableTimes.Add(new UnavailableTime
        {
            UserId = userId, From = input.From, To = input.To, MinutesPerDay = input.MinutesPerDay, Reason = input.Reason, Note = note,
            CreatedById = actor.UserId
        }).Entity;
        audit.Add(actor, AuditEntity.User, person.Id, AuditAction.UnavailableAdded, person.DepartmentId, person.DisplayName,
            new { from = input.From, to = input.To, minutesPerDay = input.MinutesPerDay });
        await db.SaveChangesAsync(ct);
        return entry.Id;
    }

    public async Task UpdateUnavailableAsync(Guid id, UnavailableInput input, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var entry = await db.UnavailableTimes.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("Unavailable time not found.");
        var person = await RequirePersonAsync(entry.UserId, ct);
        RequireManage(actor, person);
        var note = Clean(input.Note);
        Validate(AvailabilityInputRules.CheckUnavailable(input.From, input.To, input.MinutesPerDay, note));

        // The reason and note change too, but stay out of the audit details (§6.22).
        var changes = new ChangeSet()
            .Track("from", entry.From, input.From)
            .Track("to", entry.To, input.To)
            .Track("minutesPerDay", entry.MinutesPerDay, input.MinutesPerDay);
        entry.From = input.From;
        entry.To = input.To;
        entry.MinutesPerDay = input.MinutesPerDay;
        entry.Reason = input.Reason;
        entry.Note = note;
        entry.UpdatedAt = DateTime.UtcNow;
        entry.UpdatedById = actor.UserId;
        audit.Add(actor, AuditEntity.User, person.Id, AuditAction.UnavailableUpdated, person.DepartmentId, person.DisplayName, changes.Changes);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveUnavailableAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var entry = await db.UnavailableTimes.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("Unavailable time not found.");
        var person = await RequirePersonAsync(entry.UserId, ct);
        RequireManage(actor, person);

        db.UnavailableTimes.Remove(entry);
        audit.Add(actor, AuditEntity.User, person.Id, AuditAction.UnavailableRemoved, person.DepartmentId, person.DisplayName,
            new { from = entry.From, to = entry.To, minutesPerDay = entry.MinutesPerDay });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The viewer's own available minutes in the range, for My Time; null for an actor with no user. Needs no permission.</summary>
    public async Task<int?> MineAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        if (actor.UserId is not Guid me) return null;
        if (to < from) (from, to) = (to, from);
        (from, to) = TimeUtilisationRules.Clamp(from, to);
        var known = await facts.ReadAsync([me], from, to, ct);
        if (!known.TryGetValue(me, out var mine)) return null;
        return AvailabilityRules.Days(mine, await facts.HolidaysAsync(from, to, ct), from, to).Sum(d => d.AvailableMinutes);
    }

    /// <summary>Each manageable person's weekly hours in the pattern in effect today, for the Users list; people with no pattern are left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> WeeklyMinutesTodayAsync(CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var ids = Scoping.People(db.Users.AsNoTracking(), actor, Permission.AvailabilityManage).Select(u => u.Id);
        var patterns = await db.WorkPatterns.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToListAsync(ct);
        var today = Today;
        return patterns.GroupBy(p => p.UserId).ToDictionary(g => g.Key,
            g => AvailabilityRules.PatternOn(g.Select(p => new PatternFacts(p.Id, p.EffectiveFrom, p.Week)).ToList(), today)!.Week.Total);
    }

    /// <summary>What each listed entry actually took off, over its own days, after the pattern, holidays and the entries before it.</summary>
    private async Task<Dictionary<Guid, int>> RemovedAsync(Guid userId, IReadOnlyList<UnavailableTime> listed, CancellationToken ct)
    {
        if (listed.Count == 0) return [];
        var from = listed.Min(u => u.From);
        var to = listed.Max(u => u.To);
        var known = await facts.ReadAsync([userId], from, to, ct);
        var days = AvailabilityRules.Days(known[userId], await facts.HolidaysAsync(from, to, ct), from, to);
        return days.SelectMany(d => d.Removed).GroupBy(r => r.EntryId).ToDictionary(g => g.Key, g => g.Sum(r => r.Minutes));
    }

    private async Task<ApplicationUser> RequirePersonAsync(Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().Include(u => u.Department).FirstOrDefaultAsync(u => u.Id == id && !u.IsSystemAccount, ct)
        ?? throw new NotFoundException("Person not found.");

    private static void RequireManage(Actor actor, ApplicationUser person) =>
        AccessPolicy.Require(AccessPolicy.CanManageAvailability(actor, person.Id, person.DepartmentId), "You can't manage this person's availability.");

    private static void RequirePattern(Actor actor, ApplicationUser person) =>
        AccessPolicy.Require(AccessPolicy.CanChangeWorkPattern(actor, person.Id, person.DepartmentId), "You can't change this person's work pattern.");

    private static void Validate(string? problem)
    {
        if (problem is not null) throw new ValidationException(problem);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
