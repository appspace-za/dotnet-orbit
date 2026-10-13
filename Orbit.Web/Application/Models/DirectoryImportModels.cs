namespace Orbit.Application.Models;

/// <summary>What "Import from directory" lists (spec §6.13): the page's query card. Blank fields take the defaults.</summary>
public sealed class DirectoryImportQuery
{
    /// <summary>A complete LDAP filter; blank means <see cref="DirectoryImportRules.DefaultListFilter"/> of the sign-in filter.</summary>
    public string? Filter { get; set; }
    /// <summary>Blank searches the directory settings' search base.</summary>
    public string? SearchBase { get; set; }
    /// <summary>The attribute holding each person's department; blank means <c>department</c>.</summary>
    public string? DepartmentAttribute { get; set; }
}

/// <summary>Whether a directory entry can be imported, and if not, why.</summary>
public enum DirectoryImportStatus
{
    Ready,
    /// <summary>An Orbit user (active or not) already has this email.</summary>
    InOrbit,
    /// <summary>The attribute people sign in with is empty or isn't an email address.</summary>
    NoEmail,
    /// <summary>Disabled in the directory: the account couldn't sign in anyway.</summary>
    Disabled,
    /// <summary>Two entries share the email, so a directory sign-in with it would be ambiguous and refused.</summary>
    DuplicateEmail
}

/// <summary>A department as the import rules see it.</summary>
public sealed record ImportDepartment(Guid Id, string Name, bool IsArchived);

/// <summary>An Orbit user as the import rules see them: enough to match a manager and to say whether theirs would change.</summary>
public sealed record ImportPerson(Guid Id, string DisplayName, Guid? ManagerId, bool IsActive, bool IsSystemAccount)
{
    /// <summary>MGR-001.</summary>
    public bool CanBeManager => ManagerRules.CanBeManager(IsActive, IsSystemAccount);
}

/// <summary>How a directory entry's manager attribute matched Orbit's users (spec §6.13.1). Only <see cref="Resolved"/> can be set.</summary>
public enum DirectoryManagerStatus
{
    /// <summary>The agent that answered is older than 1.4 and reads no managers.</summary>
    NotRead,
    /// <summary>The entry names no manager, or names itself.</summary>
    None,
    /// <summary>The manager's own entry isn't in this listing: outside the filter or the search base.</summary>
    NotListed,
    /// <summary>Listed, but nobody in Orbit has their email and they can't be imported either.</summary>
    NotInOrbit,
    /// <summary>Listed and ready to import: they become the manager when both are imported together.</summary>
    ToBeImported,
    /// <summary>In Orbit, but deactivated there (or the Claude user), so they can't be a manager.</summary>
    Deactivated,
    /// <summary>Disabled in the directory: whatever Orbit says, they are leaving or gone.</summary>
    DisabledInDirectory,
    /// <summary>An active Orbit user.</summary>
    Resolved
}

/// <summary>
/// A directory entry's manager. <see cref="Key"/> is the <see cref="DirectoryImportRow.Key"/> of the manager's own row
/// when it is listed; <see cref="UserId"/> is set only when <see cref="Status"/> is <see cref="DirectoryManagerStatus.Resolved"/>.
/// </summary>
public sealed record DirectoryManager(DirectoryManagerStatus Status, string? Name = null, string? Dn = null, string? Key = null, Guid? UserId = null)
{
    public static readonly DirectoryManager NotRead = new(DirectoryManagerStatus.NotRead);
    public static readonly DirectoryManager None = new(DirectoryManagerStatus.None);
}

public enum ManagerChangeKind
{
    /// <summary>They have no manager in Orbit.</summary>
    Set,
    /// <summary>They have another manager in Orbit.</summary>
    Change
}

/// <summary>
/// Someone already in Orbit whose manager there differs from the directory's (spec §6.13.1): one row of "Update managers".
/// </summary>
public sealed record DirectoryManagerChange(
    Guid UserId,
    string DisplayName,
    string? Email,
    Guid? CurrentManagerId,
    string? CurrentManagerName,
    /// <summary>The manager they have in Orbit can no longer be one (deactivated).</summary>
    bool CurrentManagerInactive,
    Guid ManagerId,
    string ManagerName)
{
    public ManagerChangeKind Kind => CurrentManagerId is null ? ManagerChangeKind.Set : ManagerChangeKind.Change;

    /// <summary>
    /// What a tick posts: the person and the manager shown. Applying only a pair that is still offered when the directory
    /// is read again means a tick can't make a change the admin never saw.
    /// </summary>
    public string Tick => $"{UserId:D}:{ManagerId:D}";
}

