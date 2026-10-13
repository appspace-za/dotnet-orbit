using System.Text.RegularExpressions;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;

namespace Orbit.Application;

/// <summary>
/// The rules of "Import from directory" (spec §6.13), pure so they can be unit-tested: which directory entries to list
/// by default, where an imported user's email comes from, how each entry is classified against the users and
/// departments Orbit already has, and whom the directory names as each person's manager.
/// </summary>
public static partial class DirectoryImportRules
{
    public const string DefaultDepartmentAttribute = "department";
    public const int MaxFilterLength = 1000;
    public const int MaxSearchBaseLength = 500;
    private const int MaxDisplayNameLength = 200;

    public const string MailAttribute = "mail";
    public const string UpnAttribute = "userPrincipalName";
    /// <summary>
    /// Where a person's manager is read from: the DN of the manager's entry, in Active Directory and in inetOrgPerson alike.
    /// Orbit names it in each request, so reading another attribute would need no new agent.
    /// </summary>
    public const string ManagerAttribute = "manager";

    /// <summary>
    /// People (not computers or contacts) that the sign-in filter could find: the sign-in filter with its <c>{0}</c>
    /// replaced by <c>*</c>, so <c>(mail={0})</c> lists everyone with a mail attribute, and a <c>memberOf</c> restriction
    /// on sign-in restricts the list too. The admin can edit it before listing.
    /// </summary>
    public static string DefaultListFilter(string? userFilter)
    {
        var signIn = string.IsNullOrWhiteSpace(userFilter) ? "(mail={0})" : userFilter.Trim();
        return $"(&(objectCategory=person)(objectClass=user){signIn.Replace("{0}", "*", StringComparison.Ordinal)})";
    }

    /// <summary>
    /// A directory user signs in with their Orbit email, which the sign-in filter compares with some attribute. The
    /// imported email has to come from that same attribute or the person couldn't sign in: <c>userPrincipalName</c>
    /// when the filter compares that, otherwise <c>mail</c> - with a warning when the filter compares something else.
    /// </summary>
    public static EmailSource EmailSourceFor(string? userFilter)
    {
        var compared = ComparedWithSignInName().Matches(userFilter ?? string.Empty).Select(m => m.Groups[1].Value).ToList();
        if (compared.Any(a => a.Equals(MailAttribute, StringComparison.OrdinalIgnoreCase)))
            return new EmailSource(MailAttribute, null);
        if (compared.Any(a => a.Equals(UpnAttribute, StringComparison.OrdinalIgnoreCase)))
            return new EmailSource(UpnAttribute, null);
        return new EmailSource(MailAttribute,
            "The sign-in filter doesn't compare the sign-in email with mail or userPrincipalName, so Orbit can't tell which attribute people sign in with. " +
            "Imported emails are taken from mail: check a few with Test connection under Admin > Directory before relying on them.");
    }

    public static string ValidateListFilter(string? filter)
    {
        var f = filter?.Trim() ?? string.Empty;
        if (f.Length == 0) throw new ValidationException("A filter is required.");
        if (f.Length > MaxFilterLength) throw new ValidationException($"The filter must be {MaxFilterLength} characters or fewer.");
        if (!f.StartsWith('(') || !f.EndsWith(')') || !Balanced(f))
            throw new ValidationException("The filter must be an LDAP filter in balanced parentheses, e.g. (&(objectCategory=person)(objectClass=user)).");
        if (f.Contains("{0}", StringComparison.Ordinal))
            throw new ValidationException("This filter lists people, so it has no {0}: that belongs to the sign-in filter. Use * to mean \"any value\", e.g. (mail=*).");
        return f;
    }

