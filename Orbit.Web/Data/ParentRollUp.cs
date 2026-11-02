using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Data;

/// <summary>
/// Keeps every parent task rolled up from its subtasks (design spec 2026-11-02 §3.3 and §3.4, spec §6.15): run inside every
/// save, before the write, so the stored values change in the same unit of work as the subtask that moved them. A parent's
/// status, priority, dates, estimate and sprint come from <see cref="ParentRollUpRules"/>; a parent keeps no assignees, day plan
/// or running clock. Each parent that changes gets an <c>Updated</c> audit row as the system, marked <c>fromSubtasks</c>. No
/// email is sent for a derived change.
/// </summary>
public static class ParentRollUp
{
    /// <summary>The deepest parent chain followed; tasks are never their own ancestor, so this only stops bad data looping.</summary>
    private const int MaxDepth = 64;

    /// <summary>The most parents one save's walk up the tree recalculates beyond those it started from.</summary>
    private const int MaxWalk = 10_000;

    /// <summary>
    /// The save hook (§3.3): recalculates the parents of the tasks this save adds, moves or changes a rolled-up field on, and any
    /// parent whose own rolled-up field it writes (put back, decision 149), then their parents while values keep changing. Called
    /// by <see cref="ApplicationDbContext"/>'s save before the write.
    /// </summary>
    public static Task ApplyAsync(ApplicationDbContext db, CancellationToken ct) =>
        DetectedOnceAsync(db, async () =>
        {
            var parents = ParentRollUpRules.AffectedParents(db.ChangeTracker.Entries<TaskItem>().Select(Change).OfType<SubtaskChange>());
            if (parents.Count == 0) return 0;
            // One parent needs no ordering; several may lie on one chain, so they are ordered by their depth in the tree.
            var depths = parents.Count == 1
                ? parents.ToDictionary(id => id, _ => 0)
                : Depths(parents, await ParentChainsAsync(db, parents, ct));
            return await RecalculateAsync(db, depths, parents.Count + MaxWalk, ct);
        });

    /// <summary>
    /// The startup repair (§3.4): recalculates every parent, deepest first, through the same rule and audit path as the save
    /// hook. Writes nothing for a parent that is already right. Returns how many parents got an audit row; the caller saves.
    /// </summary>
    public static Task<int> RecalculateAllAsync(ApplicationDbContext db, CancellationToken ct) =>
        DetectedOnceAsync(db, async () =>
        {
            // Every task some task names as parent, and any still marked as one without (drift), which only loses the mark.
            // A parent's own parent is a parent too, so the chains are complete.
            var up = await db.Tasks.AsNoTracking()
                .Where(t => t.HasSubtasks || db.Tasks.Any(c => c.ParentTaskId == t.Id))
                .Select(t => new { t.Id, t.ParentTaskId })
                .ToDictionaryAsync(t => t.Id, t => t.ParentTaskId, ct);
            return await RecalculateAsync(db, Depths(up.Keys, up), up.Count + MaxWalk, ct);
        });

    /// <summary>
    /// Detects the caller's changes once, then runs the walk with automatic detection off: it reads the tracker as the caller
    /// left it, without rescanning every tracked entity at each step, and the save detects the walk's own writes.
    /// </summary>
    private static async Task<int> DetectedOnceAsync(ApplicationDbContext db, Func<Task<int>> walk)
    {
        var tracker = db.ChangeTracker;
        tracker.DetectChanges();
        var autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            return await walk();
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private static SubtaskChange? Change(EntityEntry<TaskItem> entry) => entry.State switch
    {
        EntityState.Added => new SubtaskChange(entry.Entity.Id, true, null, entry.Entity.ParentTaskId, false, false),
        EntityState.Modified => new SubtaskChange(entry.Entity.Id, false, entry.Property(t => t.ParentTaskId).OriginalValue, entry.Entity.ParentTaskId,
            ParentRollUpRules.RollUpFields.Any(f => entry.Property(f).IsModified), entry.Entity.HasSubtasks),
        _ => null
    };

    /// <summary>Each task's parent, as this save leaves it, for the given tasks and their ancestors.</summary>
    private static async Task<Dictionary<Guid, Guid?>> ParentChainsAsync(ApplicationDbContext db, IEnumerable<Guid> start, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<TaskItem>().Where(e => e.State != EntityState.Deleted)
            .ToDictionary(e => e.Entity.Id, e => e.Entity.ParentTaskId);
        var up = new Dictionary<Guid, Guid?>();
        var level = start.ToList();
        for (var i = 0; i < MaxDepth && level.Count > 0; i++)
        {
            var unknown = new List<Guid>();
            foreach (var id in level)
            {
                if (tracked.TryGetValue(id, out var parent)) up[id] = parent;
                else unknown.Add(id);
            }
            if (unknown.Count > 0)
            {
                foreach (var row in await db.Tasks.AsNoTracking().Where(t => unknown.Contains(t.Id))
                             .Select(t => new { t.Id, t.ParentTaskId }).ToListAsync(ct))
                    up[row.Id] = row.ParentTaskId;
            }
            level = level.Where(up.ContainsKey).Select(id => up[id]).OfType<Guid>().Where(p => !up.ContainsKey(p)).Distinct().ToList();
        }
        return up;
    }

    /// <summary>How many steps each task is below the top of its tree; a parent is always shallower than its subtasks.</summary>
    private static Dictionary<Guid, int> Depths(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, Guid?> up)
    {
        var depths = new Dictionary<Guid, int>();
        foreach (var id in ids)
        {
            var depth = 0;
            for (var at = id; depth < MaxDepth && up.TryGetValue(at, out var parent) && parent is Guid next; at = next) depth++;
            depths[id] = depth;
        }
        return depths;
    }

    /// <summary>
    /// Recalculates the given parents and, while values change, their parents: deepest first, one depth at a time, so a parent
    /// is only worked out after every changed subtask below it. Parents at one depth never contain each other, so they are
    /// loaded and worked out together. Returns how many parents got an audit row.
    /// </summary>
    private static async Task<int> RecalculateAsync(ApplicationDbContext db, Dictionary<Guid, int> depths, int limit, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tracked = db.ChangeTracker.Entries<TaskItem>().Where(e => e.State != EntityState.Deleted).ToList();
        var known = tracked.ToDictionary(e => e.Entity.Id, e => e.Entity);
        // A subtask this save adds or moves in isn't under its parent in the database yet, and one it moves out still is, so a
        // parent's subtasks are the database's and these, filtered on each task's parent as it stands now.
        var saved = tracked.Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity).ToList();
        var addedAssignments = db.ChangeTracker.Entries<TaskAssignment>().Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity).ToList();