/// <summary>One directory entry on the import page, classified against Orbit's users and departments.</summary>
public sealed record DirectoryImportRow(
    string Key,
    string Dn,
    string DisplayName,
    string? Email,
    string? Account,
    string? Title,
    /// <summary>The raw value of the department attribute, as the directory holds it.</summary>
    string? DirectoryDepartment,
    DirectoryImportStatus Status,
    /// <summary>The Orbit department the directory's value matched by name; null when it couldn't be linked.</summary>
    Guid? DepartmentId,
    string? DepartmentName,
    /// <summary>The name of an archived department the value matches. Archived departments aren't linked.</summary>
    string? ArchivedMatch,
    Guid? ExistingUserId,
    /// <summary>The raw value of the manager attribute: the DN of the manager's entry. Null when there is none or none was read.</summary>
    string? ManagerDn = null,
    /// <summary>Disabled in the directory, whatever <see cref="Status"/> says: someone already in Orbit is shown as that first.</summary>
    bool DisabledInDirectory = false)
{
    public bool CanImport => Status == DirectoryImportStatus.Ready;
    public bool IsLinked => DepartmentId is not null;
    public string DepartmentKey => DirectoryImportRules.DepartmentKey(DirectoryDepartment);

    /// <summary>Their manager, once <see cref="DirectoryImportRules.ResolveManagers"/> has matched it.</summary>
    public DirectoryManager Manager { get; init; } = DirectoryManager.NotRead;
    /// <summary>For someone already in Orbit: the manager they have there, to show beside the directory's.</summary>
    public string? OrbitManagerName { get; init; }
}

/// <summary>A directory department value no Orbit department matched, with how many importable people carry it.</summary>
public sealed record UnlinkedDepartment(string Key, string Value, int Count, string? ArchivedMatch)
{
    public bool IsBlank => Key.Length == 0;
}

public sealed record DirectoryImportClassification(IReadOnlyList<DirectoryImportRow> Rows, IReadOnlyList<UnlinkedDepartment> Unlinked);

/// <summary>Which attribute supplies Orbit's email (the sign-in name), and a warning when the sign-in filter leaves that unclear.</summary>
public sealed record EmailSource(string Attribute, string? Warning);

/// <summary>What the import page shows before anything is listed.</summary>
public sealed record DirectoryImportSetup(
    bool DirectoryEnabled,
    DirectoryImportQuery Defaults,
    EmailSource EmailSource,
    int AgentsConnected,
    int AgentsAbleToList,
    /// <summary>Agents new enough (1.4) to read each person's manager.</summary>
    int AgentsAbleToReadManagers);

public sealed record DirectoryImportPreview(
    string? AgentName,
    DirectoryImportQuery Query,
    EmailSource EmailSource,
    IReadOnlyList<DirectoryImportRow> Rows,
    IReadOnlyList<UnlinkedDepartment> Unlinked,
    bool Truncated,
    /// <summary>The agent that answered read managers. When it didn't, every row's manager is "not read" and none is offered.</summary>
    bool ManagersRead,
    IReadOnlyList<DirectoryManagerChange> ManagerChanges)
{
    public int ReadyCount => Rows.Count(r => r.CanImport);
    public int InOrbitCount => Rows.Count(r => r.Status == DirectoryImportStatus.InOrbit);
    public int BlockedCount => Rows.Count(r => r.Status is not (DirectoryImportStatus.Ready or DirectoryImportStatus.InOrbit));
    public int UnlinkedCount => Unlinked.Sum(u => u.Count);
}

/// <summary>The admin's choice for one unlinked directory department value: an Orbit department, or none.</summary>
public sealed record DirectoryImportMapping(string Key, Guid? DepartmentId);

public sealed class DirectoryImportRequest
{
    public DirectoryImportQuery Query { get; set; } = new();
    /// <summary><see cref="DirectoryImportRow.Key"/>s of the people ticked.</summary>
    public IReadOnlyCollection<string> Selected { get; set; } = [];
    /// <summary>One role for everyone in this import.</summary>
    public Guid RoleId { get; set; }
    public IReadOnlyList<DirectoryImportMapping> Mappings { get; set; } = [];
}

/// <summary><see cref="ManagerNote"/> says why someone the directory names as their manager wasn't set.</summary>
public sealed record ImportedUser(Guid Id, string DisplayName, string Email, string? DepartmentName, string? ManagerName = null, string? ManagerNote = null);

public sealed record SkippedUser(string DisplayName, string? Email, string Reason);

/// <summary><see cref="Preview"/> is the list as it stands once the import is done, from the same reading of the directory.</summary>
public sealed record DirectoryImportOutcome(string RoleName, IReadOnlyList<ImportedUser> Imported, IReadOnlyList<SkippedUser> Skipped, DirectoryImportPreview Preview)
{
    public bool ManagersRead => Preview.ManagersRead;
}

/// <summary>"Update managers": the ticks of people already in Orbit, each a <see cref="DirectoryManagerChange.Tick"/>.</summary>
public sealed class DirectoryManagersRequest
{
    public DirectoryImportQuery Query { get; set; } = new();
    public IReadOnlyCollection<string> Selected { get; set; } = [];
}

public sealed record UpdatedManager(Guid UserId, string DisplayName, string? FromName, string ToName);

/// <summary><see cref="Preview"/> is the list as it stands once the managers are set, from the same reading of the directory.</summary>
public sealed record DirectoryManagersOutcome(IReadOnlyList<UpdatedManager> Updated, IReadOnlyList<SkippedUser> Skipped, DirectoryImportPreview Preview);