    /// <summary>Null means "the directory settings' search base".</summary>
    public static string? ValidateSearchBase(string? searchBase)
    {
        var s = searchBase?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length > MaxSearchBaseLength) throw new ValidationException($"The search base must be {MaxSearchBaseLength} characters or fewer.");
        return s;
    }

    public static string ValidateDepartmentAttribute(string? attribute)
    {
        var a = attribute?.Trim();
        if (string.IsNullOrEmpty(a)) return DefaultDepartmentAttribute;
        if (!AttributeName().IsMatch(a))
            throw new ValidationException($"\"{a}\" isn't an LDAP attribute name. Use a name such as department, company or extensionAttribute1.");
        return a;
    }

    /// <summary>How a department name is compared: case, surrounding and repeated spaces don't matter. Blank is "no department".</summary>
    public static string DepartmentKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace().Replace(value.Trim(), " ").ToUpperInvariant();

    /// <summary>
    /// Classifies each directory entry. <paramref name="existingEmails"/> maps the email of every Orbit user, active or
    /// not, to their id, compared ignoring case. Rows come back sorted by name; the unlinked departments list the
    /// directory values of importable people that no active Orbit department matches, most people first.
    /// </summary>
    public static DirectoryImportClassification Classify(
        IEnumerable<LdapDirectoryUser> entries,
        string emailAttribute,
        IReadOnlyDictionary<string, Guid> existingEmails,
        IEnumerable<ImportDepartment> departments)
    {
        var existing = new Dictionary<string, Guid>(existingEmails, StringComparer.OrdinalIgnoreCase);
        var active = new Dictionary<string, ImportDepartment>();
        var archived = new Dictionary<string, ImportDepartment>();
        foreach (var d in departments)
            (d.IsArchived ? archived : active).TryAdd(DepartmentKey(d.Name), d);

        var list = entries.ToList();
        var useUpn = emailAttribute.Equals(UpnAttribute, StringComparison.OrdinalIgnoreCase);
        var emails = list.Select(e => ValidEmail(useUpn ? e.UserPrincipalName : e.Mail)).ToList();
        var emailCounts = emails.OfType<string>().GroupBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<DirectoryImportRow>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var email = emails[i];
            Guid? existingId = email is not null && existing.TryGetValue(email, out var id) ? id : null;
            var status = email is null ? DirectoryImportStatus.NoEmail
                : existingId is not null ? DirectoryImportStatus.InOrbit
                : e.Disabled ? DirectoryImportStatus.Disabled
                : emailCounts[email] > 1 ? DirectoryImportStatus.DuplicateEmail
                : DirectoryImportStatus.Ready;

            var key = DepartmentKey(e.Department);
            var linked = key.Length > 0 && active.TryGetValue(key, out var match) ? match : null;
            var archivedMatch = linked is null && key.Length > 0 && archived.TryGetValue(key, out var old) ? old.Name : null;

            rows.Add(new DirectoryImportRow(
                Key: string.IsNullOrEmpty(e.Id) ? e.Dn : e.Id,
                Dn: e.Dn,
                DisplayName: DisplayName(e, email),
                Email: email,
                Account: e.SamAccountName ?? e.UserPrincipalName,
                Title: e.Title,
                DirectoryDepartment: string.IsNullOrWhiteSpace(e.Department) ? null : e.Department.Trim(),
                Status: status,
                DepartmentId: linked?.Id,
                DepartmentName: linked?.Name,
                ArchivedMatch: archivedMatch,
                ExistingUserId: existingId,
                ManagerDn: string.IsNullOrWhiteSpace(e.Manager) ? null : e.Manager.Trim(),
                DisabledInDirectory: e.Disabled));
        }

        rows.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        var unlinked = rows.Where(r => r.CanImport && !r.IsLinked)
            .GroupBy(r => r.DepartmentKey)
            .Select(g => new UnlinkedDepartment(g.Key, g.First().DirectoryDepartment ?? string.Empty, g.Count(), g.First().ArchivedMatch))
            .OrderBy(u => u.IsBlank).ThenByDescending(u => u.Count).ThenBy(u => u.Value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new DirectoryImportClassification(rows, unlinked);
    }

    /// <summary>The department an imported person gets: the one their directory value matched, else the admin's mapping for that value, else none.</summary>
    public static Guid? ResolveDepartment(DirectoryImportRow row, IReadOnlyDictionary<string, Guid?> mappings) =>
        row.DepartmentId ?? mappings.GetValueOrDefault(row.DepartmentKey);

    /// <summary>
    /// Matches each entry's manager (spec §6.13.1). The directory names a manager by the DN of their own entry and Orbit
    /// joins people on email alone, so the match runs through the listing: the DN, ignoring case, to the entry listed
    /// with it, and that entry's email to an Orbit user. Someone the listing doesn't include can't be matched.
    /// <paramref name="people"/> is every Orbit user by id. Rows come back in the order given, each with its
    /// <see cref="DirectoryImportRow.Manager"/> and, for someone already in Orbit, the manager they have there.
    /// </summary>
    public static IReadOnlyList<DirectoryImportRow> ResolveManagers(
        IReadOnlyList<DirectoryImportRow> rows,
        IReadOnlyDictionary<Guid, ImportPerson> people,
        bool managersRead)
    {
        var byDn = new Dictionary<string, DirectoryImportRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) byDn.TryAdd(r.Dn.Trim(), r);

        var resolved = new List<DirectoryImportRow>(rows.Count);
        foreach (var row in rows)
        {
            var orbitManager = row.ExistingUserId is Guid uid && people.TryGetValue(uid, out var self) && self.ManagerId is Guid current
                ? people.GetValueOrDefault(current)?.DisplayName
                : null;
            resolved.Add(row with { Manager = managersRead ? ManagerOf(row, byDn, people) : DirectoryManager.NotRead, OrbitManagerName = orbitManager });
        }
        return resolved;
    }

    private static DirectoryManager ManagerOf(DirectoryImportRow row, Dictionary<string, DirectoryImportRow> byDn, IReadOnlyDictionary<Guid, ImportPerson> people)
    {
        if (row.ManagerDn is not string dn) return DirectoryManager.None;
        if (!byDn.TryGetValue(dn, out var m))
            return new DirectoryManager(DirectoryManagerStatus.NotListed, NameFromDn(dn), dn);
        // Their own entry, or a second entry of the same Orbit user: Active Directory often has the person at the top report to themself.
        if (m.Key == row.Key || (row.ExistingUserId is not null && m.ExistingUserId == row.ExistingUserId))
            return DirectoryManager.None;

        var status = m.DisabledInDirectory ? DirectoryManagerStatus.DisabledInDirectory
            : m.ExistingUserId is Guid id ? people.TryGetValue(id, out var person) && person.CanBeManager ? DirectoryManagerStatus.Resolved : DirectoryManagerStatus.Deactivated
            : m.CanImport ? DirectoryManagerStatus.ToBeImported
            : DirectoryManagerStatus.NotInOrbit;
        return new DirectoryManager(status, m.DisplayName, dn, m.Key, status == DirectoryManagerStatus.Resolved ? m.ExistingUserId : null);
    }

    /// <summary>
    /// The people already in Orbit whose manager there isn't the one the directory names: what "Update managers" offers.
    /// Only a manager matched to an active Orbit user is offered, so nothing the directory leaves blank or Orbit can't
    /// match ever clears a manager set in Orbit. Left out as well: someone deactivated in Orbit or disabled in the
    /// directory, and someone two listed entries both claim, since they can't both be right.
    /// </summary>
    public static IReadOnlyList<DirectoryManagerChange> ManagerChanges(IReadOnlyList<DirectoryImportRow> rows, IReadOnlyDictionary<Guid, ImportPerson> people)
    {
        var entriesOf = rows.Where(r => r.ExistingUserId is not null).GroupBy(r => r.ExistingUserId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var changes = new List<DirectoryManagerChange>();
        foreach (var row in rows)
        {
            if (row.Status != DirectoryImportStatus.InOrbit || row.DisabledInDirectory) continue;
            if (row.ExistingUserId is not Guid userId || entriesOf[userId] != 1) continue;
            if (!people.TryGetValue(userId, out var person) || !person.IsActive || person.IsSystemAccount) continue;
            if (row.Manager is not { Status: DirectoryManagerStatus.Resolved, UserId: Guid managerId } || managerId == person.ManagerId) continue;

            var current = person.ManagerId is Guid c ? people.GetValueOrDefault(c) : null;
            changes.Add(new DirectoryManagerChange(userId, person.DisplayName, row.Email, person.ManagerId, current?.DisplayName,
                current is { CanBeManager: false }, managerId, people.GetValueOrDefault(managerId)?.DisplayName ?? row.Manager.Name ?? string.Empty));
        }
        return changes;
    }

    /// <summary>
    /// The order to create people in: a manager before the people who report to them, so each account is created with its
    /// manager already there. A circle in the directory can't be honoured (MGR-002): whoever closes it comes first and
    /// goes without. People with no manager among <paramref name="rows"/> keep the order given.
    /// </summary>
    public static IReadOnlyList<DirectoryImportRow> ImportOrder(IReadOnlyList<DirectoryImportRow> rows)
    {
        var byKey = new Dictionary<string, DirectoryImportRow>(StringComparer.Ordinal);
        foreach (var r in rows) byKey.TryAdd(r.Key, r);

        var ordered = new List<DirectoryImportRow>(rows.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<DirectoryImportRow>();
        foreach (var start in rows)
        {
            // Walk up the reporting line until someone already placed, or met on this walk, or not being imported.
            chain.Clear();
            for (var at = start; at is not null && placed.Add(at.Key);
                 at = at.Manager is { Status: DirectoryManagerStatus.ToBeImported, Key: string key } ? byKey.GetValueOrDefault(key) : null)
                chain.Add(at);
            for (var i = chain.Count - 1; i >= 0; i--) ordered.Add(chain[i]);
        }
        return ordered;
    }

    /// <summary>
    /// The manager a person is created with, or why the one the directory names isn't set. <paramref name="created"/>
    /// maps the <see cref="DirectoryImportRow.Key"/> of everyone created so far in this import to their new id.
    /// </summary>
    public static (Guid? ManagerId, string? Note) ImportManager(DirectoryImportRow row, IReadOnlyDictionary<string, Guid> created) => row.Manager switch
    {
        { Status: DirectoryManagerStatus.Resolved, UserId: Guid id } => (id, null),
        { Status: DirectoryManagerStatus.ToBeImported, Key: string key } m => created.TryGetValue(key, out var id)
            ? (id, null)
            : (null, $"{m.Name} wasn't in Orbit yet"),
        var m => (null, ManagerNote(m))
    };

    /// <summary>
    /// Why a manager the directory names can't be set, for the page; null when it can, or there is none to explain.
    /// <paramref name="importable"/> is whether the person themself can be imported: a manager who is only ready to
    /// import is theirs if both are imported together, and for anyone else is simply not in Orbit yet.
    /// </summary>
    public static string? ManagerNote(DirectoryManager manager, bool importable = true) => manager.Status switch
    {
        DirectoryManagerStatus.NotListed => "Not in this list",
        DirectoryManagerStatus.NotInOrbit => "Not in Orbit",
        DirectoryManagerStatus.ToBeImported => importable ? "Set if imported together" : "Not in Orbit yet",
        DirectoryManagerStatus.Deactivated => "Deactivated in Orbit",
        DirectoryManagerStatus.DisabledInDirectory => "Disabled in the directory",
        _ => null
    };

    /// <summary>
    /// A name for an entry the listing doesn't include, from its DN: the value of the first part, so
    /// <c>CN=Smith\, Ann,OU=Staff,DC=corp</c> is "Smith, Ann". A DN with no such part is shown whole.
    /// </summary>
    public static string NameFromDn(string dn)
    {
        var name = new System.Text.StringBuilder();
        for (var i = dn.IndexOf('=') + 1; i < dn.Length && dn[i] != ','; i++)
        {
            if (dn[i] != '\\' || i + 1 >= dn.Length)
            {
                name.Append(dn[i]);
            }
            else if (i + 2 < dn.Length && Uri.IsHexDigit(dn[i + 1]) && Uri.IsHexDigit(dn[i + 2]))
            {
                // \2C is the same comma as \,
                name.Append((char)Convert.ToInt32(dn.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                name.Append(dn[++i]);
            }
        }
        var text = name.ToString().Trim();
        return text.Length == 0 ? dn.Trim() : text;
    }

    public static string StatusLabel(DirectoryImportStatus status) => status switch
    {
        DirectoryImportStatus.Ready => "Ready",
        DirectoryImportStatus.InOrbit => "Already in Orbit",
        DirectoryImportStatus.NoEmail => "No email in the directory",
        DirectoryImportStatus.Disabled => "Disabled in the directory",
        DirectoryImportStatus.DuplicateEmail => "Email shared with another entry",
        _ => status.ToString()
    };

    private static string? ValidEmail(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) || !v.Contains('@') || v.Length > 256 ? null : v;
    }

    private static string DisplayName(LdapDirectoryUser e, string? email)
    {
        var fullName = string.Join(' ', new[] { e.GivenName, e.Surname }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        var name = new[] { e.DisplayName, fullName, e.SamAccountName, email, e.Dn }.First(s => !string.IsNullOrWhiteSpace(s))!.Trim();
        return name.Length <= MaxDisplayNameLength ? name : name[..MaxDisplayNameLength].TrimEnd();
    }

    private static bool Balanced(string filter)
    {
        var depth = 0;
        foreach (var c in filter)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    [GeneratedRegex(@"\(\s*([A-Za-z][A-Za-z0-9-]*)\s*=\s*\{0\}\s*\)")]
    private static partial Regex ComparedWithSignInName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,63}$")]
    private static partial Regex AttributeName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
