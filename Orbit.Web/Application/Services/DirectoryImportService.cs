using Microsoft.EntityFrameworkCore;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// "Import from directory" (spec §6.13, users.manage): lists the directory's users through an Orbit Agent, shows how
/// each matches Orbit's users and departments, and creates the people the admin ticks as directory users. Each one is
/// created by <see cref="UserAdminService.CreateAsync"/>, so every rule and the audit entry are exactly those of a user
/// created by hand. It also reads whom the directory names as each person's manager (§6.13.1): the people it creates get
/// theirs, and "Update managers" sets it for the people already in Orbit whom the admin ticks. Nothing read from the
/// directory is kept beyond the accounts created and the managers set.
/// </summary>
public sealed class DirectoryImportService(
    ApplicationDbContext db,
    IActorProvider actors,
    UserAdminService users,
    DirectoryAuthService directory,
    LdapSettingsService ldapSettings,
    AgentConnectionRegistry registry)
{
    /// <summary>The most entries one listing reads; beyond it, narrow the filter or the search base.</summary>
    public const int MaxUsers = 5000;

    public async Task<DirectoryImportSetup> GetSetupAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var settings = await db.LdapSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == WellKnownIds.LdapSettingsId, ct);
        var defaults = new DirectoryImportQuery
        {
            Filter = DirectoryImportRules.DefaultListFilter(settings?.UserFilter),
            SearchBase = settings?.SearchBase,
            DepartmentAttribute = DirectoryImportRules.DefaultDepartmentAttribute
        };
        return new DirectoryImportSetup(settings?.Enabled == true, defaults, DirectoryImportRules.EmailSourceFor(settings?.UserFilter),
            registry.WithCapability(AgentCapabilities.LdapAuthenticate).Count,
            registry.WithCapability(AgentCapabilities.LdapListUsers).Count,
            registry.WithCapability(AgentCapabilities.LdapListManagers).Count);
    }

    public async Task<DirectoryImportPreview> PreviewAsync(DirectoryImportQuery query, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        return await ClassifyAsync(await ReadDirectoryAsync(query, ct), ct);
    }

    /// <summary>
    /// Creates the ticked people who can be imported, all with one role. Refuses before creating anyone when the role
    /// needs a department and a ticked person has none; otherwise each person succeeds or fails alone, since each
    /// account is saved as it is created. A manager is created before the people who report to them, so everyone whose
    /// manager is in Orbit by then is created with them.
    /// The directory is read once, before anything can be refused: a refusal carries the list it read
    /// (<see cref="DirectoryImportRefusedException"/>) and the outcome the list as it then stands, so the page never
    /// reads the directory a second time in one request.
    /// </summary>
    public async Task<DirectoryImportOutcome> ImportAsync(DirectoryImportRequest request, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        // The directory is read again rather than trusting rows posted back by the page: what is created reflects the
        // directory and Orbit as they are now, and someone imported meanwhile shows up as already in Orbit.
        var listing = await ReadDirectoryAsync(request.Query, ct);
        var preview = await ClassifyAsync(listing, ct);
        DirectoryImportOutcome outcome;
        try
        {
            outcome = await ImportFromAsync(preview, request, ct);
        }
        catch (ValidationException ex)
        {
            // Refused before anyone was created, so the list just read still stands.
            throw new DirectoryImportRefusedException(ex.Message, preview);
        }
        return outcome with { Preview = await ClassifyAsync(listing, ct) };
    }

    private async Task<DirectoryImportOutcome> ImportFromAsync(DirectoryImportPreview preview, DirectoryImportRequest request, CancellationToken ct)
    {
        var selected = request.Selected.Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) throw new ValidationException("Tick at least one person to import.");
        var role = await RoleResolver.ForRoleAsync(db, request.RoleId, ct) ?? throw new ValidationException("Choose a role.");
        var mappings = await ValidateMappingsAsync(request.Mappings, ct);

        var chosen = preview.Rows.Where(r => selected.Contains(r.Key)).ToList();
        var ready = chosen.Where(r => r.CanImport).ToList();

        if (role.RequiresDepartment)
        {
            var homeless = ready.Count(r => DirectoryImportRules.ResolveDepartment(r, mappings) is null);
            if (homeless > 0)
                throw new ValidationException(
                    $"{People(homeless)} ticked {(homeless == 1 ? "has" : "have")} no department, and the {role.Name} role has permissions scoped to a department. " +
                    "Choose an Orbit department for their directory department, untick them, or choose another role. Nobody was imported.");
        }

        var skipped = chosen.Where(r => !r.CanImport)
            .Select(r => new SkippedUser(r.DisplayName, r.Email, DirectoryImportRules.StatusLabel(r.Status)))
            .ToList();
        var missing = selected.Count - chosen.Count;
        if (missing > 0)
            skipped.Add(new SkippedUser($"{People(missing)} ticked", null, "No longer listed by the directory with this filter."));

        Task<UserSummary> CreateAsync(DirectoryImportRow row, Guid? managerId) => users.CreateAsync(new UserInput
        {
            Email = row.Email!,
            DisplayName = row.DisplayName,
            RoleId = role.Id,
            DepartmentId = DirectoryImportRules.ResolveDepartment(row, mappings),
            ManagerId = managerId,
            AuthSource = AuthSource.Ldap,
            DirectoryDn = row.Dn
        }, ct);

        var imported = new List<ImportedUser>();
        var created = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in DirectoryImportRules.ImportOrder(ready))
        {
            var (managerId, managerNote) = DirectoryImportRules.ImportManager(row, created);
            try
            {
                UserSummary user;
                try
                {
                    user = await CreateAsync(row, managerId);
                }
                catch (ValidationException ex) when (managerId is not null)
                {
                    // The manager matched when the directory was read may have stopped being one since - deactivated a
                    // moment ago, say. The person is still created, without one (§6.13.1). If it was something else that
                    // was refused, this attempt is refused for it too and the person is skipped below.
                    user = await CreateAsync(row, null);
                    managerNote = ex.Message;
                }
                created[row.Key] = user.Id;
                imported.Add(new ImportedUser(user.Id, user.DisplayName, user.Email, user.DepartmentName, user.ManagerName, managerNote));
            }
            catch (ValidationException ex)
            {
                skipped.Add(new SkippedUser(row.DisplayName, row.Email, ex.Message));
            }
            catch (DbUpdateException)
            {
                // Typically the same email created elsewhere a moment ago. Drop the failed insert so the next person saves cleanly.
                db.ChangeTracker.Clear();
                skipped.Add(new SkippedUser(row.DisplayName, row.Email, "Couldn't be saved; a user with this email may have just been created. Load the directory again."));
            }
        }
        // Created managers-first; shown as the list was.
        imported.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return new DirectoryImportOutcome(role.Name, imported, skipped, preview);
    }

    /// <summary>
    /// "Update managers": gives the ticked people already in Orbit the manager the directory names. The directory is
    /// read again, as for an import, and a tick is the person together with the manager shown, so a change is made only
    /// if it is still the one the admin saw. Each change is decided by <see cref="UserAdminService.SetManagersAsync"/>;
    /// the accepted ones are saved together. One read of the directory, as for <see cref="ImportAsync"/>: a refusal
    /// carries the list and the outcome the list as it then stands.
    /// </summary>
    public async Task<DirectoryManagersOutcome> UpdateManagersAsync(DirectoryManagersRequest request, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var listing = await ReadDirectoryAsync(request.Query, ct);
        var preview = await ClassifyAsync(listing, ct);
        DirectoryManagersOutcome outcome;
        try
        {
            outcome = await UpdateManagersFromAsync(preview, request, ct);
        }
        catch (ValidationException ex)
        {
            // Refused before any manager was saved, so the list just read still stands.
            throw new DirectoryImportRefusedException(ex.Message, preview);
        }
        return outcome with { Preview = await ClassifyAsync(listing, ct) };
    }

    private async Task<DirectoryManagersOutcome> UpdateManagersFromAsync(DirectoryImportPreview preview, DirectoryManagersRequest request, CancellationToken ct)
    {
        var ticks = request.Selected.Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ticks.Count == 0) throw new ValidationException("Tick at least one person whose manager should change.");
        if (!preview.ManagersRead)
            throw new ValidationException($"Agent \"{preview.AgentName}\" answered this time, and it is too old to read managers (it needs version 1.4 or later). No manager was changed.");

        var chosen = preview.ManagerChanges.Where(c => ticks.Contains(c.Tick)).ToList();
        var decisions = (await users.SetManagersAsync(chosen.Select(c => new ManagerChange(c.UserId, c.ManagerId)).ToList(), UserAdminService.DirectoryImportSource, ct))
            .ToDictionary(d => d.UserId);

        var updated = new List<UpdatedManager>();
        var skipped = new List<SkippedUser>();
        foreach (var change in chosen)
        {
            if (decisions[change.UserId].Refusal is string refusal)
                skipped.Add(new SkippedUser(change.DisplayName, change.Email, refusal));
            else
                updated.Add(new UpdatedManager(change.UserId, change.DisplayName, change.CurrentManagerName, change.ManagerName));
        }
        var stale = ticks.Count - chosen.Count;
        if (stale > 0)
            skipped.Add(new SkippedUser($"{People(stale)} ticked", null, "The directory or Orbit no longer says what was shown. Load the directory again."));
        return new DirectoryManagersOutcome(updated, skipped, preview);
    }

    /// <summary>What an agent read from the directory, before any of it is matched to Orbit.</summary>
    private sealed record Listing(string? AgentName, DirectoryImportQuery Query, EmailSource EmailSource, LdapListUsersResult Result, bool ManagersRead);

    /// <summary>The one call to an agent in a request: every entry the query matches, as the directory holds it.</summary>
    private async Task<Listing> ReadDirectoryAsync(DirectoryImportQuery query, CancellationToken ct)
    {
        var connection = await ldapSettings.GetForDispatchAsync(ct)
            ?? throw new ValidationException("Directory (LDAP) sign-in isn't enabled or fully set up. Imported users sign in with it, so set it up under Admin > Directory first.");
        var normalised = new DirectoryImportQuery
        {
            Filter = DirectoryImportRules.ValidateListFilter(query.Filter),
            SearchBase = DirectoryImportRules.ValidateSearchBase(query.SearchBase),
            DepartmentAttribute = DirectoryImportRules.ValidateDepartmentAttribute(query.DepartmentAttribute)
        };

        var outcome = await directory.ListUsersAsync(new LdapListUsersRequest
        {
            Settings = connection,
            Filter = normalised.Filter,
            SearchBase = normalised.SearchBase,
            DepartmentAttribute = normalised.DepartmentAttribute,
            ManagerAttribute = DirectoryImportRules.ManagerAttribute,
            MaxResults = MaxUsers
        }, ct);
        if (outcome.Result is null)
            throw new ValidationException(outcome.Error ?? "No Orbit Agent could list the directory.");
        if (!outcome.Result.Success)
            throw new ValidationException($"Agent \"{outcome.AgentName}\" couldn't list the directory: {outcome.Result.Error}.");
        return new Listing(outcome.AgentName, normalised, DirectoryImportRules.EmailSourceFor(connection.UserFilter), outcome.Result, outcome.ManagersRead);
    }

    /// <summary>
    /// Matches a listing to Orbit's users and departments as they are now. Run again on the same listing once an import
    /// or an update is done, it gives the list as it then stands without another read of the directory.
    /// </summary>
    private async Task<DirectoryImportPreview> ClassifyAsync(Listing listing, CancellationToken ct)
    {
        var emails = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var people = new Dictionary<Guid, ImportPerson>();
        foreach (var u in await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email, u.DisplayName, u.ManagerId, u.IsActive, u.IsSystemAccount }).ToListAsync(ct))
        {
            if (u.Email is not null) emails.TryAdd(u.Email, u.Id);
            people[u.Id] = new ImportPerson(u.Id, u.DisplayName, u.ManagerId, u.IsActive, u.IsSystemAccount);
        }
        var departments = await db.Departments.AsNoTracking().Select(d => new ImportDepartment(d.Id, d.Name, d.IsArchived)).ToListAsync(ct);

        var classified = DirectoryImportRules.Classify(listing.Result.Users, listing.EmailSource.Attribute, emails, departments);
        var rows = DirectoryImportRules.ResolveManagers(classified.Rows, people, listing.ManagersRead);
        return new DirectoryImportPreview(listing.AgentName, listing.Query, listing.EmailSource, rows, classified.Unlinked, listing.Result.Truncated,
            listing.ManagersRead, DirectoryImportRules.ManagerChanges(rows, people));
    }

    /// <summary>Keyed by <see cref="DirectoryImportRules.DepartmentKey"/>; each chosen department must exist and not be archived.</summary>
    private async Task<Dictionary<string, Guid?>> ValidateMappingsAsync(IEnumerable<DirectoryImportMapping> mappings, CancellationToken ct)
    {
        var list = mappings.ToList();
        var ids = list.Where(m => m.DepartmentId is not null).Select(m => m.DepartmentId!.Value).Distinct().ToList();
        var usable = (await db.Departments.AsNoTracking().Where(d => ids.Contains(d.Id) && !d.IsArchived).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        var result = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var m in list)
        {
            if (m.DepartmentId is Guid id && !usable.Contains(id))
                throw new ValidationException("A department chosen for a directory department no longer exists or has been archived. Load the directory again.");
            result[DirectoryImportRules.DepartmentKey(m.Key)] = m.DepartmentId;
        }
        return result;
    }

    private static string People(int n) => n == 1 ? "1 person" : $"{n} people";

    private async Task<Actor> RequireAdminAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageUsers(actor), "You don't have permission to manage users.");
        return actor;
    }
}
