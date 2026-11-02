# Parent Tasks as Containers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A task with subtasks becomes a container whose status, priority, dates, estimate and sprint are rolled up from its
subtasks; the Tasks and project pages show the tree; every count and estimate sum leaves parents out.

**Architecture:** The rolled-up values are stored in the parent's existing columns, recalculated by a hook in
`ApplicationDbContext.SaveChangesAsync` that calls pure rules (`ParentRollUpRules`). A new `HasSubtasks` column marks
parents; `WorkOnly()` filters them out of counts. A pure `TaskTreeRules` orders rows as a tree for the Gantt and the two list
pages; `ParentDisplayRules` works out the display-only values (status chips, ranges, assignee names).

**Tech Stack:** .NET 10, ASP.NET Core Blazor Server + MudBlazor, EF Core + PostgreSQL (Npgsql), xunit.

**Spec:** `docs/superpowers/specs/2026-11-02-parent-task-rollup-design.md` (read it first; section numbers below like
"spec §3.2" refer to it, while "orbit-spec §6.15" refers to `orbit-spec.md`).

## Global Constraints

- Namespaces start `Orbit.` (not `Orbit.Web.`): `Orbit.Application`, `Orbit.Application.Services`, `Orbit.Data`, `Orbit.Components.Tasks`.
- Pure rules have no DB access and live in `Orbit.Web/Application/`; tests are xunit in `Orbit.Tests`, pure code only, and
  cite acceptance IDs (`SUB-001` …) in `/// <summary>` doc comments, as `Orbit.Tests/Tasks/SolutionNoteRulesTests.cs` does.
- Blazor components never inject a service; they call services inside `Load(async s => …)` / `Act(async s => …)`.
- Enums are stored as names: raw SQL uses `'Todo'`, not `0`.
- British spelling in prose and identifiers. Keep each file's existing line endings (`git ls-files --eol <file>`).
- If an `Orbit.Web` process is running, build and test with `-c Release`.
- Every task that changes behaviour updates `orbit-spec.md` in the same commit: the functional section, a numbered
  §13 Decisions Log entry (next free number is **145**) and a §14 Suggested Build Order step (next free number is **129**).
  Each task below says which.
- Derived-field refusal message, exactly: `"{Number} has subtasks; its {field} comes from them."`
  (e.g. `T-26-00012 has subtasks; its due date comes from them.`).

## Review Focus

1. **A subtask moved from one parent to another in one save.** Both parents must be recalculated, and the old one loses
   `HasSubtasks` if that was its last subtask. Pinned by `AffectedParents_includes_old_and_new_parent` (Task 2).
2. **A filter that matches only a parent** (a search on its title). The parent must show as a match, not as a greyed context
   row, with none of its subtasks, and the work count stays 0. Pinned by `A_parent_that_matches_is_not_context` (Task 7).
3. **A collapsed parent that holds every match.** The rows under it hide, and nothing else disappears. Pinned by
   `Visible_hides_everything_under_a_collapsed_parent` (Task 7).
4. **Corrupt data with a loop in the parent chain** (A's parent is B, B's parent is A). The tree must not recurse forever,
   and both tasks must still appear. Pinned by `A_parent_loop_still_lists_every_task_once` (Task 7). The save hook guards
   the same way with its visited set (Task 2).
5. **A clock running on a task when it gets its first subtask.** The clock must stop and its time be logged, not lost.
   Pinned by `StoppedClockEntry_logs_the_running_time` (Task 2, SUB-024), plus a manual check.

---

## File map

| File | Responsibility | Task |
|---|---|---|
| `Orbit.Web/Application/ParentRollUpRules.cs` (new) | Stored roll-up rules, affected parents, finish check | 1, 2, 3 |
| `Orbit.Web/Application/TaskStatusStamps.cs` (new) | `CompletedAt` / `FirstRespondedAt` stamping, moved out of `TaskService` | 1 |
| `Orbit.Web/Data/ParentRollUp.cs` (new) | The save hook's DB work and the startup recalculation | 2 |
| `Orbit.Web/Application/ParentRules.cs` (new) | Derived-field refusals and messages | 3 |
| `Orbit.Web/Application/TaskQueries.cs` (new) | `WorkOnly()` | 6 |
| `Orbit.Web/Application/TaskTreeRules.cs` (new) | Tree order, paging by group, visibility under collapse | 7 |
| `Orbit.Web/Application/ParentDisplayRules.cs` (new) | Status chips, due and sprint ranges, assignee names | 7 |
| `Orbit.Web/Application/Models/TaskTreeModels.cs` (new) | `TaskTreeKey`, `TaskTreeRow`, `TaskTreePage` | 7, 8 |
| `Orbit.Web/Components/Infrastructure/BrowserTreeMemory.cs` (new) | Collapsed ids in `localStorage` | 9 |
| `Orbit.Web/Components/Tasks/ParentStatusChips.razor`, `ParentDueRange.razor`, `ParentSprintRange.razor` (new) | Parent row cells | 9 |
| Existing files | Named per task | — |

---

### Task 1: Roll-up rules

**Files:**
- Create: `Orbit.Web/Application/ParentRollUpRules.cs`
- Create: `Orbit.Web/Application/TaskStatusStamps.cs`
- Modify: `Orbit.Web/Application/Services/TaskService.cs:752-758` (`ApplyStatus` moves out)
- Test: `Orbit.Tests/Tasks/ParentRollUpRulesTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace Orbit.Application;
  public sealed record SubtaskFacts(TaskItemStatus Status, TaskPriority Priority, DateOnly? StartDate, DateOnly? DueDate,
      int? EstimateMinutes, Guid? SprintId, DateOnly? SprintStart, bool InBacklog);
  public sealed record RolledUpValues(TaskItemStatus Status, TaskPriority Priority, DateOnly? StartDate, DateOnly? DueDate,
      int? EstimateMinutes, SprintPlacement Sprint);
  public static class ParentRollUpRules
  {
      public static TaskItemStatus Status(IEnumerable<TaskItemStatus> subtasks);   // ArgumentException when empty
      public static RolledUpValues Of(IReadOnlyCollection<SubtaskFacts> subtasks); // ArgumentException when empty
  }
  public static class TaskStatusStamps { public static void Apply(TaskItem task, TaskItemStatus status, DateTime now); }
  ```

