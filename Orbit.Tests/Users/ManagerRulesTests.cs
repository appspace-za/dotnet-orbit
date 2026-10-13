using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Users;

/// <summary>Who may be a person's or a department's manager (spec §6.5, §6.6; MGR-001 to MGR-003).</summary>
public class ManagerRulesTests
{
    /// <summary>MGR-001: a manager is an active person, never a system account.</summary>
    [Fact]
    public void A_manager_is_an_active_person()
    {
        Assert.Null(ManagerRules.Refusal(new ApplicationUser { DisplayName = "Ann" }));
        Assert.Contains("Bob", ManagerRules.Refusal(new ApplicationUser { DisplayName = "Bob", IsActive = false }));
        Assert.NotNull(ManagerRules.Refusal(new ApplicationUser { DisplayName = "Claude", IsSystemAccount = true }));

        Assert.True(ManagerRules.CanBeManager(isActive: true, isSystemAccount: false));
        Assert.False(ManagerRules.CanBeManager(isActive: false, isSystemAccount: false));
        Assert.False(ManagerRules.CanBeManager(isActive: true, isSystemAccount: true));
    }

    /// <summary>MGR-002: nobody reports to themselves - not directly, and not through the people who report to them.</summary>
    [Fact]
    public void Nobody_reports_to_themselves()
    {
        var ann = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var cat = Guid.NewGuid();
        var dan = Guid.NewGuid();
        // Cat reports to Bob, Bob to Ann; Dan reports to nobody.
        var lines = new Dictionary<Guid, Guid> { [cat] = bob, [bob] = ann };

        Assert.True(ManagerRules.ReportsInACircle(ann, ann, lines));
        Assert.True(ManagerRules.ReportsInACircle(ann, bob, lines)); // Bob reports to Ann
        Assert.True(ManagerRules.ReportsInACircle(ann, cat, lines)); // ... and so does Cat, through Bob

        Assert.False(ManagerRules.ReportsInACircle(ann, dan, lines));
        Assert.False(ManagerRules.ReportsInACircle(cat, ann, lines)); // up the line she is already on
        Assert.False(ManagerRules.ReportsInACircle(dan, cat, lines));
        Assert.False(ManagerRules.ReportsInACircle(cat, dan, lines)); // moving to another manager
    }

    /// <summary>MGR-002: a circle already in the data that doesn't pass through the person neither loops forever nor refuses their change.</summary>
    [Fact]
    public void An_old_circle_elsewhere_is_not_this_changes_doing()
    {
        var ann = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var cat = Guid.NewGuid();
        var lines = new Dictionary<Guid, Guid> { [bob] = cat, [cat] = bob };

        Assert.False(ManagerRules.ReportsInACircle(ann, bob, lines));
    }

    private static ApplicationUser User(string name, bool active = true) => new() { Id = Guid.NewGuid(), DisplayName = name, IsActive = active };

    /// <summary>
    /// MGR-003: a batch of new managers is decided against the reporting lines as its earlier changes leave them, trying the
    /// refused ones again, so two people changing places are accepted whichever comes first.
    /// </summary>
    [Fact]
    public void A_batch_is_decided_as_a_whole()
    {
        var ann = User("Ann");
        var bob = User("Bob");
        var cat = User("Cat");
        var people = new[] { ann, bob, cat }.ToDictionary(u => u.Id);
        // Bob reports to Ann. The directory now has Ann under Bob, and Bob under Cat.
        var lines = new Dictionary<Guid, Guid> { [bob.Id] = ann.Id };
        ManagerChange annUnderBob = new(ann.Id, bob.Id), bobUnderCat = new(bob.Id, cat.Id);

        Assert.All(ManagerRules.DecideAll([annUnderBob, bobUnderCat], people, lines), d => Assert.True(d.Accepted));
        Assert.All(ManagerRules.DecideAll([bobUnderCat, annUnderBob], people, lines), d => Assert.True(d.Accepted));
        Assert.Equal([ann.Id, bob.Id], ManagerRules.DecideAll([annUnderBob, bobUnderCat], people, lines).Select(d => d.UserId));

        // Alone, Ann's move would have Bob report to himself through her.
        Assert.Contains("Bob reports to Ann", Assert.Single(ManagerRules.DecideAll([annUnderBob], people, lines)).Refusal);
        Assert.Single(lines); // the lines given are not changed
    }

