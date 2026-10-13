using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// Who may be a person's or a department's manager (spec §6.5, §6.6). No database access: the services load the person and the
/// reporting lines.
/// </summary>
public static class ManagerRules
{
    /// <summary>MGR-001: a manager is an active person, never the Claude user.</summary>
    public static bool CanBeManager(bool isActive, bool isSystemAccount) => isActive && !isSystemAccount;

    /// <summary>Why this person can't be made a manager, or null when they can (<see cref="CanBeManager"/>).</summary>
    public static string? Refusal(ApplicationUser manager) =>
        CanBeManager(manager.IsActive, manager.IsSystemAccount) ? null : $"{manager.DisplayName} can't be a manager: a manager must be an active user.";

    /// <summary>
    /// Whether making <paramref name="managerId"/> the manager of <paramref name="userId"/> would have someone report to themselves:
    /// they are the same person, or the manager already reports to the user, directly or through others.
    /// <paramref name="managerOf"/> is every reporting line as it stands, each person to their manager.
    /// </summary>
    public static bool ReportsInACircle(Guid userId, Guid managerId, IReadOnlyDictionary<Guid, Guid> managerOf) =>
        Circle(userId, managerId, managerOf) is not null;

    /// <summary>
    /// The circle that making <paramref name="managerId"/> the manager of <paramref name="userId"/> would close: everyone from
    /// the manager up the reporting line to the person, who is one of them. Null when the line doesn't reach the person.
    /// </summary>
    private static HashSet<Guid>? Circle(Guid userId, Guid managerId, IReadOnlyDictionary<Guid, Guid> managerOf)
    {
        var seen = new HashSet<Guid>();
        for (Guid? at = managerId; at is Guid id; at = managerOf.TryGetValue(id, out var next) ? next : null)
        {
            // A circle that doesn't pass through this person is not this change's doing.
            if (!seen.Add(id)) return null;
            if (id == userId) return seen;
        }
        return null;
    }

    // Wordings the batch (DecideAll) shares with the user's own page, so the two can't drift.
    public const string OwnManager = "A person can't be their own manager.";
    public const string NoSuchManager = "The person chosen as manager doesn't exist.";
    public const string SystemAccount = "The system account can't be edited.";

    /// <summary>
    /// Why <paramref name="manager"/> can't be made this person's manager, or null when they can (MGR-001 and MGR-002 together):
    /// not themself, an active person, and nobody who reports to them.
    /// </summary>
    public static string? RefusalFor(Guid userId, string userName, ApplicationUser manager, IReadOnlyDictionary<Guid, Guid> managerOf) =>
        RefusalFor(userId, userName, manager, managerOf, out _);

    /// <summary><paramref name="circle"/> is the circle the change would close, when that is what refuses it.</summary>
    private static string? RefusalFor(Guid userId, string userName, ApplicationUser manager, IReadOnlyDictionary<Guid, Guid> managerOf, out HashSet<Guid>? circle)
    {
        circle = null;
        return manager.Id == userId ? OwnManager
            : Refusal(manager) is string refusal ? refusal
            : (circle = Circle(userId, manager.Id, managerOf)) is not null ? $"{manager.DisplayName} reports to {userName}, directly or through others, so can't also be their manager."
            : null;
    }

    /// <summary>
    /// Decides a batch of new managers (the directory import's "Update managers", §6.13.1). Each change is checked against the
    /// reporting lines as the changes accepted before it leave them, and one refused for a circle is tried again when someone in
    /// that circle is given another manager: a reorganisation that is sound as a whole - two people changing places, one of them
    /// moving under a third - isn't refused for the order it arrived in. <paramref name="people"/> holds everyone named, by id.
    /// </summary>
    public static IReadOnlyList<ManagerDecision> DecideAll(
        IReadOnlyList<ManagerChange> wanted,
        IReadOnlyDictionary<Guid, ApplicationUser> people,
        IReadOnlyDictionary<Guid, Guid> managerOf)
    {
        var lines = new Dictionary<Guid, Guid>(managerOf);
        var refusals = new string?[wanted.Count];
        // Each person to the refused changes whose circle they are in. Only a new manager for one of them can change such a
        // refusal, and no other refusal changes at all, so this decides exactly as trying every refused change again after
        // every pass would - in the same order: a change later in the list in the pass under way, an earlier one in the next.
        var waiting = new Dictionary<Guid, List<int>>();
        var pass = new SortedSet<int>(Enumerable.Range(0, wanted.Count));
        while (pass.Count > 0)
        {
            var next = new SortedSet<int>();
            while (pass.Count > 0)
            {
                var i = pass.Min;
                pass.Remove(i);
                var (userId, managerId) = wanted[i];
                HashSet<Guid>? circle = null;
                refusals[i] = !people.TryGetValue(userId, out var user) ? "This person is no longer in Orbit."
                    : user.IsSystemAccount ? SystemAccount
                    // A manager they already have is kept without being checked again, as on the user's own page.
                    : lines.TryGetValue(userId, out var current) && current == managerId ? null
                    : !people.TryGetValue(managerId, out var manager) ? NoSuchManager
                    : RefusalFor(userId, user.DisplayName, manager, lines, out circle);
                if (refusals[i] is not null)
                {
                    foreach (var id in circle ?? [])
                    {
                        if (!waiting.TryGetValue(id, out var held)) waiting[id] = held = [];
                        held.Add(i);
                    }
                    continue;
                }
                lines[userId] = managerId;
                if (!waiting.Remove(userId, out var freed)) continue;
                // One accepted since it was put there stays accepted.
                foreach (var j in freed.Where(j => refusals[j] is not null)) (j > i ? pass : next).Add(j);
            }
            pass = next;
        }
        return wanted.Select((w, i) => new ManagerDecision(w.UserId, w.ManagerId, refusals[i])).ToList();
    }
}