        var todo = new Dictionary<Guid, int>(depths);
        var done = new HashSet<Guid>();
        var written = 0;
        while (todo.Count > 0 && done.Count < limit)
        {
            var depth = todo.Values.Max();
            var level = todo.Where(p => p.Value == depth).Select(p => p.Key).ToList();
            foreach (var id in level) todo.Remove(id);
            level = level.Where(done.Add).ToList();
            if (level.Count == 0) continue;
            var ids = level.ToHashSet();

            var missing = level.Where(id => !known.ContainsKey(id)).ToList();
            if (missing.Count > 0)
                foreach (var task in await db.Tasks.Where(t => missing.Contains(t.Id)).ToListAsync(ct)) known[task.Id] = task;

            var children = (await db.Tasks.Where(t => t.ParentTaskId != null && level.Contains(t.ParentTaskId.Value)).ToListAsync(ct))
                .Concat(saved)
                .Where(t => t.ParentTaskId is Guid p && ids.Contains(p))
                .DistinctBy(t => t.Id)
                .ToLookup(t => t.ParentTaskId!.Value);
            foreach (var child in children.SelectMany(g => g)) known.TryAdd(child.Id, child);

            var sprintIds = children.SelectMany(g => g).Select(t => t.SprintId).OfType<Guid>().Distinct().ToList();
            var sprintStarts = sprintIds.Count == 0 ? new Dictionary<Guid, DateOnly>()
                : await db.Sprints.AsNoTracking().Where(s => sprintIds.Contains(s.Id))
                    .Select(s => new { s.Id, s.StartDate }).ToDictionaryAsync(s => s.Id, s => s.StartDate, ct);

            // A container keeps no assignees and runs no clock (§2, §3.3): what any of them still has is removed below.
            var containers = level.Where(children.Contains).ToList();
            var assignmentRows = containers.Count == 0 ? []
                : await db.TaskAssignments.Where(a => containers.Contains(a.TaskId)).ToListAsync(ct);
            var assignments = assignmentRows.Concat(addedAssignments)
                .Where(a => ids.Contains(a.TaskId) && db.Entry(a).State != EntityState.Deleted)
                .Distinct()
                .ToLookup(a => a.TaskId);
            var clockRows = containers.Count == 0 ? []
                : await db.RunningClocks.Where(c => containers.Contains(c.TaskId)).ToListAsync(ct);
            // A clock already stopped in this unit of work (the start-up step, whose save runs this hook again) is still in the
            // database and comes back as its tracked, deleted instance: its time is logged already.
            var clocks = clockRows.Where(c => db.Entry(c).State != EntityState.Deleted).ToLookup(c => c.TaskId);

            foreach (var id in level)
            {
                if (!known.TryGetValue(id, out var parent)) continue;
                var subtasks = children[id].ToList();
                if (subtasks.Count == 0)
                {
                    if (LoseSubtasks(db, parent, now)) written++;
                    continue;
                }
                if (Roll(db, parent, subtasks, sprintStarts, assignments[id].ToList(), clocks[id].ToList(), now, out var valuesChanged))
                    written++;
                // Its own parent rolls up these values, so it is next, one level up, unless already worked out.
                if (valuesChanged && parent.ParentTaskId is Guid up && !done.Contains(up)) todo.TryAdd(up, depth - 1);
            }
        }
        return written;
    }

    /// <summary>A task whose last subtask moved off stops being a parent; the values it had are kept (§3.3).</summary>
    private static bool LoseSubtasks(ApplicationDbContext db, TaskItem parent, DateTime now)
    {
        if (!parent.HasSubtasks) return false;
        var changes = new ChangeSet().Track("hasSubtasks", true, false);
        parent.HasSubtasks = false;
        parent.UpdatedAt = now;
        AuditUpdated(db, parent, changes);
        return true;
    }

    /// <summary>
    /// Writes the rolled-up values onto a parent and removes what a container can't keep: its assignees, its day plan and any
    /// running clock, whose time is logged as the clock's own stop logs it (SUB-024). True when the parent got an audit row;
    /// <paramref name="valuesChanged"/> says whether a value its own parent rolls up changed.
    /// </summary>
    private static bool Roll(ApplicationDbContext db, TaskItem parent, List<TaskItem> subtasks, IReadOnlyDictionary<Guid, DateOnly> sprintStarts,
        List<TaskAssignment> assignments, List<RunningClock> clocks, DateTime now, out bool valuesChanged)
    {
        var values = ParentRollUpRules.Of(subtasks.Select(t => new SubtaskFacts(t.Status, t.Priority, t.StartDate, t.DueDate,
            t.EstimateMinutes, t.SprintId, t.SprintId is Guid s && sprintStarts.TryGetValue(s, out var start) ? start : (DateOnly?)null,
            t.InBacklog)).ToList());

        var changes = new ChangeSet()
            .Track("status", parent.Status, values.Status)
            .Track("priority", parent.Priority, values.Priority)
            .Track("startDate", parent.StartDate, values.StartDate)
            .Track("dueDate", parent.DueDate, values.DueDate)
            .Track("estimateMinutes", parent.EstimateMinutes, values.EstimateMinutes)
            .Track("sprintId", parent.SprintId, values.Sprint.SprintId)
            .Track("inBacklog", parent.InBacklog, values.Sprint.IsBacklog);
        valuesChanged = changes.HasChanges;
        changes.Track("hasSubtasks", parent.HasSubtasks, true);

        if (parent.Status != values.Status) TaskStatusStamps.Apply(parent, values.Status, now);
        parent.Priority = values.Priority;
        parent.StartDate = values.StartDate;
        parent.DueDate = values.DueDate;
        parent.EstimateMinutes = values.EstimateMinutes;
        values.Sprint.ApplyTo(parent);
        parent.HasSubtasks = true;

        if (assignments.Count > 0)
        {
            changes.Track<IReadOnlyList<Guid>>("assigneeIds", assignments.Select(a => a.UserId).Order().ToList(), []);
            db.TaskAssignments.RemoveRange(assignments);
        }
        if (parent.PlannedFor is not null)
        {
            changes.Track("plannedFor", parent.PlannedFor, null);
            parent.PlannedFor = null;
        }
        foreach (var clock in clocks) StopClock(db, parent, clock, now);

        if (changes.HasChanges)
        {
            parent.UpdatedAt = now;
            AuditUpdated(db, parent, changes);
        }
        return changes.HasChanges || clocks.Count > 0;
    }

    /// <summary>Stops a clock running on a task that became a parent, logging its time and the stop as <c>TimeEntryService</c> does.</summary>
    private static void StopClock(ApplicationDbContext db, TaskItem parent, RunningClock clock, DateTime now)
    {
        db.RunningClocks.Remove(clock);
        var entry = TimeRules.StoppedClockEntry(clock, now);
        if (entry is not null)
        {
            db.TimeEntries.Add(entry);
            db.AuditLogs.Add(AuditService.Row(Actor.System, AuditEntity.Task, parent.Id, AuditAction.TimeLogged, parent.DepartmentId,
                parent.Title, new { timeEntryId = entry.Id, entry.UserId, entry.Date, entry.DurationMinutes, clock = true }));
        }
        var startedAt = DateTime.SpecifyKind(clock.StartedAt, DateTimeKind.Utc);
        var lastSeenAt = DateTime.SpecifyKind(clock.LastSeenAt, DateTimeKind.Utc);
        db.AuditLogs.Add(AuditService.Row(Actor.System, AuditEntity.Task, parent.Id, AuditAction.ClockStopped, parent.DepartmentId,
            parent.Title, new
            {
                startedAt, stoppedAt = TimeRules.ClockStoppedAt(lastSeenAt, now), minutes = entry?.DurationMinutes ?? 0,
                timeEntryId = entry?.Id, stale = TimeRules.IsClockStale(lastSeenAt, now), fromSubtasks = true
            }));
    }

    private static void AuditUpdated(ApplicationDbContext db, TaskItem parent, ChangeSet changes) =>
        db.AuditLogs.Add(AuditService.Row(Actor.System, AuditEntity.Task, parent.Id, AuditAction.Updated, parent.DepartmentId,
            parent.Title, new Dictionary<string, object?>(changes.Changes) { ["fromSubtasks"] = true }));
}