    /// <summary>MGR-003: each change a batch can't make says why - a circle the batch itself would close, oneself, someone who can't be a manager - and holds up no other.</summary>
    [Fact]
    public void A_batch_refuses_each_change_alone()
    {
        var ann = User("Ann");
        var bob = User("Bob");
        var cat = User("Cat");
        var dan = User("Dan", active: false);
        var eve = User("Eve");
        var people = new[] { ann, bob, cat, dan, eve }.ToDictionary(u => u.Id);
        var lines = new Dictionary<Guid, Guid> { [eve.Id] = dan.Id };

        var decisions = ManagerRules.DecideAll(
        [
            new(ann.Id, bob.Id),
            new(bob.Id, ann.Id),          // closes the circle the change before it opened
            new(cat.Id, cat.Id),
            new(cat.Id, dan.Id),
            new(cat.Id, Guid.NewGuid()),
            new(Guid.NewGuid(), ann.Id),
            new(eve.Id, dan.Id),          // the manager she has, kept without a second look
            new(cat.Id, ann.Id)
        ], people, lines);

        Assert.Null(decisions[0].Refusal);
        Assert.Contains("Ann reports to Bob", decisions[1].Refusal);
        Assert.Equal(ManagerRules.OwnManager, decisions[2].Refusal);
        Assert.Equal(ManagerRules.Refusal(dan), decisions[3].Refusal);
        Assert.Equal(ManagerRules.NoSuchManager, decisions[4].Refusal);
        Assert.Contains("no longer in Orbit", decisions[5].Refusal);
        Assert.Null(decisions[6].Refusal);
        Assert.Null(decisions[7].Refusal);
    }

    /// <summary>
    /// MGR-003: the batch tries a refused change again only when someone in the circle that refused it is given another manager,
    /// and still decides exactly as trying every refused change again after every pass would: the same changes, for the same reasons.
    /// </summary>
    [Fact]
    public void A_batch_decides_as_trying_every_refused_change_again_would()
    {
        for (var seed = 0; seed < 500; seed++)
        {
            var random = new Random(seed);
            var users = Enumerable.Range(0, random.Next(2, 9)).Select(i => User($"P{i}", active: random.Next(8) > 0)).ToList();
            if (random.Next(10) == 0) users[^1].IsSystemAccount = true;
            var people = users.ToDictionary(u => u.Id);
            // Each person under someone before them or under nobody - and now and then a circle already in the data.
            var lines = new Dictionary<Guid, Guid>();
            for (var i = 1; i < users.Count; i++)
                if (random.Next(3) > 0) lines[users[i].Id] = users[random.Next(i)].Id;
            if (random.Next(6) == 0) lines[users[0].Id] = users[^1].Id;
            // Anyone under anyone: themself, someone unknown, and the same person named more than once.
            Guid Anyone() => random.Next(12) == 0 ? Guid.NewGuid() : users[random.Next(users.Count)].Id;
            var wanted = Enumerable.Range(0, random.Next(1, 10)).Select(_ => new ManagerChange(Anyone(), Anyone())).ToList();

            Assert.True(TryingEveryRefusedChangeAgain(wanted, people, lines).SequenceEqual(ManagerRules.DecideAll(wanted, people, lines)), $"The batch of seed {seed} is decided differently.");
        }
    }

