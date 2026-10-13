using Orbit.Agents.Contracts;
using Orbit.Application;
using Orbit.Application.Models;

namespace Orbit.Tests.Users;

/// <summary>The rules of "Import from directory" (spec §6.13, DIM-001 to DIM-017).</summary>
public class DirectoryImportRulesTests
{
    private static readonly ImportDepartment Finance = new(Guid.NewGuid(), "Finance", false);
    private static readonly ImportDepartment SalesEmea = new(Guid.NewGuid(), "Sales EMEA", false);
    private static readonly ImportDepartment OldIt = new(Guid.NewGuid(), "IT", true);
    private static readonly ImportDepartment[] Departments = [Finance, SalesEmea, OldIt];
    private static readonly Dictionary<string, Guid> NoUsers = [];

    private static string Dn(string name) => $"CN={name},OU=Staff,DC=corp,DC=example,DC=com";

    /// <summary><paramref name="manager"/> is the name of the entry they report to, as <see cref="Dn"/> spells it.</summary>
    private static LdapDirectoryUser Entry(string name, string? mail, string? department = null, bool disabled = false, string? upn = null, string? manager = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Dn = Dn(name),
        DisplayName = name,
        Mail = mail,
        UserPrincipalName = upn,
        SamAccountName = name.Replace(" ", "").ToLowerInvariant(),
        Department = department,
        Manager = manager is null ? null : Dn(manager),
        Disabled = disabled
    };

    private static DirectoryImportClassification Classify(IReadOnlyDictionary<string, Guid>? users = null, params LdapDirectoryUser[] entries) =>
        DirectoryImportRules.Classify(entries, DirectoryImportRules.MailAttribute, users ?? NoUsers, Departments);

    /// <summary>An Orbit user whose email is their name at corp.com, as <see cref="Mail"/> spells it.</summary>
    private static ImportPerson Person(string name, ImportPerson? manager = null, bool active = true) => new(Guid.NewGuid(), name, manager?.Id, active, false);

    private static string Mail(string name) => $"{name.ToLowerInvariant()}@corp.com";

    /// <summary>What <see cref="DirectoryImportRules.ImportManager"/> answers: the manager to create someone with, or why there is none.</summary>
    private static (Guid? ManagerId, string? Note) With(Guid? managerId, string? note = null) => (managerId, note);

    /// <summary>The rows by name, classified against <paramref name="people"/> and with their managers matched.</summary>
    private static (Dictionary<string, DirectoryImportRow> Rows, IReadOnlyList<DirectoryManagerChange> Changes) Managers(
        ImportPerson[] people, bool managersRead, params LdapDirectoryUser[] entries)
    {
        var byId = people.ToDictionary(p => p.Id);
        var emails = people.ToDictionary(p => Mail(p.DisplayName), p => p.Id);
        var classified = DirectoryImportRules.Classify(entries, DirectoryImportRules.MailAttribute, emails, Departments).Rows;
        var rows = DirectoryImportRules.ResolveManagers(classified, byId, managersRead);
        return (rows.ToDictionary(r => r.DisplayName), DirectoryImportRules.ManagerChanges(rows, byId));
    }