- [ ] **Step 1: Write the failing tests** in `Orbit.Tests/Tasks/ParentRollUpRulesTests.cs` (class `ParentRollUpRulesTests`,
  with `using static Orbit.Data.Entities.TaskItemStatus;` and `using static Orbit.Data.Entities.TaskPriority;`, helpers
  `static SubtaskFacts S(TaskItemStatus status = Todo, TaskPriority priority = Medium, DateOnly? start = null,
  DateOnly? due = null, int? estimate = null, Guid? sprint = null, DateOnly? sprintStart = null, bool backlog = false)` and
  `static RolledUpValues Of(params SubtaskFacts[] s) => ParentRollUpRules.Of(s)`):

  ```csharp
  // SUB-001
  Assert.Equal(Cancelled, ParentRollUpRules.Status([Cancelled, Cancelled]));
  // SUB-002
  Assert.Equal(Done, ParentRollUpRules.Status([Done, Done, Cancelled]));
  // SUB-003
  Assert.Equal(Todo, ParentRollUpRules.Status([Todo, Todo, Cancelled]));
  // SUB-004
  Assert.Equal(Blocked, ParentRollUpRules.Status([Blocked, Blocked]));
  Assert.Equal(Blocked, ParentRollUpRules.Status([Done, Blocked]));
  Assert.Equal(Waiting, ParentRollUpRules.Status([Waiting, Done]));
  Assert.Equal(Blocked, ParentRollUpRules.Status([Waiting, Blocked]));
  // SUB-005
  foreach (var mix in new TaskItemStatus[][] { [Todo, Done], [Todo, Blocked], [Todo, Waiting], [InProgress, Blocked], [Todo, InProgress] })
      Assert.Equal(InProgress, ParentRollUpRules.Status(mix));
  // SUB-022: a new open subtask under a Done parent reopens it
  Assert.Equal(InProgress, ParentRollUpRules.Status([Done, Todo]));
  // SUB-010: exhaustive - two groups of two subtasks, every status: rolling up the groups equals rolling up the leaves
  var all = Enum.GetValues<TaskItemStatus>();
  foreach (var a in all) foreach (var b in all) foreach (var c in all) foreach (var d in all)
      Assert.Equal(ParentRollUpRules.Status([a, b, c, d]),
                   ParentRollUpRules.Status([ParentRollUpRules.Status([a, b]), ParentRollUpRules.Status([c, d])]));
  ```

  Field rules (`Of`), with `Nov(n) = new DateOnly(2026, 11, n)`:
  ```csharp
  // SUB-006: highest open priority; none open -> highest overall; cancelled ignored
  Assert.Equal(Low, Of(S(Todo, Low), S(Done, Critical)).Priority);
  Assert.Equal(High, Of(S(Done, Low), S(Done, High)).Priority);
  Assert.Equal(Low, Of(S(Todo, Low), S(Cancelled, Critical)).Priority);
  // SUB-007: earliest start, latest due, missing dates skipped, cancelled ignored
  var v = Of(S(start: Nov(3), due: Nov(10)), S(start: Nov(1)), S(Cancelled, start: Nov(1).AddDays(-30), due: Nov(30)));
  Assert.Equal(Nov(1), v.StartDate); Assert.Equal(Nov(10), v.DueDate);
  Assert.Null(Of(S(), S()).StartDate); Assert.Null(Of(S(), S()).DueDate);
  // SUB-008: estimate sum, null when none has one
  Assert.Equal(90, Of(S(estimate: 60), S(), S(estimate: 30), S(Cancelled, estimate: 600)).EstimateMinutes);
  Assert.Null(Of(S(), S()).EstimateMinutes);
  // SUB-009: earliest open sprint by start; Backlog, then Not Planned; all closed -> same rule over all non-cancelled
  Guid a = Guid.NewGuid(), b = Guid.NewGuid(), z = Guid.NewGuid();
  Assert.Equal(SprintPlacement.In(a), Of(S(sprint: b, sprintStart: Nov(15)), S(sprint: a, sprintStart: Nov(1)), S(Done, sprint: z, sprintStart: Nov(1).AddDays(-31))).Sprint);
  Assert.Equal(SprintPlacement.Backlog, Of(S(backlog: true), S(), S(Done, sprint: z, sprintStart: Nov(1))).Sprint);
  Assert.Equal(SprintPlacement.NotPlanned, Of(S(), S(Done, backlog: true)).Sprint);
  Assert.Equal(SprintPlacement.In(a), Of(S(Done, sprint: b, sprintStart: Nov(15)), S(Done, sprint: a, sprintStart: Nov(1))).Sprint);
  // Empty input is a programming error
  Assert.Throws<ArgumentException>(() => ParentRollUpRules.Of([]));
  ```
  Note the Backlog case: the open subtasks decide, so a Done subtask in a sprint doesn't count while any subtask is open.

  SUB-011 (`TaskStatusStamps.Apply`):
  ```csharp
  var t = new TaskItem { Status = Todo }; var now = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
  TaskStatusStamps.Apply(t, InProgress, now);        Assert.Equal(now, t.FirstRespondedAt); Assert.Null(t.CompletedAt);
  TaskStatusStamps.Apply(t, Done, now.AddHours(1));  Assert.Equal(now.AddHours(1), t.CompletedAt);
  TaskStatusStamps.Apply(t, InProgress, now.AddHours(2)); Assert.Null(t.CompletedAt); Assert.Equal(now, t.FirstRespondedAt);
  ```

- [ ] **Step 2: Run to see them fail**
  Run: `dotnet test Orbit.Tests --filter "FullyQualifiedName~ParentRollUpRulesTests"`
  Expected: build error, `ParentRollUpRules` / `TaskStatusStamps` not found.

- [ ] **Step 3: Implement `ParentRollUpRules.Status` and `Of`** following spec §3.1–3.2. Cancelled subtasks are dropped first,
  unless all are cancelled (then status is Cancelled and the field rules run over all of them). The Sprint rule compares
  `SprintStart`, then `SprintId` for a stable tie-break.

- [ ] **Step 4: Move `TaskService.ApplyStatus` to `TaskStatusStamps.Apply`** unchanged (`TaskService.cs:752-758`), and replace
  its three callers in `TaskService` with `TaskStatusStamps.Apply`.

- [ ] **Step 5: Run the tests**
  Run: `dotnet test Orbit.Tests --filter "FullyQualifiedName~ParentRollUpRulesTests"` → PASS. Then `dotnet test Orbit.slnx` → all pass.

- [ ] **Step 6: Commit**
  `git add Orbit.Web/Application/ParentRollUpRules.cs Orbit.Web/Application/TaskStatusStamps.cs Orbit.Web/Application/Services/TaskService.cs Orbit.Tests/Tasks/ParentRollUpRulesTests.cs`
  `git commit -m "Add parent roll-up rules (SUB-001 to SUB-011, SUB-022)"`

---

### Task 2: Stored roll-up — column, migration, save hook, startup

**Files:**
- Modify: `Orbit.Web/Data/Entities/TaskItem.cs` (new property after `Children`)
- Modify: `Orbit.Web/Data/ApplicationDbContext.cs` (index at `:148` area; save overrides)
- Create: `Orbit.Web/Data/ParentRollUp.cs`
- Modify: `Orbit.Web/Application/ParentRollUpRules.cs` (add `SubtaskChange`, `RollUpFields`, `AffectedParents`)
- Modify: `Orbit.Web/Application/TimeRules.cs` (add `StoppedClockEntry`)
- Modify: `Orbit.Web/Application/Services/TimeEntryService.cs:274-299` (`SaveStopAsync` uses `StoppedClockEntry`)
- Modify: `Orbit.Web/Application/Services/AuditService.cs:12` (static `Row`)
- Modify: `Orbit.Web/Data/DbInitializer.cs` (after migrations, before roles)
- Create: migration `AddHasSubtasks` (generated)
- Modify: `orbit-spec.md` §5.1, §6.15 (replace the "No roll-up" bullet and assumption 3), §13 entry **145**, §14 step **129**
- Test: `Orbit.Tests/Tasks/ParentRollUpRulesTests.cs`, `Orbit.Tests/Tasks/TimeRulesTests.cs`

**Interfaces:**
- Consumes: `ParentRollUpRules.Of`, `TaskStatusStamps.Apply` (Task 1).
- Produces:
  ```csharp
  public bool HasSubtasks { get; set; }                       // TaskItem
  public sealed record SubtaskChange(bool Added, Guid? OriginalParentId, Guid? ParentId, bool RollUpFieldChanged);
  public static readonly IReadOnlyList<string> RollUpFields;  // ParentRollUpRules: Status, Priority, StartDate, DueDate, EstimateMinutes, SprintId, InBacklog
  public static IReadOnlySet<Guid> AffectedParents(IEnumerable<SubtaskChange> changes); // ParentRollUpRules
  public static TimeEntry? StoppedClockEntry(RunningClock clock, DateTime now);         // TimeRules; null under one minute
  public static AuditLog Row(Actor actor, string entityType, Guid entityId, string action, Guid? departmentId, string? summary, object? details = null); // AuditService
  namespace Orbit.Data;
  public static class ParentRollUp
  {
      public static Task ApplyAsync(ApplicationDbContext db, CancellationToken ct);          // from the save override
      public static Task<int> RecalculateAllAsync(ApplicationDbContext db, CancellationToken ct); // startup; parents changed
  }
  ```