    /// <summary>The batch as first written: every refused change tried again, in list order, for as long as a pass accepts something.</summary>
    private static List<ManagerDecision> TryingEveryRefusedChangeAgain(
        IReadOnlyList<ManagerChange> wanted, IReadOnlyDictionary<Guid, ApplicationUser> people, IReadOnlyDictionary<Guid, Guid> managerOf)
    {
        var lines = new Dictionary<Guid, Guid>(managerOf);
        var refusals = new string?[wanted.Count];
        var pending = Enumerable.Range(0, wanted.Count).ToList();
        for (var accepted = true; accepted && pending.Count > 0;)
        {
            accepted = false;
            var refused = new List<int>();
            foreach (var i in pending)
            {
                var (userId, managerId) = wanted[i];
                refusals[i] = !people.TryGetValue(userId, out var user) ? "This person is no longer in Orbit."
                    : user.IsSystemAccount ? ManagerRules.SystemAccount
                    : lines.TryGetValue(userId, out var current) && current == managerId ? null
                    : !people.TryGetValue(managerId, out var manager) ? ManagerRules.NoSuchManager
                    : ManagerRules.RefusalFor(userId, user.DisplayName, manager, lines);
                if (refusals[i] is not null)
                {
                    refused.Add(i);
                    continue;
                }
                lines[userId] = managerId;
                accepted = true;
            }
            pending = refused;
        }
        return wanted.Select((w, i) => new ManagerDecision(w.UserId, w.ManagerId, refusals[i])).ToList();
    }

    /// <summary>
    /// MGR-003: a change freed by one before it in the list is tried where it stands, in the same pass - so of two changes that
    /// can't both be made, the earlier in the list is still the one made.
    /// </summary>
    [Fact]
    public void A_batch_tries_a_freed_change_where_it_stands_in_the_list()
    {
        var wes = User("Wes");
        var vic = User("Vic");
        var una = User("Una");
        var max = User("Max");
        var uri = User("Uri");
        var meg = User("Meg");
        var bea = User("Bea");
        var tom = User("Tom");
        var tess = User("Tess");
        var people = new[] { wes, vic, una, max, uri, meg, bea, tom, tess }.ToDictionary(u => u.Id);
        // An old circle in the data: Una - Bea - Uri - Wes - Una. Vic reports to Wes, Max to Uri, Meg to Una.
        var lines = new Dictionary<Guid, Guid>
        {
            [una.Id] = bea.Id, [bea.Id] = uri.Id, [uri.Id] = wes.Id, [wes.Id] = una.Id,
            [vic.Id] = wes.Id, [max.Id] = uri.Id, [meg.Id] = una.Id
        };
        ManagerChange[] wanted =
        [
            new(wes.Id, vic.Id),   // waits for Vic to move, last in the list, so is made in the second pass...
            new(una.Id, max.Id),   // ...which frees this one, tried straight after it
            new(uri.Id, meg.Id),   // so this one, free since Bea moved, would now close a circle
            new(bea.Id, tess.Id),
            new(vic.Id, tom.Id)
        ];

        var decisions = ManagerRules.DecideAll(wanted, people, lines);

        Assert.Equal(TryingEveryRefusedChangeAgain(wanted, people, lines), decisions);
        Assert.Equal([true, true, false, true, true], decisions.Select(d => d.Accepted));
        Assert.Contains("Meg reports to Uri", decisions[2].Refusal);
    }

    /// <summary>MGR-003: a long reporting line turned upside down is accepted in full, though each change waits for the one listed after it.</summary>
    [Fact]
    public void A_batch_listed_back_to_front_is_accepted_in_full()
    {
        var line = Enumerable.Range(0, 2000).Select(i => User($"P{i}")).ToList();
        var top = User("Top");
        var people = line.Append(top).ToDictionary(u => u.Id);
        // P0 reports to P1, P1 to P2, and so on up.
        var lines = Enumerable.Range(0, line.Count - 1).ToDictionary(i => line[i].Id, i => line[i + 1].Id);
        // Now each is to report to the one who reported to them, and P0 to someone new. Listed from the top of the old line down.
        var wanted = Enumerable.Range(1, line.Count - 1).Reverse().Select(i => new ManagerChange(line[i].Id, line[i - 1].Id))
            .Append(new ManagerChange(line[0].Id, top.Id)).ToList();

        Assert.All(ManagerRules.DecideAll(wanted, people, lines), d => Assert.True(d.Accepted));
    }
}