    /// <summary>DIM-001: the default list is the sign-in filter with * for {0}, restricted to people.</summary>
    [Fact]
    public void The_default_filter_lists_whoever_the_sign_in_filter_could_find()
    {
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(mail=*))", DirectoryImportRules.DefaultListFilter("(mail={0})"));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(&(mail=*)(memberOf=CN=Orbit Users,OU=Groups,DC=corp)))",
            DirectoryImportRules.DefaultListFilter("(&(mail={0})(memberOf=CN=Orbit Users,OU=Groups,DC=corp))"));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(userPrincipalName=*))", DirectoryImportRules.DefaultListFilter(" (userPrincipalName={0}) "));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(mail=*))", DirectoryImportRules.DefaultListFilter(null));
    }

    /// <summary>DIM-002: the email comes from the attribute the sign-in filter compares, so the imported person can sign in.</summary>
    [Fact]
    public void The_email_attribute_follows_the_sign_in_filter()
    {
        Assert.Equal(new EmailSource("mail", null), DirectoryImportRules.EmailSourceFor("(mail={0})"));
        Assert.Equal(new EmailSource("mail", null), DirectoryImportRules.EmailSourceFor("(&(objectClass=user)( mail = {0} ))"));
        Assert.Equal(new EmailSource("userPrincipalName", null), DirectoryImportRules.EmailSourceFor("(userPrincipalName={0})"));
        Assert.Equal(new EmailSource("mail", null), DirectoryImportRules.EmailSourceFor("(|(userPrincipalName={0})(mail={0}))"));

        var unclear = DirectoryImportRules.EmailSourceFor("(proxyAddresses=smtp:{0})");
        Assert.Equal("mail", unclear.Attribute);
        Assert.NotNull(unclear.Warning);
    }

    /// <summary>DIM-003: the list filter is a complete filter in balanced parentheses, with no {0}.</summary>
    [Fact]
    public void The_list_filter_is_validated()
    {
        Assert.Equal("(mail=*)", DirectoryImportRules.ValidateListFilter("  (mail=*) "));
        foreach (var bad in new[] { null, "", "   ", "mail=*", "(mail=*", "(&(mail=*)))", ")(mail=*)(", "(mail={0})", "(" + new string('a', 1000) + ")" })
            Assert.Throws<ValidationException>(() => DirectoryImportRules.ValidateListFilter(bad));
    }

    /// <summary>DIM-004: the department attribute defaults to "department" and must be an attribute name; a blank search base means the settings' one.</summary>
    [Fact]
    public void The_attribute_and_search_base_are_validated()
    {
        Assert.Equal("department", DirectoryImportRules.ValidateDepartmentAttribute(null));
        Assert.Equal("department", DirectoryImportRules.ValidateDepartmentAttribute("  "));
        Assert.Equal("extensionAttribute1", DirectoryImportRules.ValidateDepartmentAttribute(" extensionAttribute1 "));
        foreach (var bad in new[] { "dept;binary", "1dept", "de pt", "(department)", "department=*" })
            Assert.Throws<ValidationException>(() => DirectoryImportRules.ValidateDepartmentAttribute(bad));

        Assert.Null(DirectoryImportRules.ValidateSearchBase("  "));
        Assert.Equal("OU=Finance,DC=corp", DirectoryImportRules.ValidateSearchBase(" OU=Finance,DC=corp "));
        Assert.Throws<ValidationException>(() => DirectoryImportRules.ValidateSearchBase(new string('a', 501)));
    }

    /// <summary>DIM-005: department names match ignoring case, surrounding and repeated spaces.</summary>
    [Fact]
    public void Department_keys_ignore_case_and_spacing()
    {
        Assert.Equal(DirectoryImportRules.DepartmentKey("Sales EMEA"), DirectoryImportRules.DepartmentKey("  sales   emea "));
        Assert.NotEqual(DirectoryImportRules.DepartmentKey("Sales EMEA"), DirectoryImportRules.DepartmentKey("Sales-EMEA"));
        Assert.Equal(string.Empty, DirectoryImportRules.DepartmentKey(null));
        Assert.Equal(string.Empty, DirectoryImportRules.DepartmentKey("   "));
    }

    /// <summary>DIM-006: a directory department links to the active Orbit department of that name; an archived one is noted, not linked.</summary>
    [Fact]
    public void Departments_link_by_name_and_archived_ones_do_not()
    {
        var result = Classify(null,
            Entry("Ann", "ann@corp.com", "finance"),
            Entry("Bob", "bob@corp.com", " Sales  Emea "),
            Entry("Cat", "cat@corp.com", "It"),
            Entry("Dan", "dan@corp.com", "Warehouse"),
            Entry("Eve", "eve@corp.com"));
        var rows = result.Rows.ToDictionary(r => r.DisplayName);

        Assert.Equal(Finance.Id, rows["Ann"].DepartmentId);
        Assert.Equal("Finance", rows["Ann"].DepartmentName);
        Assert.Equal(SalesEmea.Id, rows["Bob"].DepartmentId);
        Assert.Null(rows["Cat"].DepartmentId);
        Assert.Equal("IT", rows["Cat"].ArchivedMatch);
        Assert.False(rows["Dan"].IsLinked);
        Assert.Null(rows["Dan"].ArchivedMatch);
        Assert.False(rows["Eve"].IsLinked);
        Assert.Null(rows["Eve"].DirectoryDepartment);
        Assert.All(result.Rows, r => Assert.Equal(DirectoryImportStatus.Ready, r.Status));
    }

    /// <summary>DIM-007: why an entry can't be imported - already in Orbit (any case), no email, disabled, or an email two entries share.</summary>
    [Fact]
    public void Entries_that_cannot_be_imported_say_why()
    {
        var existingId = Guid.NewGuid();
        var users = new Dictionary<string, Guid> { ["Ann@Corp.com"] = existingId };
        var result = Classify(users,
            Entry("Ann", "ann@corp.com", disabled: true),
            Entry("Bob", null),
            Entry("Cat", "not-an-email"),
            Entry("Dan", "dan@corp.com", disabled: true),
            Entry("Eve", "shared@corp.com"),
            Entry("Fay", "SHARED@corp.com"),
            Entry("Gus", "gus@corp.com"));
        var rows = result.Rows.ToDictionary(r => r.DisplayName);

        Assert.Equal(DirectoryImportStatus.InOrbit, rows["Ann"].Status); // being in Orbit is the more useful thing to show
        Assert.Equal(existingId, rows["Ann"].ExistingUserId);
        Assert.Equal(DirectoryImportStatus.NoEmail, rows["Bob"].Status);
        Assert.Equal(DirectoryImportStatus.NoEmail, rows["Cat"].Status);
        Assert.Null(rows["Cat"].Email);
        Assert.Equal(DirectoryImportStatus.Disabled, rows["Dan"].Status);
        Assert.Equal(DirectoryImportStatus.DuplicateEmail, rows["Eve"].Status);
        Assert.Equal(DirectoryImportStatus.DuplicateEmail, rows["Fay"].Status);
        Assert.Equal(DirectoryImportStatus.Ready, rows["Gus"].Status);
        Assert.Equal(["Gus"], result.Rows.Where(r => r.CanImport).Select(r => r.DisplayName));
    }

    /// <summary>DIM-008: with a userPrincipalName sign-in filter the email is the UPN, and mail is ignored.</summary>
    [Fact]
    public void The_upn_is_the_email_when_people_sign_in_with_it()
    {
        var result = DirectoryImportRules.Classify(
            [Entry("Ann", "ann.smith@corp.com", upn: "asmith@corp.com"), Entry("Bob", "bob@corp.com")],
            DirectoryImportRules.UpnAttribute, NoUsers, Departments);
        var rows = result.Rows.ToDictionary(r => r.DisplayName);

        Assert.Equal("asmith@corp.com", rows["Ann"].Email);
        Assert.Equal(DirectoryImportStatus.NoEmail, rows["Bob"].Status);
    }

    /// <summary>DIM-009: the display name falls back from displayName to given name and surname, the account name, then the email.</summary>
    [Fact]
    public void The_display_name_falls_back_sensibly()
    {
        var entries = new[]
        {
            new LdapDirectoryUser { Id = "1", Dn = "CN=a", GivenName = "Ann", Surname = "Smith", SamAccountName = "asmith", Mail = "a@corp.com" },
            new LdapDirectoryUser { Id = "2", Dn = "CN=b", DisplayName = "  ", SamAccountName = "bjones", Mail = "b@corp.com" },
            new LdapDirectoryUser { Id = "3", Dn = "CN=c", Mail = "c@corp.com" },
            new LdapDirectoryUser { Id = "4", Dn = "CN=d", DisplayName = new string('x', 250), Mail = "d@corp.com" }
        };
        var rows = DirectoryImportRules.Classify(entries, "mail", NoUsers, Departments).Rows.ToDictionary(r => r.Key);

        Assert.Equal("Ann Smith", rows["1"].DisplayName);
        Assert.Equal("bjones", rows["2"].DisplayName);
        Assert.Equal("c@corp.com", rows["3"].DisplayName);
        Assert.Equal(200, rows["4"].DisplayName.Length);
    }

    /// <summary>DIM-010: the unlinked departments count importable people only, grouped by name, most first, "no department" last; rows sort by name.</summary>
    [Fact]
    public void Unlinked_departments_are_grouped_and_counted()
    {
        var result = Classify(null,
            Entry("Zed", "zed@corp.com", "Warehouse"),
            Entry("Amy", "amy@corp.com", "warehouse "),
            Entry("Bea", "bea@corp.com", "Legal"),
            Entry("Cy", "cy@corp.com"),
            Entry("Di", "di@corp.com", "Legal", disabled: true),
            Entry("Ed", "ed@corp.com", "Finance"));

        Assert.Equal(["Amy", "Bea", "Cy", "Di", "Ed", "Zed"], result.Rows.Select(r => r.DisplayName));
        Assert.Collection(result.Unlinked,
            u => { Assert.Equal("WAREHOUSE", u.Key); Assert.Equal(2, u.Count); },
            u => { Assert.Equal("Legal", u.Value); Assert.Equal(1, u.Count); },
            u => { Assert.True(u.IsBlank); Assert.Equal(1, u.Count); });
    }

    /// <summary>DIM-011: the department imported with is the linked one, else the admin's choice for that directory value, else none.</summary>
    [Fact]
    public void The_department_comes_from_the_link_then_the_mapping()
    {
        var rows = Classify(null,
            Entry("Ann", "ann@corp.com", "Finance"),
            Entry("Bob", "bob@corp.com", "Warehouse"),
            Entry("Cat", "cat@corp.com"),
            Entry("Dan", "dan@corp.com", "Legal")).Rows.ToDictionary(r => r.DisplayName);
        var mappings = new Dictionary<string, Guid?>
        {
            [DirectoryImportRules.DepartmentKey("Finance")] = SalesEmea.Id, // ignored: Ann is linked already
            [DirectoryImportRules.DepartmentKey("warehouse")] = Finance.Id,
            [string.Empty] = SalesEmea.Id,
            [DirectoryImportRules.DepartmentKey("Legal")] = null
        };

        Assert.Equal(Finance.Id, DirectoryImportRules.ResolveDepartment(rows["Ann"], mappings));
        Assert.Equal(Finance.Id, DirectoryImportRules.ResolveDepartment(rows["Bob"], mappings));
        Assert.Equal(SalesEmea.Id, DirectoryImportRules.ResolveDepartment(rows["Cat"], mappings));
        Assert.Null(DirectoryImportRules.ResolveDepartment(rows["Dan"], mappings));
        Assert.Null(DirectoryImportRules.ResolveDepartment(rows["Dan"], new Dictionary<string, Guid?>()));
    }

    /// <summary>DIM-012: a manager is matched through the listing - the DN, ignoring case, to the entry listed with it, and that entry's email to an active Orbit user.</summary>
    [Fact]
    public void A_manager_is_matched_through_the_listing()
    {
        var ann = Person("Ann");
        var bob = Person("Bob");
        var bobEntry = Entry("Bob", "BOB@corp.com", manager: "Ann");
        bobEntry.Manager = bobEntry.Manager!.ToLowerInvariant();
        var (rows, _) = Managers([ann, bob], true,
            Entry("Ann", Mail("Ann")),
            bobEntry,
            Entry("Cat", Mail("Cat"), manager: "Ann"));

        Assert.Equal(new DirectoryManager(DirectoryManagerStatus.Resolved, "Ann", Dn("Ann").ToLowerInvariant(), rows["Ann"].Key, ann.Id), rows["Bob"].Manager);
        // Someone not in Orbit yet is matched the same way: Cat is created with Ann as her manager.
        Assert.Equal(DirectoryImportStatus.Ready, rows["Cat"].Status);
        Assert.Equal(ann.Id, rows["Cat"].Manager.UserId);
        Assert.Equal(With(ann.Id), DirectoryImportRules.ImportManager(rows["Cat"], new Dictionary<string, Guid>()));
        Assert.Null(DirectoryImportRules.ManagerNote(rows["Cat"].Manager));
    }

    /// <summary>DIM-013: no manager, or oneself, is none; a manager who isn't listed, isn't in Orbit, is deactivated there or is disabled in the directory isn't matched, and says why.</summary>
    [Fact]
    public void A_manager_who_cannot_be_matched_says_why()
    {
        var bob = Person("Bob");
        var gus = Person("Gus", active: false);
        var ian = Person("Ian");
        var (rows, _) = Managers([bob, gus, ian], true,
            Entry("Ann", Mail("Ann")),
            Entry("Bob", Mail("Bob"), manager: "Bob"),
            Entry("Bob Admin", Mail("Bob"), manager: "Bob"), // a second account of the same person
            Entry("Cat", Mail("Cat"), manager: "Zed"),
            Entry("Dan", Mail("Dan"), manager: "Eve"),
            Entry("Eve", null),
            Entry("Fay", Mail("Fay"), manager: "Gus"),
            Entry("Gus", Mail("Gus")),
            Entry("Hal", Mail("Hal"), manager: "Ian"),
            Entry("Ian", Mail("Ian"), disabled: true),
            Entry("Jo", Mail("Jo"), manager: "Kim"),
            Entry("Kim", Mail("Kim")));

        Assert.Equal(DirectoryManager.None, rows["Ann"].Manager);
        Assert.Equal(DirectoryManager.None, rows["Bob"].Manager);
        Assert.Equal(DirectoryManager.None, rows["Bob Admin"].Manager);

        (string Name, DirectoryManagerStatus Status, string Note)[] unmatched =
        [
            ("Cat", DirectoryManagerStatus.NotListed, "Not in this list"),
            ("Dan", DirectoryManagerStatus.NotInOrbit, "Not in Orbit"),
            ("Fay", DirectoryManagerStatus.Deactivated, "Deactivated in Orbit"),
            ("Hal", DirectoryManagerStatus.DisabledInDirectory, "Disabled in the directory"),
            ("Jo", DirectoryManagerStatus.ToBeImported, "Set if imported together")
        ];
        foreach (var (name, status, note) in unmatched)
        {
            Assert.Equal(status, rows[name].Manager.Status);
            Assert.Null(rows[name].Manager.UserId);
            Assert.Equal(note, DirectoryImportRules.ManagerNote(rows[name].Manager));
        }
        Assert.Equal("Zed", rows["Cat"].Manager.Name);
        Assert.Equal(rows["Kim"].Key, rows["Jo"].Manager.Key);
        // "Imported together" is no help to someone who is in Orbit already, or can't be imported.
        Assert.Equal("Not in Orbit yet", DirectoryImportRules.ManagerNote(rows["Jo"].Manager, importable: false));
    }

    /// <summary>DIM-014: a manager is created before the people who report to them; a circle in the directory leaves whoever closes it without one.</summary>
    [Fact]
    public void Managers_are_created_before_their_reports()
    {
        var old = Person("Old");
        var (rows, _) = Managers([old], true,
            Entry("Old", Mail("Old")),
            Entry("Ann", Mail("Ann"), manager: "Zed"),
            Entry("Bob", Mail("Bob"), manager: "Ann"),
            Entry("Cy", Mail("Cy"), manager: "Di"),
            Entry("Di", Mail("Di"), manager: "Cy"),
            Entry("Eve", Mail("Eve"), manager: "Old"),
            Entry("Zed", Mail("Zed")));
        var ready = new[] { "Ann", "Bob", "Cy", "Di", "Eve", "Zed" }.Select(n => rows[n]).ToList();

        var order = DirectoryImportRules.ImportOrder(ready);
        Assert.Equal(["Zed", "Ann", "Bob", "Di", "Cy", "Eve"], order.Select(r => r.DisplayName));

        var created = new Dictionary<string, Guid>();
        var managers = new Dictionary<string, (Guid? ManagerId, string? Note)>();
        foreach (var row in order)
        {
            managers[row.DisplayName] = DirectoryImportRules.ImportManager(row, created);
            created[row.Key] = Guid.NewGuid();
        }
        Assert.Equal(With(null), managers["Zed"]);
        Assert.Equal(With(created[rows["Zed"].Key]), managers["Ann"]);
        Assert.Equal(With(created[rows["Ann"].Key]), managers["Bob"]);
        Assert.Equal(With(null, "Cy wasn't in Orbit yet"), managers["Di"]);
        Assert.Equal(With(created[rows["Di"].Key]), managers["Cy"]);
        Assert.Equal(With(old.Id), managers["Eve"]);

        // Ticked without their manager: created all the same, and told why they have none.
        Assert.Equal(With(null, "Zed wasn't in Orbit yet"),
            DirectoryImportRules.ImportManager(DirectoryImportRules.ImportOrder([rows["Ann"]]).Single(), new Dictionary<string, Guid>()));
    }

    /// <summary>
    /// DIM-015: for someone already in Orbit a manager is offered only when the one matched differs from theirs. Nothing the directory
    /// leaves blank or Orbit can't match clears a manager, and a person two entries claim, deactivated in Orbit or disabled in the
    /// directory is offered nothing.
    /// </summary>
    [Fact]
    public void A_manager_is_offered_only_where_the_directory_and_orbit_differ()
    {
        var ann = Person("Ann");
        var old = Person("Old", active: false);
        var bob = Person("Bob", ann);
        var cat = Person("Cat", ann);
        var dan = Person("Dan", old);
        var eve = Person("Eve", ann);
        var fay = Person("Fay", active: false);
        var gus = Person("Gus");
        var hal = Person("Hal");
        var ian = Person("Ian");
        var jo = Person("Jo");
        var (rows, changes) = Managers([ann, old, bob, cat, dan, eve, fay, gus, hal, ian, jo], true,
            Entry("Ann", Mail("Ann")),
            Entry("Bob", Mail("Bob"), manager: "Ann"),          // the same in both
            Entry("Cat", Mail("Cat"), manager: "Bob"),          // another in Orbit
            Entry("Dan", Mail("Dan"), manager: "Ann"),          // a deactivated one in Orbit
            Entry("Eve", Mail("Eve")),                          // blank in the directory
            Entry("Fay", Mail("Fay"), manager: "Ann"),          // deactivated in Orbit
            Entry("Gus", Mail("Gus"), manager: "Ann"),          // none in Orbit
            Entry("Hal", Mail("Hal"), manager: "Zed"),          // not matched
            Entry("Ian", Mail("Ian"), manager: "Ann"),
            Entry("Ian Admin", Mail("Ian"), manager: "Bob"),    // two entries, one Orbit user
            Entry("Jo", Mail("Jo"), disabled: true, manager: "Ann"),
            Entry("Kim", Mail("Kim"), manager: "Ann"));         // not in Orbit: that is an import

        Assert.Collection(changes,
            c =>
            {
                Assert.Equal((cat.Id, "Cat", ManagerChangeKind.Change), (c.UserId, c.DisplayName, c.Kind));
                Assert.Equal(ann.Id, c.CurrentManagerId);
                Assert.Equal(("Ann", false), (c.CurrentManagerName, c.CurrentManagerInactive));
                Assert.Equal((bob.Id, "Bob"), (c.ManagerId, c.ManagerName));
                Assert.Equal($"{cat.Id}:{bob.Id}", c.Tick);
            },
            c =>
            {
                Assert.Equal((dan.Id, ManagerChangeKind.Change, ann.Id), (c.UserId, c.Kind, c.ManagerId));
                Assert.Equal(("Old", true), (c.CurrentManagerName, c.CurrentManagerInactive));
            },
            c => Assert.Equal((gus.Id, ManagerChangeKind.Set, ann.Id), (c.UserId, c.Kind, c.ManagerId)));

        // The manager Orbit has is shown beside a blank in the directory, and kept.
        Assert.Equal(DirectoryManager.None, rows["Eve"].Manager);
        Assert.Equal("Ann", rows["Eve"].OrbitManagerName);
    }

    /// <summary>DIM-016: a manager the listing doesn't include is shown by the first name in their DN.</summary>
    [Fact]
    public void An_unlisted_manager_is_named_from_the_dn()
    {
        Assert.Equal("Zed", DirectoryImportRules.NameFromDn(Dn("Zed")));
        Assert.Equal("Smith, Ann", DirectoryImportRules.NameFromDn(@"CN=Smith\, Ann,OU=Staff,DC=corp"));
        Assert.Equal("Smith, Ann", DirectoryImportRules.NameFromDn(@"CN=Smith\2C Ann,OU=Staff,DC=corp"));
        Assert.Equal("jdoe", DirectoryImportRules.NameFromDn("uid=jdoe,ou=people,dc=example,dc=org"));
        Assert.Equal("not a dn", DirectoryImportRules.NameFromDn(" not a dn "));
    }

    /// <summary>DIM-017: when the agent read no managers, every row says so; nobody is offered one and nobody is created with one.</summary>
    [Fact]
    public void Managers_that_were_not_read_are_not_guessed()
    {
        var ann = Person("Ann");
        var bob = Person("Bob");
        var (rows, changes) = Managers([ann, bob], false,
            Entry("Ann", Mail("Ann")),
            Entry("Bob", Mail("Bob"), manager: "Ann"),
            Entry("Cat", Mail("Cat"), manager: "Ann"));

        Assert.All(rows.Values, r => Assert.Equal(DirectoryManager.NotRead, r.Manager));
        Assert.Empty(changes);
        Assert.Equal(With(null), DirectoryImportRules.ImportManager(rows["Cat"], new Dictionary<string, Guid>()));
    }
}