- [ ] **Step 1: Write the failing tests**
  In `ParentRollUpRulesTests`:
  ```csharp
  [Fact] public void AffectedParents_includes_old_and_new_parent() // Review Focus 1
  {
      Guid p1 = Guid.NewGuid(), p2 = Guid.NewGuid(), p3 = Guid.NewGuid();
      var set = ParentRollUpRules.AffectedParents([
          new(Added: false, OriginalParentId: p1, ParentId: p2, RollUpFieldChanged: false),  // moved
          new(Added: true, OriginalParentId: null, ParentId: p3, RollUpFieldChanged: false), // new subtask
          new(Added: false, OriginalParentId: null, ParentId: null, RollUpFieldChanged: true), // top-level task: nothing
          new(Added: false, OriginalParentId: p3, ParentId: p3, RollUpFieldChanged: false)]); // untouched subtask: nothing new
      Assert.Equal(new HashSet<Guid> { p1, p2, p3 }, set);
  }
  ```
  In `TimeRulesTests` (SUB-024, Review Focus 5):
  ```csharp
  [Fact] public void StoppedClockEntry_logs_the_running_time()
  {
      var start = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
      var clock = new RunningClock { UserId = Guid.NewGuid(), TaskId = Guid.NewGuid(), StartedAt = start, LastSeenAt = start.AddMinutes(44) };
      var entry = TimeRules.StoppedClockEntry(clock, start.AddMinutes(45));
      Assert.Equal((45, clock.TaskId, clock.UserId, DateOnly.FromDateTime(start)), (entry!.DurationMinutes, entry.TaskId, entry.UserId, entry.Date));
      var fresh = new RunningClock { UserId = clock.UserId, TaskId = clock.TaskId, StartedAt = start, LastSeenAt = start };
      Assert.Null(TimeRules.StoppedClockEntry(fresh, start.AddSeconds(20))); // under a minute -> nothing logged
  }
  ```
- [ ] **Step 2: Run to see them fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~ParentRollUpRulesTests|FullyQualifiedName~TimeRulesTests"` → build errors.
- [ ] **Step 3: Implement `AffectedParents`** (Added → `ParentId`; `OriginalParentId != ParentId` → both non-null ids;
  `RollUpFieldChanged` → `ParentId`) and **`StoppedClockEntry`** (the entry `SaveStopAsync` builds today, including its
  `Note`; `SaveStopAsync` then calls it). Run the tests → PASS.
- [ ] **Step 4: Add `HasSubtasks`** to `TaskItem` (doc: true while any task names this one as `ParentTaskId`; maintained by
  `ParentRollUp`, spec §2) and `b.HasIndex(t => t.HasSubtasks);` beside the other `TaskItem` indexes.
- [ ] **Step 5: Add `AuditService.Row`** (the row-building half of `Add`; `Add` becomes `db.AuditLogs.Add(Row(...))`).
- [ ] **Step 6: Implement `ParentRollUp`** in `Orbit.Web/Data/ParentRollUp.cs`:
  - `ApplyAsync`: `db.ChangeTracker.DetectChanges()`; build a `SubtaskChange` per tracked `TaskItem` entry that is Added or
    Modified (`OriginalParentId` from `entry.OriginalValues[nameof(TaskItem.ParentTaskId)]` for Modified;
    `RollUpFieldChanged` = any of `RollUpFields` has `entry.Property(f).IsModified`); if `AffectedParents` is empty, return;
    else `RecalculateAsync(db, parents, ct)`.
  - `RecalculateAsync(db, IEnumerable<Guid> start, ct)` — the algorithm, since the tracker matters:
    ```
    queue = start (in order); visited = {}
    while queue not empty:
      id = dequeue; if !visited.Add(id) or visited.Count > 10_000: continue
      parent = the tracked TaskItem entry with this id if any (incl. Added), else await db.Tasks.FirstOrDefaultAsync(t => t.Id == id)
      if parent is null: continue
      // Current children = DB rows ∪ tracked entries, filtered on the *current* ParentTaskId
      // (a child moved in isn't in the DB result yet; one moved out still is):
      children = (await db.Tasks.Where(t => t.ParentTaskId == id).ToListAsync())
                 .Concat(db.ChangeTracker.Entries<TaskItem>().Where(e => e.State is Added or Modified).Select(e => e.Entity))
                 .Where(t => t.ParentTaskId == id).DistinctBy(t => t.Id).ToList()
      if children empty:
        if parent.HasSubtasks: set false, audit ("hasSubtasks" true -> false); values are kept
        continue
      starts = sprint StartDate for the children's SprintIds (one query)
      values = ParentRollUpRules.Of(children as SubtaskFacts)
      changes = ChangeSet tracking hasSubtasks, status, priority, startDate, dueDate, estimateMinutes, sprintId, inBacklog
      becameParent = !parent.HasSubtasks
      write the values (TaskStatusStamps.Apply when the status differs; values.Sprint.ApplyTo(parent)); HasSubtasks = true
      if becameParent:
        rows = await db.TaskAssignments.Where(a => a.TaskId == id).ToListAsync()
        track "assigneeIds" (rows' user ids -> []); db.TaskAssignments.RemoveRange(rows)
        parent.PlannedFor = null
        for each RunningClock on parent: Remove it; StoppedClockEntry -> db.TimeEntries.Add + TimeLogged audit row (Actor.System, clock = true)
      if changes.HasChanges:
        parent.UpdatedAt = now
        db.AuditLogs.Add(AuditService.Row(Actor.System, AuditEntity.Task, parent.Id, AuditAction.Updated, parent.DepartmentId,
                         parent.Title, new Dictionary<string, object?>(changes.Changes) { ["fromSubtasks"] = true }))
        if parent.ParentTaskId is Guid up: enqueue up
    ```
  - `RecalculateAllAsync`: load `(Id, ParentTaskId)` of every task some task names as parent, order deepest first (depth
    from the parent chain, guarded at 64), call `RecalculateAsync` with that order, return how many parents got an audit row.
- [ ] **Step 7: Override the save** in `ApplicationDbContext`:
  `SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct)` awaits `ParentRollUp.ApplyAsync(this, ct)` then
  the base. `SaveChanges(bool)` runs `ParentRollUp.ApplyAsync(this, default).GetAwaiter().GetResult()` then the base (no
  caller uses it today; it keeps the rule true for any future one).
- [ ] **Step 8: Migration** — from the repo root: `dotnet ef migrations add AddHasSubtasks --project Orbit.Web`. In `Up`, after
  the `AddColumn`, add:
  `migrationBuilder.Sql("UPDATE \"Tasks\" t SET \"HasSubtasks\" = EXISTS (SELECT 1 FROM \"Tasks\" c WHERE c.\"ParentTaskId\" = t.\"Id\");");`
- [ ] **Step 9: Startup** — in `DbInitializer.InitializeAsync`, after the migration block:
  `var rolled = await ParentRollUp.RecalculateAllAsync(db, ct); if (rolled > 0) { await db.SaveChangesAsync(ct); logger.LogInformation("Rolled up {Count} parent task(s) from their subtasks.", rolled); }`
- [ ] **Step 10: Build and test** — `dotnet build Orbit.slnx -c Release` → 0 errors; `dotnet test Orbit.slnx -c Release` → all pass.
- [ ] **Step 11: Check by running the app** (`dotnet run --project Orbit.Web`):
  - Create task A with an estimate, an assignee and a running clock. Add subtask B (due 10 Nov, estimate 2h).
    Expected: A's due date is 10 Nov and its estimate 2h; A has no assignees; the clock stopped and logged; A's history has a
    System entry marked `fromSubtasks`.
  - Set B In Progress → A In Progress. Set B Done → A Done with `CompletedAt` set.
  - Move B under task C → A loses `HasSubtasks` (keeps its values); C rolls up.
  - Three levels (A › B › C): change C's due date → A's due date follows.
  - Migration: `dotnet ef database update AddNotPlanned --project Orbit.Web`, then `dotnet ef database update --project Orbit.Web`; both succeed.
- [ ] **Step 12: Spec** — orbit-spec §5.1 (`HasSubtasks`); §6.15: replace "**No roll-up.** …" and assumption (3) with the
  roll-up rules (spec §3) and display summary; decision **145** ("Parents roll up from their subtasks", reversing decision
  30's roll-up rule, with why: double counting and visibility); build-order step **129**.
- [ ] **Step 13: Commit**
  `git commit -m "Store parent roll-up: HasSubtasks, save hook, startup recalculation (SUB-024)"` (all files above, plus the migration).

---

### Task 3: Parent restrictions in the services and access rules

**Files:**
- Create: `Orbit.Web/Application/ParentRules.cs`
- Modify: `Orbit.Web/Application/ParentRollUpRules.cs` (add `FinishesParent`)
- Modify: `Orbit.Web/Application/AccessPolicy.cs:36-37, 45, 107-109, 116`
- Modify: `Orbit.Web/Helpers/Ui.cs:161` (`AllowedStatuses`)
- Modify: `Orbit.Web/Application/Services/TaskService.cs` (`UpdateAsync:193`, `ChangeStatusAsync:324`, assignee methods
  `:358, :374, :381`, `TakeAsync:438`, `ChangeDueDateAsync:476`, `ChangeEstimateAsync:496`, `ChangeDatesAsync:518`,
  `SetPlannedForAsync:547`, `CarryOverAsync:567`, `CarryOverUnfinishedAsync:592`, `MoveToSprintAsync:685`, `ChangeSprintAsync:710`)
- Modify: `Orbit.Web/Application/Services/TimeEntryService.cs` (`AddAsync:58`, `StartClockAsync:168`)
- Modify: `Orbit.Web/Application/Services/TaskStructureService.cs:281-314` (closing rule, finish gate), `:332-333` (closed parent), `:135` (picker)
- Modify: `Orbit.Web/Application/Services/SprintService.cs:149, :183` (sprint completion skips parents)
- Modify: `orbit-spec.md` §6.15 (parent behaviour, closing, reopening, finish gate), §6.3 (completion skips parents), decision **146**, step **130**
- Test: `Orbit.Tests/Tasks/ParentRulesTests.cs`

**Interfaces:**
- Consumes: `ParentRollUpRules.Status` (Task 1), `TaskItem.HasSubtasks` (Task 2).
- Produces:
  ```csharp
  public static class ParentRules
  {
      public static string DerivedMessage(TaskItem task, string field);        // "{Number} has subtasks; its {field} comes from them."
      public static void RequireNotParent(TaskItem task, string field);        // ValidationException(DerivedMessage)
      public static string? DerivedChange(TaskItem task, TaskItemStatus status, TaskPriority priority, DateOnly? startDate,
          DateOnly? dueDate, int? estimateMinutes, SprintPlacement sprint, bool assigneesChange); // first differing field, or null
      public const string NoTimeMessage = "This task has subtasks; log time on one of them.";
  }
  public static bool FinishesParent(IEnumerable<TaskItemStatus> otherSubtasks, TaskItemStatus childTo, TaskItemStatus parentStatus); // ParentRollUpRules
  ```
  Field words used by `DerivedChange` and `RequireNotParent`, in this order: `"status"`, `"priority"`, `"start date"`,
  `"due date"`, `"estimate"`, `"sprint"`, `"assignees"`; `"day plan"` for `SetPlannedForAsync`.

- [ ] **Step 1: Write the failing tests** (`ParentRulesTests`, fixture `Parent()` = a `TaskItem` with `Number = "T-26-00012"`,
  `HasSubtasks = true`, `Assignments = Assigned.To()`, `DepartmentId = It`; `Plain()` the same with `HasSubtasks = false`):
  ```csharp
  // SUB-020: derived fields refused on a parent, allowed unchanged, ignored on a plain task
  var p = Parent(); p.DueDate = new(2026, 11, 10);
  Assert.Null(ParentRules.DerivedChange(p, p.Status, p.Priority, p.StartDate, p.DueDate, p.EstimateMinutes, SprintPlacement.Of(p), false));
  Assert.Equal("due date", ParentRules.DerivedChange(p, p.Status, p.Priority, p.StartDate, new(2026, 11, 11), p.EstimateMinutes, SprintPlacement.Of(p), false));
  Assert.Equal("assignees", ParentRules.DerivedChange(p, p.Status, p.Priority, p.StartDate, p.DueDate, p.EstimateMinutes, SprintPlacement.Of(p), true));
  Assert.Null(ParentRules.DerivedChange(Plain(), Done, Critical, null, null, 60, SprintPlacement.Backlog, true));
  Assert.Equal("T-26-00012 has subtasks; its due date comes from them.", ParentRules.DerivedMessage(p, "due date"));
  // SUB-020: no planning, taking or time on a parent
  var member = TestActors.Member(It);
  Assert.False(AccessPolicy.CanPlanTask(member, Parent()));  Assert.True(AccessPolicy.CanPlanTask(member, Plain()));
  Assert.False(AccessPolicy.CanTakeTask(member, Parent()));  Assert.True(AccessPolicy.CanTakeTask(member, Plain()));
  Assert.False(AccessPolicy.CanLogTimeForOthers(TestActors.DepartmentAdmin(It), Parent()));
  Assert.Equal([Parent().Status], Ui.AllowedStatuses(TestActors.SystemAdmin(), Parent()));
  // SUB-021: closing the last open subtask finishes the parent; closing another doesn't
  Assert.True(ParentRollUpRules.FinishesParent([Done, Cancelled], Done, InProgress));
  Assert.True(ParentRollUpRules.FinishesParent([Done], Cancelled, InProgress));
  Assert.False(ParentRollUpRules.FinishesParent([Todo], Done, InProgress));
  Assert.False(ParentRollUpRules.FinishesParent([Done], Done, Done));
  ```
  (If `CanTakeTask(member, Plain())` needs `TasksTake` in the member role, use the same actor `AccessPolicyTests` uses at `:25`.)
- [ ] **Step 2: Run to see them fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~ParentRulesTests"` → build errors.
- [ ] **Step 3: Implement `ParentRules`, `FinishesParent`** (`Status(others ++ childTo) == Done && parentStatus != Done`),
  the four `AccessPolicy` rules (`&& !t.HasSubtasks`; for `CanLogTimeFor` and `CanLogTimeForOthers` put it first), and
  `Ui.AllowedStatuses` (a parent gets `[task.Status]`). Run → PASS.
- [ ] **Step 4: Service refusals.**
  - `UpdateAsync`: after `SolutionNoteRules.Require`, `if (ParentRules.DerivedChange(task, newStatus, input.Priority, input.StartDate, input.DueDate, estimate, placement, !assignees.IsEmpty) is { } field) throw new ValidationException(ParentRules.DerivedMessage(task, field));`
    (`estimate` and `placement` are computed above it today; move the `estimate` line up).
  - Each single-field method listed under Files calls `ParentRules.RequireNotParent(task, "<field word>")` right after its
    view check. `MoveToSprintAsync` refuses when any selected task is a parent, naming its number.
    `CarryOverAsync` / `CarryOverUnfinishedAsync` select only `!t.HasSubtasks`.
  - `TimeEntryService.AddAsync` and `StartClockAsync`: `if (task.HasSubtasks) throw new ValidationException(ParentRules.NoTimeMessage);` before the access check.
- [ ] **Step 5: Closing and gates** in `TaskStructureService.EnsureStatusChangeAllowedAsync`:
  - Delete the open-children block (`:284-292`).
  - After the existing finish check: when `to.IsClosed()` and the task has a parent, walk up: load the parent and its other
    subtasks' statuses; if `FinishesParent(...)`, load the parent's links as successor whose type doesn't gate start and
    aren't met; if any, throw `$"Can't finish \"{task.Title}\": it completes \"{parent.Title}\", which waits on {Reasons(unmet, parent.Id)}."`;
    otherwise continue with the grandparent as if the parent became Done. Stop when it doesn't finish its parent.
- [ ] **Step 6: Reopening** — delete the closed-parent refusal in `ValidateParentAsync` (`:332-333`) and the open-only filter
  in the parent picker query (`:135`); the picker already labels a candidate's status.
- [ ] **Step 7: Sprint completion** — add `&& !t.HasSubtasks` to the open-task queries at `SprintService.cs:149` and `:183`.
- [ ] **Step 8: Build, test, check** — `dotnet test Orbit.slnx -c Release` → pass. Run the app: on a parent, the status
  control is a plain chip, Take/clock/Today/sprint controls are gone; editing a parent's due date through the API returns
  the message; closing the last subtask of a parent with an unmet FF link is refused; adding an open subtask under a Done
  parent reopens it; completing a sprint leaves parents alone and their sprint follows the moved subtasks.
- [ ] **Step 9: Spec** — orbit-spec §6.15 (derived fields, closing by itself, reopening, finish gate on the last open
  subtask, no time on parents), §6.3 (completion skips parents), decision **146**, step **130**.
- [ ] **Step 10: Commit** — `git commit -m "Parents: derived fields refused, no planning or time, finish gate on last subtask (SUB-020, SUB-021)"`

---

### Task 4: Parents on the edit form and the task page; Add subtask pre-fill

**Files:**
- Modify: `Orbit.Web/Components/Pages/Tasks/TaskForm.cs` (add `PrefillFromParent`)
- Modify: `Orbit.Web/Components/Pages/Tasks/TaskEditor.razor:340-346` (call it), and the status/priority/start/due/estimate/sprint/assignee inputs (disable on a parent)
- Modify: `Orbit.Web/Components/Pages/Tasks/TaskDetails.razor:202` (`TaskAssigneesCard CanEdit`), `TaskFactsCard.razor` ("from subtasks" labels)
- Modify: `orbit-spec.md` §6.15 "Where it shows" (form and task page), decision **146** (extend), README task-form notes
- Test: `Orbit.Tests/Tasks/TaskFormPrefillTests.cs`

**Interfaces:**
- Consumes: `TaskItem.HasSubtasks`.
- Produces: `public void PrefillFromParent(TaskItem parent)` on `TaskForm` — no-op when `parent.HasSubtasks`; otherwise copies
  `AssigneeIds`, `Priority`, `DueDate`, `EstimateMinutes`, and `Sprint = SprintPlacement.Of(parent)` unless
  `parent.Sprint?.Status == SprintStatus.Completed` (then `Sprint` is left as it was).

- [ ] **Step 1: Write the failing test** (SUB-023):
  ```csharp
  var sprint = new Sprint { Id = Guid.NewGuid(), Status = SprintStatus.Active };
  var parent = new TaskItem { Priority = High, DueDate = new(2026, 11, 10), EstimateMinutes = 480, SprintId = sprint.Id, Sprint = sprint, Assignments = Assigned.To(alice) };
  var form = new TaskForm { Sprint = SprintPlacement.Backlog };
  form.PrefillFromParent(parent);
  Assert.Equal((High, (DateOnly?)new DateOnly(2026, 11, 10), (int?)480, SprintPlacement.In(sprint.Id)), (form.Priority, form.DueDate, form.EstimateMinutes, form.Sprint));
  Assert.Equal([alice], form.AssigneeIds);
  sprint.Status = SprintStatus.Completed; var later = new TaskForm { Sprint = SprintPlacement.Backlog }; later.PrefillFromParent(parent);
  Assert.Equal(SprintPlacement.Backlog, later.Sprint);
  parent.HasSubtasks = true; var none = new TaskForm(); none.PrefillFromParent(parent);
  Assert.Equal((Medium, (DateOnly?)null), (none.Priority, none.DueDate));
  ```
- [ ] **Step 2: Run to see it fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~TaskFormPrefillTests"` → build error.
- [ ] **Step 3: Implement `PrefillFromParent`** and call it in `TaskEditor` right after `form.DepartmentId = parent.DepartmentId;`. Run → PASS.
- [ ] **Step 4: Read-only on the form** — when the edited task `HasSubtasks`, set `Disabled="true"` and `HelperText="From subtasks"`
  on the status, priority, start date, due date, estimate, sprint and assignee inputs. The saved values pass through
  unchanged, so `UpdateAsync` accepts them.
- [ ] **Step 5: Task page** — `TaskAssigneesCard CanEdit="_canEdit && !t.HasSubtasks"`, and when `t.HasSubtasks` show
  "Assignees are on the subtasks." in its place of the add control; in `TaskFactsCard`, suffix due date, start date,
  estimate, priority and sprint with a muted "(from subtasks)" when `Task.HasSubtasks`.
- [ ] **Step 6: Check by running** — *Add subtask* on a task without subtasks pre-fills the five fields; on a parent it doesn't.
  Editing a parent shows those fields disabled and saving a title change works.
- [ ] **Step 7: Spec and README; commit** — `git commit -m "Parent tasks on the form and task page; Add subtask pre-fill (SUB-023)"`

---

### Task 5: Claude's tools, the Gantt, the critical path and due-date emails

**Files:**
- Modify: `Orbit.Web/Application/Models/TaskModels.cs` (`TaskFilter.HasSubtasks`), `TaskService.ListAsync:32-60` (apply it)
- Modify: `Orbit.Web/Mcp/OrbitTools.cs` (`TaskDto:1246` gains `hasSubtasks`; `list_tasks` parameter; descriptions of
  `create_task`/`update_task` `parentTaskId` and `update_task`), `Orbit.Web/Program.cs` (`ServerInstructions`), `README.md` (tool list)
- Modify: `Orbit.Web/Application/Models/CriticalPathModels.cs:76-92, :153-154`, `Orbit.Web/Application/Scheduling/CriticalPathEngine.cs:272-290, :340-353`
- Modify: `Orbit.Web/Helpers/GanttChart.cs` (parent bar), `Orbit.Web/Components/Pages/Projects/ProjectGantt.razor` (no drag on parents)
- Modify: `Orbit.Web/Jobs/DueDateNotificationJob.cs:30-33`
- Modify: `orbit-spec.md` §7.1, §6.16, §6.17 (counts, warning removed, assumption 7), decision **146** (extend), step **130** (extend)
- Test: `Orbit.Tests/Scheduling/CriticalPathEngineTests.cs`

**Interfaces:**
- Produces: `public bool? HasSubtasks { get; set; }` on `TaskFilter`; `TaskAnalysis` gains a last positional parameter
  `bool IsParent = false` (stored analyses deserialise as false).

- [ ] **Step 1: Write the failing test** (SUB-025) in `CriticalPathEngineTests`: an FS chain A → B, plus a parent P with
  `HasSubtasks = true`, dates spanning A and B, linked FS before A. Assert `result.Tasks.Single(t => t.TaskId == P.Id).IsParent`
  and that `result.CriticalTaskCount` equals the critical rows that aren't parents (2). Also assert no warning with code
  `"ParentWindow"` for a parent whose dates don't cover a subtask.
- [ ] **Step 2: Run to see it fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~CriticalPathEngineTests"`.
- [ ] **Step 3: Implement** — engine passes `t.HasSubtasks` as `IsParent` on both row constructors; `CriticalTaskCount` and
  `NearCriticalTaskCount` add `&& !t.IsParent`; delete the `ParentWindow` warning block and `PlanIssueCodes.ParentWindow`.
  Run → PASS.
- [ ] **Step 4: MCP** — `TaskDto` adds `hasSubtasks = t.HasSubtasks`; `list_tasks` gets
  `[Description("true for parent tasks only (tasks with subtasks), false for the tasks without subtasks - the work itself.")] bool? hasSubtasks = null`
  mapped to `TaskFilter.HasSubtasks`, which `ListAsync` applies as `q.Where(t => t.HasSubtasks == value)`.
  Descriptions: `update_task` says a parent's status, priority, dates, estimate, sprint and assignees come from its subtasks
  and are refused; `parentTaskId` (both tools) says giving a task its first subtask replaces its own values and removes its
  assignees, and drops "closing a parent with open subtasks". Add the same to `ServerInstructions` and the README tool list.
- [ ] **Step 5: Gantt** — in `GanttChart.Build`, a row whose task `HasSubtasks` gets no `Bar`/`Milestone` of its own, only its
  `Summary`; in `ProjectGantt.razor` a parent row is never draggable.
- [ ] **Step 6: Due-date notices** — add `&& !t.HasSubtasks` to the job's query.
- [ ] **Step 7: Build, test, check** — `dotnet test Orbit.slnx -c Release` → pass; run the app: `list_tasks` with
  `hasSubtasks=false` leaves parents out (MCP Inspector or Claude); the Gantt shows a parent as a summary span only.
- [ ] **Step 8: Spec and commit** — `git commit -m "Parents in MCP, Gantt, critical path and due notices (SUB-025)"`

---

### Task 6: Counting without parents

**Files:**
- Create: `Orbit.Web/Application/TaskQueries.cs`
- Modify (apply `WorkOnly()` to the task query, or `!t.HasSubtasks` with a `// WorkOnly (§6.15)` comment inside a nested
  `p.Tasks.Count(...)`), line numbers from the survey:
  - `Application/Services/DashboardService.cs:94-97, 100-101, 108-109, 114, 120-125, 131-134, 140-142, 152-168, 177-183, 215-219`
  - `Application/Services/ProjectService.cs:33-39, 109-110, 112-130` (leave `:108` time sum as is)
  - `Application/Services/DepartmentService.cs:34, 52-54, 56-57`
  - `Application/Services/SprintService.cs:19-35, 52` (list counts; the sprint page's and board's tasks)
  - `Application/Services/SprintPlanningService.cs:37-49` (via `SprintPlanningRules.Shown`, below)
  - `Application/Services/TaskService.cs:603-652` (`GetDayPlanAsync`: planned, unfinished and last-plan queries)
  - `Application/Services/ReportingService.cs:17-33, 36-49, 55-85, 88-118, 159-167, 219-232, 322-326, 389-409`
    (task facts only; time-entry sums by task keep every task)
  - `Application/Services/AssetService.cs:181-183, 197`
  - `Mcp/OrbitTools.cs:1312-1334` (`ProjectDto` counts over `p.Tasks`)
  - `Application/SprintPlanningRules.cs:26-28` (`Shown` adds `&& !t.HasSubtasks`)
  - `Components/Pages/Projects/ProjectDetails.razor:216` ("(x of y)" counts tasks without subtasks)
- Modify: `orbit-spec.md` §6.9, §6.1 (progress), §6.3 and §6.12 (parents left out of Planning, sprint pages and Today),
  §6.10 (no time on parents; reverses decision 64's parent-and-subtask time), §12 (reports), decision **147**, step **131**
- Test: `Orbit.Tests/Tasks/WorkOnlyTests.cs`, `Orbit.Tests/Sprints/SprintPlanningRulesTests.cs`

**Interfaces:**
- Produces: `public static IQueryable<TaskItem> WorkOnly(this IQueryable<TaskItem> q)` in `Orbit.Application.TaskQueries`.

- [ ] **Step 1: Write the failing tests** (SUB-019):
  ```csharp
  TaskItem[] tasks = [new() { Title = "parent", HasSubtasks = true }, new() { Title = "work" }];
  Assert.Equal(["work"], tasks.AsQueryable().WorkOnly().Select(t => t.Title).ToList());
  ```
  and in `SprintPlanningRulesTests`, `Shown_leaves_parents_out`: a project task on the Backlog with `HasSubtasks = true` is
  not in `SprintPlanningRules.Shown(...)`, the same task without it is.
- [ ] **Step 2: Run to see them fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~WorkOnlyTests|FullyQualifiedName~SprintPlanningRulesTests"`.
- [ ] **Step 3: Implement `WorkOnly` and `Shown`** → PASS.
- [ ] **Step 4: Apply at every site listed.** Afterwards, `grep -rn "WorkOnly" Orbit.Web --include=*.cs --include=*.razor`
  lists each one.
- [ ] **Step 5: Check by running** — with a parent holding two open subtasks: the dashboard's open count, up-for-grabs and
  per-department figures count 2, not 3; each tile's link opens a Tasks list whose header shows the same number; project
  progress, the sprint page, Planning (parent absent, subtasks present), Today and the project status report count 2.
- [ ] **Step 6: Spec and commit** — `git commit -m "Leave parents out of every count and estimate sum (SUB-019)"`

---

### Task 7: Tree and display rules

**Files:**
- Create: `Orbit.Web/Application/TaskTreeRules.cs`, `Orbit.Web/Application/ParentDisplayRules.cs`, `Orbit.Web/Application/Models/TaskTreeModels.cs`
- Modify: `Orbit.Web/Helpers/GanttChart.cs:70-83` (use `TaskTreeRules.Build`; order unchanged)
- Test: `Orbit.Tests/Tasks/TaskTreeRulesTests.cs`, `Orbit.Tests/Tasks/ParentDisplayRulesTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // Models/TaskTreeModels.cs (namespace Orbit.Application.Models)
  public sealed record TaskTreeKey(Guid Id, Guid? ParentTaskId, bool IsMatch, bool HasSubtasks, TaskItemStatus Status,
      DateOnly? DueDate, TaskPriority Priority, DateTime CreatedAt);
  // TaskTreeRules.cs
  public sealed record TreeRow(Guid Id, Guid? ParentId, int Depth, bool IsContext, bool HasShownChildren);
  public static class TaskTreeRules
  {
      public const int MaxIndent = 4, MaxDepth = 64;
      public static readonly IComparer<TaskTreeKey> ListOrder; // open first, due (nulls last) asc, priority desc, CreatedAt desc - the Tasks list's SQL order
      public static IReadOnlyList<TreeRow> Build<T>(IEnumerable<T> items, Func<T, Guid> id, Func<T, Guid?> parentId,
          Func<T, bool> isMatch, IComparer<T> siblingOrder);
      public static (IReadOnlyList<TreeRow> Rows, int TotalPages) Page(IReadOnlyList<TreeRow> rows, int page, int pageSize);
      public static IReadOnlyList<TreeRow> Visible(IReadOnlyList<TreeRow> rows, IReadOnlySet<Guid> collapsed);
      public static int Indent(int depth) => Math.Min(depth, MaxIndent);
  }
  // ParentDisplayRules.cs
  public sealed record DescendantFacts(Guid Id, Guid ParentId, bool HasSubtasks, TaskItemStatus Status, DateOnly? DueDate,
      Guid? SprintId, string? SprintName, DateOnly? SprintStart, IReadOnlyList<string> AssigneeNames);
  public sealed record StatusCount(TaskItemStatus Status, int Count);
  public sealed record ParentSummary(int SubtaskCount, IReadOnlyList<StatusCount> Statuses, DateOnly? NextDue, bool Overdue,
      string? FirstSprint, string? LastSprint, int Unplanned, IReadOnlyList<string> Assignees);
  public static class ParentDisplayRules
  {
      public static IReadOnlyDictionary<Guid, ParentSummary> Summarise(IEnumerable<Guid> parentIds,
          IReadOnlyCollection<DescendantFacts> descendants, DateOnly today);
      public static (DateOnly? From, DateOnly? To) DueRange(DateOnly? nextDue, DateOnly? lastDue); // From null = one date
  }
  ```
  Semantics: `Build` treats an item whose parent isn't among the items as a root; depth-first in `siblingOrder`; `IsContext`
  = `!isMatch`; an item never reached from a root (a loop) becomes a root itself; recursion stops at `MaxDepth`. `Page`
  fills pages with whole top-level groups (a group starts at each `Depth == 0` row) up to `pageSize` rows; a group bigger
  than `pageSize` takes a page alone; page numbers clamp to `1..TotalPages`. `Visible` drops every row below a collapsed row.
  `Summarise` works over each parent's **bottom-level** descendants (`!HasSubtasks`): `Statuses` in enum order, present ones
  only; `NextDue` = earliest due of open ones; `Overdue` = any open one due before `today`; sprints = open ones in a sprint,
  first and last by `SprintStart`; `Unplanned` = open ones in no sprint; `Assignees` = distinct names of open ones, sorted;
  `SubtaskCount` = direct children (`ParentId == parent`).

- [ ] **Step 1: Write the failing tests.** `TaskTreeRulesTests` (keys built with a helper `K(id, parent, match, ...)`):
  - SUB-016 `Matches_sit_under_their_parent_chain`: root R (no match) › P (no match) › C (match); plus a top-level match T
    due earlier. Rows in order: T (depth 0), R (0, context), P (1, context), C (2, not context).
  - Review Focus 2 `A_parent_that_matches_is_not_context`: only parent P matches; rows = [P], `IsContext == false`,
    `HasShownChildren == false`.
  - SUB-017 `Pages_never_split_a_group`: groups of 3, 3 and 60 rows with `pageSize` 5 → page 1 = first group only
    (3 + 3 > 5), page 2 = second group, page 3 = the 60-row group, `TotalPages == 3`.
  - SUB-018 `Indent_is_capped`: `Indent(7) == 4`, `Indent(2) == 2`.
  - Review Focus 3 `Visible_hides_everything_under_a_collapsed_parent`: R › P › C plus sibling S of R; collapsing P hides C
    only; collapsing R hides P and C; S stays.
  - Review Focus 4 `A_parent_loop_still_lists_every_task_once`: A.parent = B, B.parent = A → two rows, distinct ids, no hang.
  - `ListOrder_matches_the_list_sql_order`: open before closed, earlier due first, null due last, Critical before Low, newer first.

  `ParentDisplayRulesTests`, parent P with children A (a parent) and B; A's children C and D:
  - SUB-012 statuses: C Done, D InProgress, B Blocked → `[(InProgress,1),(Blocked,1),(Done,1)]` for P (A not counted);
    `SubtaskCount` 2 for P.
  - SUB-013: C due 1 Oct Done, D due 10 Nov open, B due 15 Dec open, today 12 Nov → `NextDue` 10 Nov, `Overdue` true;
    `DueRange(10 Nov, 15 Dec) == (10 Nov, 15 Dec)`, `DueRange(15 Dec, 15 Dec) == (null, 15 Dec)`, `DueRange(null, 15 Dec) == (null, 15 Dec)`.
  - SUB-014: D in "Sprint 12" (start 1 Nov), B in "Sprint 14" (start 1 Dec), another open leaf in no sprint →
    `("Sprint 12", "Sprint 14", 1)` for (`FirstSprint`, `LastSprint`, `Unplanned`); none in a sprint → both null.
  - SUB-015: D assigned Bob and Alice, B assigned Alice, Done C assigned Zed → `["Alice", "Bob"]`.
- [ ] **Step 2: Run to see them fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~TaskTreeRulesTests|FullyQualifiedName~ParentDisplayRulesTests"`.
- [ ] **Step 3: Implement both rule classes and the models** → PASS.
- [ ] **Step 4: Gantt** — replace `GanttChart.Build`'s `Visit`/`Sorted` tree walk with `TaskTreeRules.Build(all, t => t.Id,
  t => t.ParentTaskId, _ => true, <the Gantt's existing comparer: first planned date, then title>)`, mapping rows back to tasks.
  Run the app: a project's Gantt rows are in the same order as before.
- [ ] **Step 5: Commit** — `git commit -m "Tree and parent display rules (SUB-012 to SUB-018)"`

---

### Task 8: Tree data in the services

**Files:**
- Modify: `Orbit.Web/Application/Services/TaskService.cs:32-75` (extract the filter; add `ListTreeAsync`)
- Modify: `Orbit.Web/Application/Services/TaskStructureService.cs` (add `GetParentSummariesAsync` beside `GetWaitingAsync:58`)
- Modify: `Orbit.Web/Application/Models/TaskTreeModels.cs` (add the row and page records)
- Modify: `Orbit.Web/Application/TaskTreeRules.cs` (add `ForTasks`)

**Interfaces:**
- Consumes: `TaskTreeRules.Build/Page/ListOrder`, `ParentDisplayRules.Summarise`, `TaskTreeKey` (Task 7).
- Produces:
  ```csharp
  public sealed record TaskTreeRow(TaskItem Task, int Depth, bool IsContext, bool HasShownChildren);
  public sealed record TaskTreePage(IReadOnlyList<TaskTreeRow> Rows, int Page, int TotalPages, int Count); // Count = matches without subtasks
  public Task<TaskTreePage> ListTreeAsync(TaskFilter f, CancellationToken ct = default);                    // TaskService
  public Task<IReadOnlyDictionary<Guid, ParentSummary>> GetParentSummariesAsync(IEnumerable<TaskItem> tasks, DateOnly today, CancellationToken ct = default); // TaskStructureService
  public static IReadOnlyList<TaskTreeRow> ForTasks(IReadOnlyList<TaskItem> all, Func<TaskItem, bool> isMatch); // TaskTreeRules: matches plus their ancestors from `all`, in ListOrder
  ```

- [ ] **Step 1: Extract** `private static IQueryable<TaskItem> Filtered(IQueryable<TaskItem> q, Actor actor, TaskFilter f)`
  from `ListAsync` (its scope and every `Where`, lines 36-60); `ListAsync` keeps working through it. Run
  `dotnet test Orbit.slnx -c Release` → pass.
- [ ] **Step 2: `ListTreeAsync`**:
  1. `matches` = `Filtered(...)` projected to `TaskTreeKey` with `IsMatch = true` (no paging).
  2. Ancestors: repeatedly load `TaskTreeKey`s (`IsMatch = false`) for parent ids not yet loaded, from `db.Tasks` **without**
     the view scope (the "in: Parent" line already shows any parent's title), up to `TaskTreeRules.MaxDepth` rounds.
  3. `rows = TaskTreeRules.Build(keys, k => k.Id, k => k.ParentTaskId, k => k.IsMatch, TaskTreeRules.ListOrder)`;
     `(pageRows, totalPages) = TaskTreeRules.Page(rows, f.Page, Math.Clamp(f.PageSize, 1, 500))`.
  4. Load the page's tasks with `WithIncludes(db.Tasks.AsNoTracking())` by id (unscoped, as in step 2), map to `TaskTreeRow` in row order.
  5. `Count = matches.Count(k => !k.HasSubtasks)`.
- [ ] **Step 3: `GetParentSummariesAsync`** — for the given tasks that have subtasks, load descendants level by level as
  `DescendantFacts` (projection: sprint name and start through `t.Sprint`, names through `t.Assignments.Select(a => a.User.DisplayName)`),
  unscoped like the task page's Subtasks section, until no loaded descendant has subtasks (max `MaxDepth` rounds); then
  `ParentDisplayRules.Summarise`.
- [ ] **Step 4: `ForTasks`** — build `TaskTreeKey`s from `all`, add each match's ancestors found in `all`, `Build` with
  `ListOrder`, map back to `TaskTreeRow`.
- [ ] **Step 5: Build and commit** — `dotnet build Orbit.slnx -c Release` → 0 errors;
  `git commit -m "Tree listing and parent summaries in the task services"`. (Exercised through the pages in Task 9.)

---

### Task 9: Tree view on the Tasks page and the project page, with collapse memory

**Files:**
- Modify: `Orbit.Web/wwwroot/js/orbit-interop.js` (add `window.orbit.tree` after `window.orbit.filters`, `:202-220`)
- Create: `Orbit.Web/Components/Infrastructure/BrowserTreeMemory.cs`; register in `Orbit.Web/Program.cs:138` beside `BrowserFilterMemory`
- Create: `Orbit.Web/Components/Tasks/ParentStatusChips.razor`, `ParentDueRange.razor`, `ParentSprintRange.razor`
- Modify: `Orbit.Web/Components/Tasks/TaskTable.razor` (tree mode and parent cells)
- Modify: `Orbit.Web/Components/Pages/Tasks/TasksList.razor:96-108, 155, 219-298`
- Modify: `Orbit.Web/Components/Pages/Projects/ProjectDetails.razor:214-228, 321-343`
- Modify: `Orbit.Web/wwwroot/css/app.css` (tree classes)
- Modify: `orbit-spec.md` §6.2 and §6.1 (tree view, filters, paging, count, collapse), §6.15 "Where it shows" (tables), decision **148**, step **132**; `README.md`

**Interfaces:**
- Consumes: `TaskTreeRow`, `TaskTreePage`, `ListTreeAsync`, `GetParentSummariesAsync`, `ForTasks` (Task 8);
  `TaskTreeRules.Visible/Indent`, `ParentSummary`, `ParentDisplayRules.DueRange` (Task 7).
- Produces:
  - JS: `orbit.tree.collapsed()` → array of id strings from `localStorage['orbit-collapsed']` (JSON; `[]` on any error);
    `orbit.tree.set(ids, collapsed)` adds or removes ids and keeps the 2000 most recent; every storage access in try/catch.
  - `BrowserTreeMemory(IJSRuntime js)`: `Task<HashSet<Guid>> CollapsedAsync()`, `Task SetAsync(IEnumerable<Guid> ids, bool collapsed)`.
  - `TaskTable` parameters: `IReadOnlyList<TaskTreeRow>? TreeRows`, `IReadOnlyDictionary<Guid, ParentSummary>? ParentSummaries`,
    `IReadOnlySet<Guid>? Collapsed`, `EventCallback<(IReadOnlyCollection<Guid> Ids, bool Collapsed)> CollapseChanged`.

- [ ] **Step 1: Collapse memory** — the JS helper and `BrowserTreeMemory` (same shape as `BrowserFilterMemory`, no owner key).
- [ ] **Step 2: Parent cells** — `ParentStatusChips` (one `MudChip` per `StatusCount`, colour `TaskLook.Status(status)`, text
  `"{Count} {status.Label()}"`); `ParentDueRange` (`Ui.Day(From) → Ui.Day(To)`, or one date, wrapping to "→ end" in
  `orbit-sub` on a narrow screen; `orbit-overdue` when `Overdue`); `ParentSprintRange` (`First → Last` or one name, then
  `+N unplanned` in `orbit-sub`; with no sprint, `SprintPlacement.Of(task).Label()`).
- [ ] **Step 3: `TaskTable` tree mode** — when `TreeRows` is set, iterate `TaskTreeRules.Visible(rows, Collapsed)` instead
  of `Tasks`; on the Task cell apply `style="padding-left: calc(var(--orbit-tree-step) * {Indent(depth)})"`; a parent row
  with `HasShownChildren` gets a ▸/▾ `MudIconButton` raising `CollapseChanged` with its id, and every parent row shows
  `(SubtaskCount)` in `orbit-muted`; a context row gets class `orbit-tree-context` and is treated as read-only (no controls);
  the "in: Parent" line is hidden in tree mode. Above the table, when any row has shown children, "Expand all" /
  "Collapse all" raise `CollapseChanged` with every such id. In both modes, a row whose task `HasSubtasks` and has a
  summary renders the three parent cells, the assignee names from the summary, and the stored estimate as plain text.
- [ ] **Step 4: CSS** — in `app.css`: `--orbit-tree-step: 1.25rem` on `.orbit-tasks`; `.orbit-tree-context` (muted text, no
  hover emphasis); `.orbit-tree-toggle` (compact). Both themes use existing variables.
- [ ] **Step 5: Tasks page** — `ReloadAsync` calls `ListTreeAsync(filter)` and `GetParentSummariesAsync(rows' tasks, _today)`;
  `GetWaitingAsync` over the rows' tasks; `_collapsed = await TreeMemory.CollapsedAsync()` (inject `BrowserTreeMemory`
  in the page, as `OrbitListPage` injects `Memory`). Header count = `page.Count`; paging buttons use `page.TotalPages`.
  `CollapseChanged` updates `_collapsed`, calls `SetAsync` and re-renders without reloading.
- [ ] **Step 6: Project page** — replace the in-memory filter/order (`:321-330`) with
  `TaskTreeRules.ForTasks(project.Tasks, t => <the same three filter checks>)`; summaries via `GetParentSummariesAsync`;
  the same collapse wiring; the header shows "(matches without subtasks of all tasks without subtasks)".
- [ ] **Step 7: Check by running** (desktop and a ~390px wide window):
  - Unfiltered Tasks list: subtasks under parents, indented, chips/ranges on parents; the "in:" line gone.
  - *My tasks*: your subtasks under greyed parents; the count matches the dashboard's figure.
  - Search a parent's title: the parent shows as a normal row with nothing under it.
  - Collapse a parent, open a task, press Back: still collapsed; the same parent is collapsed on its project page.
  - Expand all / Collapse all; page 2 starts with a whole group; no flash of open rows on load.
  - A parent in another department you can't open: greyed row, title not a link.
  - Light and dark theme.
- [ ] **Step 8: Spec, README, commit** — `git commit -m "Tree view with collapsible parents on the Tasks and project pages"`

---

## After the last task

- `dotnet test Orbit.slnx -c Release` → all pass; `dotnet build Orbit.slnx -c Release` → 0 warnings introduced.
- Re-read the spec's §§2-8 against the branch; every SUB ID has a test or a recorded manual check.
- Use superpowers:finishing-a-development-branch.
