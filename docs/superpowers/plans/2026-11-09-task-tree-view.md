# Task Tree View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show subtasks under their parent on the Tasks page and a project's page, with collapsible parents and a display-only summary line of each parent's branch.

**Architecture:** Two pure rule classes in `Orbit.Web/Application/` build the tree (order, context rows, paging, collapse) and the branch summaries. The Tasks page gets them through a new `TaskService.ListTreeAsync`; the project page runs them on the tasks it already loads. `TaskTable` gains an optional tree mode that owns the collapsed state (kept in the browser's local storage). Nothing is stored in the database.

**Tech Stack:** .NET 10, Blazor Server + MudBlazor, EF Core + PostgreSQL, xunit.

**Spec:** `docs/superpowers/specs/2026-11-09-task-tree-view-design.md` (read it first; this plan argues from it).

## Global Constraints

- `RootNamespace` is `Orbit`: namespaces are `Orbit.Application`, `Orbit.Application.Models`, `Orbit.Components.Tasks`, `Orbit.Components.Infrastructure`, never `Orbit.Web.*`.
- No migration, no new stored field, no new permission, no MCP change, no change to counts, dashboard, reports, Gantt, critical path, Planning, sprint or Today pages.
- A parent stays a normal task: its own values keep their inline controls. The closing rule stays in `TaskStructureService.EnsureStatusChangeAllowedAsync`, unchanged.
- Fixed values: page size **50** rows (`TaskTreeRules.PageRows`); indent cap **6** levels (`TaskTreeRules.MaxIndent`); loop/depth guard **64** (`TaskTreeRules.MaxDepth`); collapsed-id cap **1000** (`BrowserTreeMemory.Limit`); local storage key `orbit-tree-{owner}`; status tooltip text exactly `Close its open subtasks first.`
- Blazor components never inject an Orbit service; pages call services inside `Load(async s => ...)` / `Act(...)`. `BrowserTreeMemory` (JS only, no DbContext) may be injected, as `BrowserFilterMemory` is.
- Tests are pure (no DB), xunit, and cite the acceptance ID in a `/// <summary>` doc comment, e.g. `/// <summary>TREE-001: ...</summary>`.
- Build and test with `-c Release` (an IDE-run `Orbit.Web` locks `bin/Debug`): `dotnet build Orbit.slnx -c Release`, `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~<Class>"`.
- Keep each edited file's line endings (`git ls-files --eol <file>`): `TaskTable.razor`, `TaskService.cs`, `TasksList.razor`, `ProjectDetails.razor`, `Program.cs`, `app.css`, `orbit-interop.js`, `orbit-spec.md`, `README.md` are CRLF on disk; `BrowserFilterMemory.cs` and `TaskStatusSelect.razor` are LF.
- British spelling in prose and identifiers. A feature change updates `orbit-spec.md` (functional section, §13 decision **145**, §14 step **129**) and `README.md` in the same task.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A visible subtask under a parent the viewer can't see** (A visible → B outside scope → C visible): C must still count in A's summary, and on the Tasks page C shows under A and B with B as a title-only context row. Pinned by TREE-010b in Task 2; the service must gather descendant ids *unscoped* and filter afterwards (Task 4, Step 1).
2. **A page number past the end** (an old link, or a filter that shrank the list): show the last page, not an empty table. Pinned by TREE-004c in Task 1.
3. **A matched parent whose subtasks the filter hid**: no toggle (there is nothing under it to collapse), but its `(n)` count and summary still show. Pinned by TREE-003c in Task 1; rendering in Task 3.
4. **Collapsed state across an inline edit**: changing a due date reloads the page's data and may move rows; the collapsed parents must stay collapsed. Checked by running in Task 4, Step 6.
5. **Collapse all on one page, then Expand all on another**: Expand all clears only that page's parents, and the stored list never grows past 1000. Pinned by TREE-020 in Task 3.

---

## File Structure

| File | Responsibility |
|---|---|
| Create `Orbit.Web/Application/TaskTreeRules.cs` | `TaskTreeNode`, `TaskTreeRow`, `TaskTreeCut` records and `TaskTreeRules`: nodes from in-memory tasks, tree order, paging, collapse, assembling entries |
| Create `Orbit.Web/Application/BranchSummaryRules.cs` | `StatusCount`, `BranchSummary` records and `BranchSummaryRules`: a parent's branch and its summary, the closing-blocked check |
| Create `Orbit.Web/Application/Models/TaskTreeModels.cs` | `TaskTreeEntry` (what `TaskTable` draws) and `TaskTreePage` (what `ListTreeAsync` returns) |
| Create `Orbit.Web/Components/Infrastructure/BrowserOwner.cs` | The signed-in user's id as the browser memory owner, shared by both memories |
| Create `Orbit.Web/Components/Infrastructure/BrowserTreeMemory.cs` | Read and write the collapsed ids through `orbit.tree` |
| Create `Orbit.Web/Components/Tasks/BranchSummaryCell.razor` | One cell's summary line (status chips, priority chips, sprint chips, due range, estimate total, assignees) |
| Modify `Orbit.Web/Components/Tasks/TaskTable.razor` | Optional `Tree` mode: indent, toggle, context rows, summary lines, Expand all / Collapse all |
| Modify `Orbit.Web/Components/Tasks/TaskStatusSelect.razor` | Disable Done and Cancelled with a reason |
| Modify `Orbit.Web/Application/Services/TaskService.cs` | Share the filter between `ListAsync` and the new `ListTreeAsync` |
| Modify `Orbit.Web/Components/Pages/Tasks/TasksList.razor` | Use `ListTreeAsync` and the tree mode |
| Modify `Orbit.Web/Components/Pages/Projects/ProjectDetails.razor` | Build the tree in memory and use the tree mode |
| Modify `Orbit.Web/Components/Infrastructure/BrowserFilterMemory.cs` | Use `BrowserOwner` |
| Modify `Orbit.Web/wwwroot/js/orbit-interop.js`, `Orbit.Web/wwwroot/css/app.css`, `Orbit.Web/Program.cs` | `orbit.tree`, tree styles, DI registration |
| Create `Orbit.Tests/Tasks/TaskTreeRulesTests.cs`, `Orbit.Tests/Tasks/BranchSummaryRulesTests.cs`, `Orbit.Tests/Tasks/BrowserTreeMemoryTests.cs` | Unit tests |
| Modify `orbit-spec.md`, `README.md` | §6.1, §6.2, §6.15, §13 item 145, §14 item 129; README task paragraphs |

---

### Task 1: Tree rules

**Files:**
- Create: `Orbit.Web/Application/TaskTreeRules.cs`
- Test: `Orbit.Tests/Tasks/TaskTreeRulesTests.cs`

**Interfaces:**
- Consumes: `TaskItem` (`Id`, `ParentTaskId`, `Status`, `DueDate`, `Priority`, `CreatedAt`), `TaskItemStatus.IsClosed()`.
- Produces (namespace `Orbit.Application`):
  ```csharp
  public sealed record TaskTreeNode(Guid Id, Guid? ParentId, bool Matched, bool IsClosed, DateOnly? DueDate, TaskPriority Priority, DateTime CreatedAt)
  {
      public static TaskTreeNode Of(TaskItem task, bool matched);
  }
  public sealed record TaskTreeRow(Guid Id, Guid? ParentId, int Depth, bool IsContext, bool HasChildren);
  public sealed record TaskTreeCut(IReadOnlyList<TaskTreeRow> Rows, int Page, int PageCount, int MatchCount);

  public static class TaskTreeRules
  {
      public const int PageRows = 50;
      public const int MaxIndent = 6;
      public const int MaxDepth = 64;
      public static IReadOnlyList<TaskTreeNode> NodesFor(IReadOnlyCollection<TaskItem> all, Func<TaskItem, bool> matches);
      public static IReadOnlyList<TaskTreeRow> Build(IReadOnlyCollection<TaskTreeNode> nodes);
      public static TaskTreeCut Cut(IReadOnlyList<TaskTreeRow> rows, int page, int pageRows = PageRows);
      public static IReadOnlySet<Guid> Hidden(IReadOnlyList<(Guid Id, int Depth)> rows, IReadOnlySet<Guid> collapsed);
      public static int Indent(int depth);
  }
  ```
  (`Entries(...)` is added to this class in Task 2, once `BranchSummary` exists.)

- [ ] **Step 1: Write the failing tests**

`Orbit.Tests/Tasks/TaskTreeRulesTests.cs`, namespace `Orbit.Tests.Tasks`, class `TaskTreeRulesTests` with a class doc comment citing spec §6.15 and decision 145. Helpers:

```csharp
private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
private static Guid G() => Guid.NewGuid();
private static DateOnly D(int day) => new(2026, 11, day);
private static TaskTreeNode N(Guid id, Guid? parent = null, bool matched = true, DateOnly? due = null,
    TaskPriority priority = TaskPriority.Medium, bool closed = false, int ageDays = 0) =>
    new(id, parent, matched, closed, due, priority, T0.AddDays(-ageDays));
private static TaskItem Item(TaskItem? parent = null) => new() { ParentTaskId = parent?.Id, Assignments = Assigned.To() };
private static IEnumerable<TaskTreeRow> Branch(int size)
{
    var root = G();
    yield return new(root, null, 0, false, size > 1);
    for (var i = 1; i < size; i++) yield return new(G(), root, 1, false, false);
}
```

Tests (each with its ID in the doc comment):

```csharp
/// TREE-001
[Fact] public void A_subtask_sits_directly_under_its_parent()
{
    Guid a = G(), b = G(), c = G(), x = G();
    var rows = TaskTreeRules.Build([N(c, b, due: D(5)), N(x, due: D(3)), N(a, due: D(20)), N(b, a, due: D(10))]);
    Assert.Equal([x, a, b, c], rows.Select(r => r.Id));
    Assert.Equal([0, 0, 1, 2], rows.Select(r => r.Depth));
    Assert.True(rows[1].HasChildren);
    Assert.False(rows[3].HasChildren);
    Assert.Equal(6, TaskTreeRules.Indent(9));
    Assert.Equal(2, TaskTreeRules.Indent(2));
}

/// TREE-002a: the parent is due last but its subtask first, so the branch goes ahead of a task due in between.
[Fact] public void A_branch_sits_where_its_first_match_would()
{
    Guid parent = G(), sub = G(), middle = G(), closedEarly = G(), noDue = G();
    var rows = TaskTreeRules.Build([N(parent, due: D(30)), N(sub, parent, due: D(2)), N(middle, due: D(10)),
        N(closedEarly, due: D(1), closed: true), N(noDue)]);
    Assert.Equal([parent, sub, middle, noDue, closedEarly], rows.Select(r => r.Id));
}

/// TREE-002b: siblings with the same due date go highest priority first, then newest first.
[Fact] public void Siblings_follow_todays_order()
{
    Guid top = G(), high = G(), lowNew = G(), lowOld = G();
    var rows = TaskTreeRules.Build([N(top, due: D(1)), N(lowOld, top, due: D(5), priority: TaskPriority.Low, ageDays: 3),
        N(high, top, due: D(5), priority: TaskPriority.High), N(lowNew, top, due: D(5), priority: TaskPriority.Low, ageDays: 1)]);
    Assert.Equal([top, high, lowNew, lowOld], rows.Select(r => r.Id));
}

/// TREE-003a
[Fact] public void Unmatched_ancestors_are_context_rows()
{
    Guid top = G(), mid = G(), match = G();
    var rows = TaskTreeRules.Build([N(top, matched: false), N(mid, top, matched: false), N(match, mid)]);
    Assert.Equal([true, true, false], rows.Select(r => r.IsContext));
}

/// TREE-003b: NodesFor adds a match's ancestors as context and leaves out what lies below or beside it.
[Fact] public void NodesFor_adds_ancestors_and_leaves_out_unmatched_descendants()
{
    var top = Item(); var mid = Item(top); var match = Item(mid); var below = Item(match); var other = Item();
    var nodes = TaskTreeRules.NodesFor([top, mid, match, below, other], t => t == match);
    Assert.Equal(new[] { top.Id, mid.Id, match.Id }.Order(), nodes.Select(n => n.Id).Order());
    Assert.Equal([match.Id], nodes.Where(n => n.Matched).Select(n => n.Id));
}

/// TREE-003c (Review Focus 3): a matched parent whose subtasks were filtered out has nothing to collapse.
[Fact] public void A_matched_parent_with_its_subtasks_filtered_out_has_no_children()
{
    var parent = Item(); var child = Item(parent);
    var row = Assert.Single(TaskTreeRules.Build(TaskTreeRules.NodesFor([parent, child], t => t == parent)));
    Assert.False(row.HasChildren);
}

/// TREE-004a
[Fact] public void A_page_never_splits_a_branch()
{
    var rows = Branch(30).Concat(Branch(30)).Concat(Branch(10)).ToList();
    Assert.Equal(2, TaskTreeRules.Cut(rows, 1, 50).PageCount);
    Assert.Equal(30, TaskTreeRules.Cut(rows, 1, 50).Rows.Count);
    Assert.Equal(40, TaskTreeRules.Cut(rows, 2, 50).Rows.Count);
}

/// TREE-004b
[Fact] public void An_oversized_branch_gets_a_page_of_its_own()
{
    var rows = Branch(5).Concat(Branch(80)).Concat(Branch(5)).ToList();
    Assert.Equal([5, 80, 5], Enumerable.Range(1, 3).Select(p => TaskTreeRules.Cut(rows, p, 50).Rows.Count));
    Assert.Equal(3, TaskTreeRules.Cut(rows, 1, 50).PageCount);
}

/// TREE-004c (Review Focus 2)
[Fact] public void A_page_past_the_end_shows_the_last_page()
{
    var rows = Branch(30).Concat(Branch(30)).ToList();
    var cut = TaskTreeRules.Cut(rows, 99, 50);
    Assert.Equal(2, cut.Page);
    Assert.Equal(30, cut.Rows.Count);
    var empty = TaskTreeRules.Cut([], 3, 50);
    Assert.Equal(1, empty.Page);
    Assert.Equal(1, empty.PageCount);
    Assert.Empty(empty.Rows);
}

/// TREE-005a
[Fact] public void The_match_count_leaves_out_context_rows()
{
    Guid top = G(), a = G(), b = G();
    Assert.Equal(2, TaskTreeRules.Cut(TaskTreeRules.Build([N(top, matched: false), N(a, top), N(b, top)]), 1, 50).MatchCount);
}

/// TREE-005b
[Fact] public void A_loop_or_a_deep_chain_lists_every_node_once()
{
    Guid a = G(), b = G();
    var loop = TaskTreeRules.Build([N(a, b), N(b, a)]);
    Assert.Equal(2, loop.Count);
    Assert.Equal(2, loop.Select(r => r.Id).Distinct().Count());
    var chain = new List<TaskTreeNode>();
    Guid? parent = null;
    for (var i = 0; i < 100; i++) { var id = G(); chain.Add(N(id, parent)); parent = id; }
    var deep = TaskTreeRules.Build(chain);
    Assert.Equal(100, deep.Count);
    Assert.All(deep, r => Assert.InRange(r.Depth, 0, TaskTreeRules.MaxDepth));
}

/// TREE-006: collapsing hides every row below, at any depth, and nothing beside it.
[Fact] public void Collapsing_hides_the_whole_branch_below()
{
    Guid a = G(), b = G(), c = G(), d = G();
    (Guid Id, int Depth)[] rows = [(a, 0), (b, 1), (c, 2), (d, 0)];
    Assert.Equal(new[] { b, c }.Order(), TaskTreeRules.Hidden(rows, new HashSet<Guid> { a }).Order());
    Assert.Equal([c], TaskTreeRules.Hidden(rows, new HashSet<Guid> { b }));
    Assert.Empty(TaskTreeRules.Hidden(rows, new HashSet<Guid> { d }));
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TaskTreeRulesTests"`
Expected: build error, `TaskTreeRules` / `TaskTreeNode` not found.

- [ ] **Step 3: Implement `Orbit.Web/Application/TaskTreeRules.cs`**

Records and signatures as in the Interfaces block; class doc comments cite spec §6.15 and decision 145. Rules the tests leave open:

- `Of` maps `IsClosed` from `task.Status.IsClosed()`.
- `NodesFor`: every matching task plus each one's ancestors found in `all` by `ParentTaskId` (stop at a missing parent, a repeat or `MaxDepth`); an ancestor that doesn't itself match is `Matched: false`. Each task appears once.
- `Build` orders a subtree by its **earliest matching row**. That algorithm:

  ```csharp
  // key(n) = (IsClosed, DueDate is null, DueDate, -(int)Priority, -CreatedAt.Ticks, Id), compared in that order, smaller first.
  // subtreeKey(n) = min(key(n) if n.Matched, subtreeKey(child) for each child); every node in Build's input has a matched
  // descendant or is matched, as NodesFor and ListTreeAsync guarantee. A node with no matched row in its subtree sorts last.
  // Roots = nodes whose ParentId is null or not among the nodes. Visit roots in subtreeKey order, depth-first, children in
  // subtreeKey order, keeping a visited set; stop descending past MaxDepth. Any node not visited afterwards (a loop, or below
  // the depth cap) is visited as a further root, in subtreeKey order, so every node appears exactly once.
  ```
- `HasChildren` is true when any row in the result has this row's id as its `ParentId`.
- `Cut`: split rows into branches at each `Depth == 0`; fill pages greedily with whole branches while the page stays within `pageRows`, a branch alone over the limit taking a page of its own; `PageCount` is at least 1; `Page` is clamped to `[1, PageCount]`; `MatchCount` counts every row (all pages) with `!IsContext`.
- `Hidden`: walk rows in order; when a row is in `collapsed`, every following row with a greater depth is hidden until a row at the same or a smaller depth.
- `Indent(depth) => Math.Min(depth, MaxIndent)`.

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TaskTreeRulesTests"`
Expected: all TaskTreeRulesTests pass.

- [ ] **Step 5: Commit**

```bash
git add Orbit.Web/Application/TaskTreeRules.cs Orbit.Tests/Tasks/TaskTreeRulesTests.cs
git commit -m "Task tree rules: order, context rows, paging and collapse (TREE-001 to TREE-006)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Branch summary rules and tree entries

**Files:**
- Create: `Orbit.Web/Application/BranchSummaryRules.cs`
- Create: `Orbit.Web/Application/Models/TaskTreeModels.cs`
- Modify: `Orbit.Web/Application/TaskTreeRules.cs` (add `Entries`)
- Test: `Orbit.Tests/Tasks/BranchSummaryRulesTests.cs`

**Interfaces:**
- Consumes: `TaskTreeRow` (Task 1); `TaskItem` (`Status`, `Priority`, `DueDate`, `EstimateMinutes`, `Sprint`, `SprintId`, `InBacklog`, `Assignees`, `IsOpen`); `Sprint` (`Id`, `Name`, `StartDate`); `SprintPlacement.Of(TaskItem)`.
- Produces:
  ```csharp
  // Orbit.Application
  public sealed record StatusCount(TaskItemStatus Status, int Count);
  public sealed record BranchSummary(
      int SubtaskCount, IReadOnlyList<StatusCount> Statuses, IReadOnlyList<TaskPriority> Priorities,
      IReadOnlyList<Sprint> Sprints, bool OnBacklog, bool NotPlanned,
      DateOnly? DueFrom, DateOnly? DueTo, bool Overdue,
      int? EstimateTotal, int OwnEstimate, int SubtasksEstimate,
      IReadOnlyList<ApplicationUser> Assignees, bool HasOpenSubtasks);
  public static class BranchSummaryRules
  {
      public static IReadOnlyList<TaskItem> BranchOf(Guid parentId, ILookup<Guid?, Guid> idsByParent, IReadOnlyDictionary<Guid, TaskItem> countable);
      public static BranchSummary? Summarise(TaskItem parent, IReadOnlyCollection<TaskItem> branch, DateOnly today);
  }
  // TaskTreeRules (added)
  public static IReadOnlyList<TaskTreeEntry> Entries(IReadOnlyList<TaskTreeRow> rows, IReadOnlyDictionary<Guid, TaskItem> tasks,
      Func<TaskItem, bool> titleOnly, Func<TaskItem, BranchSummary?> summary);

  // Orbit.Application.Models
  public sealed record TaskTreeEntry(TaskItem Task, int Depth, bool IsContext, bool HasChildren, bool TitleOnly, BranchSummary? Summary);
  public sealed record TaskTreePage(IReadOnlyList<TaskTreeEntry> Rows, int Page, int PageCount, int MatchCount)
  {
      public bool HasPrevious => Page > 1;
      public bool HasNext => Page < PageCount;
  }
  ```

- [ ] **Step 1: Write the failing tests**

`Orbit.Tests/Tasks/BranchSummaryRulesTests.cs`, class `BranchSummaryRulesTests`, `using static Orbit.Data.Entities.TaskItemStatus;`. Helpers:

```csharp
private static readonly DateOnly Today = new(2026, 11, 9);
private static Guid G() => Guid.NewGuid();
private static TaskItem T(TaskItemStatus s = Todo, TaskPriority p = TaskPriority.Medium, DateOnly? due = null, int? est = null,
    Sprint? sprint = null, bool backlog = false, IEnumerable<ApplicationUser>? people = null) =>
    new() { Status = s, Priority = p, DueDate = due, EstimateMinutes = est, Sprint = sprint, SprintId = sprint?.Id,
            InBacklog = backlog, Assignments = Assigned.ToPeople([.. people ?? []]) };
private static BranchSummary Summ(IReadOnlyCollection<TaskItem> branch) => BranchSummaryRules.Summarise(T(), branch, Today)!;
```

Tests:

```csharp
/// TREE-010a
[Fact] public void Status_chips_count_every_subtask_in_workflow_order()
{
    var s = Summ([T(Done), T(InProgress), T(Done), T(Cancelled), T(Todo)]);
    Assert.Equal([new StatusCount(Todo, 1), new StatusCount(InProgress, 1), new StatusCount(Done, 2), new StatusCount(Cancelled, 1)], s.Statuses);
    Assert.Equal(5, s.SubtaskCount);
}

/// TREE-010b (Review Focus 1): the branch reaches every level, passing through a task the viewer can't count.
[Fact] public void The_branch_reaches_every_level_through_tasks_it_cannot_count()
{
    var a = T(); var b = T(); var c = T(); var d = T();
    b.ParentTaskId = a.Id; c.ParentTaskId = b.Id; d.ParentTaskId = a.Id;
    var idsByParent = new[] { a, b, c, d }.ToLookup(t => t.ParentTaskId, t => t.Id);
    var countable = new[] { a, c, d }.ToDictionary(t => t.Id);
    Assert.Equal(new[] { c.Id, d.Id }.Order(), BranchSummaryRules.BranchOf(a.Id, idsByParent, countable).Select(t => t.Id).Order());
}

/// TREE-010c: a loop in the ids doesn't hang, and the parent never counts in its own branch.
[Fact] public void A_loop_ends_and_leaves_out_the_parent()
{
    var a = T(); var b = T();
    a.ParentTaskId = b.Id; b.ParentTaskId = a.Id;
    var idsByParent = new[] { a, b }.ToLookup(t => t.ParentTaskId, t => t.Id);
    Assert.Equal([b.Id], BranchSummaryRules.BranchOf(a.Id, idsByParent, new[] { a, b }.ToDictionary(t => t.Id)).Select(t => t.Id));
}

/// TREE-011
[Fact] public void Priority_chips_are_the_open_subtasks_distinct_highest_first()
{
    var s = Summ([T(p: TaskPriority.Low), T(Done, TaskPriority.Critical), T(p: TaskPriority.High), T(p: TaskPriority.Low), T(Cancelled, TaskPriority.Critical)]);
    Assert.Equal([TaskPriority.High, TaskPriority.Low], s.Priorities);
}

/// TREE-012
[Fact] public void Sprint_chips_follow_sprint_dates_then_backlog_and_not_planned()
{
    var s10 = new Sprint { Name = "Sprint 10", StartDate = new(2026, 10, 5) };
    var s12 = new Sprint { Name = "Sprint 12", StartDate = new(2026, 11, 2) };
    var s14 = new Sprint { Name = "Sprint 14", StartDate = new(2026, 11, 30) };
    var s = Summ([T(sprint: s14), T(sprint: s12), T(sprint: s14), T(backlog: true), T(), T(Done, sprint: s10)]);
    Assert.Equal(["Sprint 12", "Sprint 14"], s.Sprints.Select(x => x.Name));
    Assert.True(s.OnBacklog);
    Assert.True(s.NotPlanned);
    var planned = Summ([T(sprint: s12)]);
    Assert.False(planned.OnBacklog);
    Assert.False(planned.NotPlanned);
}

/// TREE-013
[Fact] public void Due_range_comes_from_open_subtasks()
{
    var s = Summ([T(due: new(2026, 11, 20)), T(due: new(2026, 11, 12)), T(), T(Done, due: new(2026, 10, 1))]);
    Assert.Equal(new DateOnly(2026, 11, 12), s.DueFrom);
    Assert.Equal(new DateOnly(2026, 11, 20), s.DueTo);
    Assert.False(s.Overdue);
    var late = Summ([T(due: new(2026, 11, 8)), T(due: new(2026, 11, 8))]);
    Assert.Equal(late.DueFrom, late.DueTo);
    Assert.True(late.Overdue);
    var none = Summ([T(Done, due: new(2026, 11, 1))]);
    Assert.Null(none.DueFrom);
    Assert.False(none.Overdue);
}

/// TREE-014
[Fact] public void Estimate_total_counts_done_work_and_leaves_out_cancelled()
{
    var s = BranchSummaryRules.Summarise(T(est: 120), [T(est: 240), T(Done, est: 480), T(Cancelled, est: 600), T()], Today)!;
    Assert.Equal(840, s.EstimateTotal);
    Assert.Equal(120, s.OwnEstimate);
    Assert.Equal(720, s.SubtasksEstimate);
    Assert.Null(BranchSummaryRules.Summarise(T(est: 120), [T(), T(Cancelled, est: 60)], Today)!.EstimateTotal);
    Assert.Equal(30, BranchSummaryRules.Summarise(T(), [T(est: 30)], Today)!.EstimateTotal);
}

/// TREE-015
[Fact] public void Assignees_are_the_distinct_people_on_open_subtasks()
{
    var ann = Assigned.Person(G(), "Ann"); var bob = Assigned.Person(G(), "Bob"); var cy = Assigned.Person(G(), "Cy");
    var s = Summ([T(people: [bob, ann]), T(people: [ann]), T(Done, people: [cy])]);
    Assert.Equal(["Ann", "Bob"], s.Assignees.Select(u => u.DisplayName));
}

/// TREE-016
[Fact] public void No_subtasks_means_no_summary_and_the_open_flag_is_right()
{
    Assert.Null(BranchSummaryRules.Summarise(T(), [], Today));
    Assert.True(Summ([T(Waiting), T(Done)]).HasOpenSubtasks);
    Assert.False(Summ([T(Done), T(Cancelled)]).HasOpenSubtasks);
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~BranchSummaryRulesTests"`
Expected: build error, `BranchSummaryRules` not found.

- [ ] **Step 3: Implement `BranchSummaryRules.cs`, `TaskTreeModels.cs` and `TaskTreeRules.Entries`**

- `BranchOf`: walk `idsByParent` breadth-first from `parentId` with a visited set (the parent itself pre-visited) and a `TaskTreeRules.MaxDepth` level cap; return the visited ids found in `countable`, as tasks. Every id is walked whether countable or not.
- `Summarise` returns null for an empty branch. Otherwise, per spec §4.2: `Statuses` over every branch task, ordered by the enum (`Todo` … `Cancelled`), zero counts left out; `Priorities`, `Sprints`, `OnBacklog`, `NotPlanned`, `DueFrom`/`DueTo`/`Overdue` and `Assignees` over open tasks only (`IsOpen`); `Sprints` from each open task's loaded `Sprint`, distinct by `Id`, ordered by `StartDate` then `Name`; `OnBacklog` / `NotPlanned` from `SprintPlacement.Of(task)`; `Overdue` when `DueFrom < today`; `Assignees` distinct by `Id`, ordered by `DisplayName`; `SubtasksEstimate` sums `EstimateMinutes ?? 0` over tasks not `Cancelled`; `EstimateTotal` is `OwnEstimate + SubtasksEstimate` when any non-cancelled branch task has an estimate, else null; `OwnEstimate` is `parent.EstimateMinutes ?? 0`.
- `Entries`: one `TaskTreeEntry` per row whose id is in `tasks`, in row order, with `TitleOnly = titleOnly(task)` and `Summary = titleOnly(task) ? null : summary(task)`. Rows whose task is missing are skipped.

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~BranchSummaryRulesTests|FullyQualifiedName~TaskTreeRulesTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Orbit.Web/Application/BranchSummaryRules.cs Orbit.Web/Application/Models/TaskTreeModels.cs Orbit.Web/Application/TaskTreeRules.cs Orbit.Tests/Tasks/BranchSummaryRulesTests.cs
git commit -m "Branch summary rules and tree entries (TREE-010 to TREE-016)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Tree mode in the task table, with collapse memory

**Files:**
- Create: `Orbit.Web/Components/Infrastructure/BrowserOwner.cs`
- Create: `Orbit.Web/Components/Infrastructure/BrowserTreeMemory.cs`
- Create: `Orbit.Web/Components/Tasks/BranchSummaryCell.razor`
- Modify: `Orbit.Web/Components/Infrastructure/BrowserFilterMemory.cs` (its private `OwnerAsync` → `BrowserOwner`)
- Modify: `Orbit.Web/Components/Tasks/TaskTable.razor`
- Modify: `Orbit.Web/wwwroot/js/orbit-interop.js`, `Orbit.Web/wwwroot/css/app.css`, `Orbit.Web/Program.cs:138`
- Test: `Orbit.Tests/Tasks/BrowserTreeMemoryTests.cs`

**Interfaces:**
- Consumes: `TaskTreeEntry`, `BranchSummary`, `StatusCount` (Task 2); `TaskTreeRules.Hidden`, `TaskTreeRules.Indent` (Task 1); `TaskLook.Status`, `TaskLook.Priority`, `TimeFormat.Minutes`, `Ui.Day`, `SprintPlacement.BacklogLabel` / `NotPlannedLabel`.
- Produces:
  ```csharp
  // Orbit.Components.Infrastructure
  internal static class BrowserOwner { public static Task<string> IdAsync(AuthenticationStateProvider authentication); }
  public sealed class BrowserTreeMemory(IJSRuntime js, AuthenticationStateProvider authentication)
  {
      public const int Limit = 1000;
      public Task<HashSet<Guid>> CollapsedAsync();
      public Task SetAsync(IEnumerable<Guid> taskIds, bool collapsed);
      public static IReadOnlyList<Guid> Next(IReadOnlyList<Guid> current, IEnumerable<Guid> taskIds, bool collapsed, int limit = Limit);
  }
  // Orbit.Components.Tasks: TaskTable gains
  [Parameter] public IReadOnlyList<TaskTreeEntry>? Tree { get; set; }   // Tasks loses [EditorRequired]; a page passes Tasks or Tree
  // BranchSummaryCell
  public enum BranchColumn { Priority, Assignee, Due, Estimate, Sprint, Status }
  [Parameter, EditorRequired] public BranchSummary Summary { get; set; }
  [Parameter, EditorRequired] public BranchColumn Column { get; set; }
  ```

- [ ] **Step 1: Write the failing tests**

`Orbit.Tests/Tasks/BrowserTreeMemoryTests.cs`, class `BrowserTreeMemoryTests`:

```csharp
private static Guid G() => Guid.NewGuid();

/// TREE-020a: collapsing adds each id once, the most recent last.
[Fact] public void Collapsing_adds_each_id_once_most_recent_last()
{
    Guid a = G(), b = G(), c = G();
    Assert.Equal([b, a], BrowserTreeMemory.Next([a, b], [a], collapsed: true));
    Assert.Equal([a, b, c], BrowserTreeMemory.Next([a, b], [c], collapsed: true));
}

/// TREE-020b (Review Focus 5): expanding removes only the ids given.
[Fact] public void Expanding_removes_only_the_ids_given()
{
    Guid a = G(), b = G(), c = G();
    Assert.Equal([a, c], BrowserTreeMemory.Next([a, b, c], [b], collapsed: false));
}

/// TREE-020c (Review Focus 5): the list keeps the newest ids up to the limit.
[Fact] public void The_list_keeps_the_newest_ids_up_to_the_limit()
{
    var ids = Enumerable.Range(0, 5).Select(_ => G()).ToArray();
    Assert.Equal(ids[2..], BrowserTreeMemory.Next(ids[..4], [ids[4]], collapsed: true, limit: 3));
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~BrowserTreeMemoryTests"`
Expected: build error, `BrowserTreeMemory` not found.

- [ ] **Step 3: Implement `BrowserOwner`, `BrowserTreeMemory` and `orbit.tree`**

- `BrowserOwner.IdAsync` is `BrowserFilterMemory.OwnerAsync`'s body moved out (the user id as `"N"`, or `"-"`); `BrowserFilterMemory` calls it.
- `Next`: collapsing removes the given ids from `current` then appends them (given order), then keeps the last `limit`; expanding removes them.
- `CollapsedAsync` calls `orbit.tree.read(owner)` (string ids; unparsable ones skipped). `SetAsync` reads, applies `Next`, and writes with `orbit.tree.write(owner, ids)`.
- Add to `orbit-interop.js`, after `window.orbit.filters`:

  ```js
  // Collapsed parents in the tree view (spec §6.15, BrowserTreeMemory.cs): task ids per user in localStorage, so they outlast the session.
  window.orbit.tree = {
    read: function (owner) {
      try { var raw = localStorage.getItem('orbit-tree-' + owner); return raw ? JSON.parse(raw) : []; } catch (e) { return []; }
    },
    write: function (owner, ids) {
      try { localStorage.setItem('orbit-tree-' + owner, JSON.stringify(ids)); } catch (e) { /* storage unavailable */ }
    }
  };
  ```
- Register `builder.Services.AddScoped<BrowserTreeMemory>();` beside `BrowserFilterMemory` in `Program.cs`.

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~BrowserTreeMemoryTests"`
Expected: 3 pass.

- [ ] **Step 5: Implement `BranchSummaryCell.razor`**

A `<div class="orbit-branch">` holding, per `Column`: **Status** one small chip per `StatusCount` (`"2 Done"`, colour `TaskLook.Status`); **Priority** one small chip per priority (colour `TaskLook.Priority`, outlined for Low as the row chip is); **Sprint** one small outlined chip per sprint name, then `SprintPlacement.BacklogLabel` and `SprintPlacement.NotPlannedLabel` chips when flagged; **Due** `Ui.Day(DueFrom)` or `"{from} → {to}"` when they differ, with class `orbit-overdue` when `Overdue`; **Estimate** `"{TimeFormat.Minutes(total)} total"` with `title="{own} on this task, {subtasks} on its subtasks"` (both through `TimeFormat.Minutes`); **Assignee** the names comma-separated. Render nothing when the column has nothing (empty list, null `DueFrom`, null `EstimateTotal`).

- [ ] **Step 6: Add the tree mode to `TaskTable.razor`**

- `[Inject] BrowserTreeMemory TreeMemory`; `HashSet<Guid>? _collapsed`, loaded in `OnParametersSetAsync` the first time `Tree` is non-null. While `Tree` is set and `_collapsed` is still null, render nothing (no flash of expanded rows).
- Rows drawn: `Tasks` as today when `Tree` is null; otherwise the `Tree` entries not in `TaskTreeRules.Hidden(Tree.Select(e => (e.Task.Id, e.Depth)).ToList(), _collapsed)`. The empty message, `Selectables` and `CandidatesFor` work over the drawn tasks.
- **Above the table** when any entry `HasChildren`: `<div class="orbit-tree-tools">` with text buttons **Expand all** and **Collapse all**, acting on the `HasChildren` entries of `Tree` via `TreeMemory.SetAsync(ids, false|true)` and updating `_collapsed`.
- **Task cell** in tree mode: wrap its content in `<div class="orbit-tree-cell" style="--orbit-indent: @TaskTreeRules.Indent(entry.Depth)">`. First a `MudIconButton` (small; `Icons.Material.Filled.ExpandMore` when open, `ChevronRight` when collapsed; `aria-label` and `title` "Collapse {Number}" / "Expand {Number}") when `HasChildren`, otherwise a `<span class="orbit-tree-spacer"></span>` of the same width. Then the existing content, plus `<span class="orbit-branch-count" title="Subtasks in its branch">(@Summary.SubtaskCount)</span>` after the title when `Summary` is set. Toggling calls `TreeMemory.SetAsync([id], collapsed)` and updates `_collapsed`; no `Changed` callback (no reload).
- The `in: Parent` line is not drawn in tree mode.
- **Context rows** (`IsContext`): `<tr>` gets class `orbit-context` (with `RowClass`'s), and the row is drawn as `ReadOnly` would draw it: no inline control, no Today tick, no Take button.
- **Title-only rows** (`TitleOnly`): only the number and the unlinked title in the Task cell; every other cell empty.
- **Summary lines**: when the entry has a `Summary`, add `<BranchSummaryCell Summary="..." Column="BranchColumn.X" />` at the end of the Priority, Assignee, Due, Est. (when `ShowEstimate`), Sprint (when `ShowSprint`, after `SprintCell` or the control) and Status cells.
- Update the component's top comment: the tree mode (spec §6.15, decision 145).

- [ ] **Step 7: Add the styles to `app.css`** (beside the other `.orbit-tasks` rules)

Class names: `.orbit-tree-cell` (flex row, items start, gap `.25rem`, `padding-left: calc(var(--orbit-indent, 0) * 1.25rem)`, and `.75rem` per level at `max-width: 600px`); `.orbit-tree-spacer` (the small icon button's width, `flex: none`); `tr.orbit-context td` (opacity `.6`); `.orbit-branch` (margin-top `.125rem`, `font-size: .8rem`, secondary text colour, `display: flex; flex-wrap: wrap; gap: .25rem`); `.orbit-branch-count` (secondary text colour, margin-left `.25rem`); `.orbit-tree-tools` (flex, end-aligned, gap `.5rem`).

- [ ] **Step 8: Build and run every test**

Run: `dotnet build Orbit.slnx -c Release` then `dotnet test Orbit.slnx -c Release`
Expected: build succeeds with no new warnings; all tests pass (the flat mode is unchanged, so no page changes yet).

- [ ] **Step 9: Commit**

```bash
git add Orbit.Web/Components Orbit.Web/wwwroot Orbit.Web/Program.cs Orbit.Tests/Tasks/BrowserTreeMemoryTests.cs
git commit -m "Task table tree mode, branch summary cells and collapse memory (TREE-020)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The Tasks page as a tree

**Files:**
- Modify: `Orbit.Web/Application/Services/TaskService.cs:32-74` (`ListAsync`) and add `ListTreeAsync`
- Modify: `Orbit.Web/Components/Pages/Tasks/TasksList.razor`
- Modify: `orbit-spec.md` (§6.2, §6.15, §13 item 145, §14 item 129), `README.md`

**Interfaces:**
- Consumes: `TaskTreeNode`, `TaskTreeRules.Build` / `Cut` / `Entries` / `PageRows` / `MaxDepth` (Task 1-2); `BranchSummaryRules.BranchOf` / `Summarise`; `TaskTreePage`, `TaskTreeEntry`; `TaskTable.Tree` (Task 3); `Scoping.Tasks`, `AccessPolicy.CanViewTask`.
- Produces: `public async Task<TaskTreePage> ListTreeAsync(TaskFilter f, CancellationToken ct = default)` on `TaskService`.

- [ ] **Step 1: Implement `ListTreeAsync`**

- Move `ListAsync`'s filtering (from `Scope(...)` through `Matching(...)`) into `private IQueryable<TaskItem> Filtered(IQueryable<TaskItem> q, Actor actor, TaskFilter f)`; `ListAsync` becomes `WithIncludes(Filtered(db.Tasks.AsNoTracking(), actor, f))` with its ordering and paging unchanged.
- `ListTreeAsync`, following spec §5.2:
  1. Matches: `Filtered(db.Tasks.AsNoTracking(), actor, f)` projected to `new TaskTreeNode(t.Id, t.ParentTaskId, true, t.Status == TaskItemStatus.Done || t.Status == TaskItemStatus.Cancelled, t.DueDate, t.Priority, t.CreatedAt)`, unpaged.
  2. Ancestors: while some node's `ParentId` isn't among the nodes (at most `TaskTreeRules.MaxDepth` rounds), load those ids from `db.Tasks` **unscoped**, projected the same way with `Matched: false`.
  3. `rows = TaskTreeRules.Build(nodes)`; `cut = TaskTreeRules.Cut(rows, f.Page, TaskTreeRules.PageRows)` (`f.PageSize` is not used by the tree).
  4. Full rows: `WithIncludes(db.Tasks.AsNoTracking()).Where(t => pageIds.Contains(t.Id))`, as a dictionary.
  5. Summaries: from the page's ids, gather descendant `(Id, ParentTaskId)` pairs level by level from `db.Tasks` **unscoped** (at most `MaxDepth` rounds; Review Focus 1), then `countable` = `Scoping.Tasks(db.Tasks.AsNoTracking().Include(t => t.Sprint), actor)` restricted to those ids, as a dictionary (assignees and their users are auto-included). `idsByParent` = the pairs as a lookup.
  6. `entries = TaskTreeRules.Entries(cut.Rows, byId, titleOnly: t => !AccessPolicy.CanViewTask(actor, t), summary: t => BranchSummaryRules.Summarise(t, BranchSummaryRules.BranchOf(t.Id, idsByParent, countable), today))` with `today = DateOnly.FromDateTime(DateTime.UtcNow)`. Note a matched row is always viewable, so only context rows can be title-only.
  7. Return `new TaskTreePage(entries, cut.Page, cut.PageCount, cut.MatchCount)`.
- Doc comment cites spec §6.2, §6.15 and decision 145.

- [ ] **Step 2: Switch `TasksList.razor` to the tree**

- `PagedResult<TaskItem>? _result` → `TaskTreePage? _tree`, filled by `ListTreeAsync(filter)`; `_waiting` from `GetWaitingAsync(_tree.Rows.Select(r => r.Task))`.
- Header count: `_tree.MatchCount`. Paging footer: `_tree.Page`, `_tree.PageCount`, `HasPrevious`, `HasNext`, `PageUrl(...)` as now.
- `TaskTable`: `Tree="_tree.Rows"` instead of `Tasks`; `ShowDepartment="TaskLook.MixesDepartments(_actor, _tree.Rows.Select(r => r.Task))"`; every other parameter unchanged.
- Update the page's top comment (tree, decision 145).

- [ ] **Step 3: Build and run every test**

Run: `dotnet build Orbit.slnx -c Release` then `dotnet test Orbit.slnx -c Release`
Expected: build succeeds; all tests pass.

- [ ] **Step 4: Update the spec and README**

- §6.2: a new bullet after *Quick edit from task lists*, **Tree view**: subtasks under their parent on the Tasks list and the project page, the toggle and `(n)`, collapse remembered per browser and user (`orbit-tree-{owner}` in local storage, at most 1000 ids), Expand all / Collapse all, context rows under a filter, order by first match, pages of whole branches up to 50 rows, the header counting matches, and the summary line (point to §6.15).
- §6.15: rewrite the *Task tables* bullet under **Where it shows** (the Tasks list and project page show the tree; Planning, sprint and Today pages stay flat with `in: Parent`). Amend **No roll-up**: stored values are never rolled up; the Tasks list and project page show a display-only **branch summary** under a parent's own values (the column table of the design spec §4.2, condensed to one sentence per column). Add an **Acceptance** line listing TREE-001 to TREE-006, TREE-010 to TREE-016 and TREE-020 with one clause each, in the style of the other acceptance lines.
- §13: item **145**, *Subtasks under their parent on the Tasks list and project page (§6.1, §6.2, §6.15)*, asked as "It is difficult to visualise subtasks when there are many tasks in the task list". Record what was decided and why: display-only summary, not stored (the container design of `feature/parent-task-rollup` was dropped because the parent must stay a normal task with its own time, comments and values); own values with inline controls plus a summary line (option B); chips for status (with counts), priority and sprint, a due range, an estimate total, assignees; filter option A with context rows; ordering by first match; whole-branch paging; local storage for collapse; Done/Cancelled greyed on a parent with open subtasks (added in Task 6).
- §14: item **129**, in the style of items 124 to 128: `TaskTreeRules` and `BranchSummaryRules` with TREE tests → `TaskTable` tree mode, `BranchSummaryCell`, `BrowserTreeMemory` → `TaskService.ListTreeAsync` and the Tasks list → the project page → Done/Cancelled greyed → §6.1, §6.2, §6.15 and the README. No migration.
- `README.md`: in the opening Blazor paragraph or after the task table paragraph near line 215, a short paragraph: subtasks sit under their parent on the Tasks list and project page; ▸/▾ collapses a branch and is remembered in the browser; Expand all / Collapse all; a filter shows a match's parents greyed; a parent's summary line shows its subtasks' statuses, priorities, sprints, due range, total estimate and people, while its own values stay editable.

- [ ] **Step 5: Run the app and check the Tasks page**

Run: `dotnet run --project Orbit.Web -c Release` and open `/Tasks` (dev data has at least one parent with an open subtask; add subtasks with *Add subtask* to get two levels and a mixed branch).
Expected: subtasks under parents, indented; the toggle and `(n)`; the summary line under the parent's own controls; a filter (e.g. Assignee = a subtask's assignee) shows the parent greyed with no controls; paging footer and count right; no browser console errors.

- [ ] **Step 6: Check the collapse behaviour** (Review Focus 3 and 4)

Collapse a parent, change a due date inline on another row (the page reloads its data): the parent stays collapsed. Navigate to the dashboard and back, then restart the browser: still collapsed. Filter so a matched parent's subtasks are all hidden: it shows `(n)` and its summary but no toggle. Collapse all then Expand all on page 1 leaves page 2's collapsed parents alone.

- [ ] **Step 7: Commit**

```bash
git add Orbit.Web/Application/Services/TaskService.cs Orbit.Web/Components/Pages/Tasks/TasksList.razor orbit-spec.md README.md
git commit -m "Tasks list shows subtasks under their parent with branch summaries (decision 145)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The project page as a tree

**Files:**
- Modify: `Orbit.Web/Components/Pages/Projects/ProjectDetails.razor:216-227` (markup) and `:308-356` (`ReloadAsync`)
- Modify: `orbit-spec.md` (§6.1), `README.md` if the Task 4 paragraph doesn't already name the project page

**Interfaces:**
- Consumes: `TaskTreeRules.NodesFor` / `Build` / `Entries`, `BranchSummaryRules.BranchOf` / `Summarise`, `TaskTreeEntry`, `TaskTable.Tree`.
- Produces: nothing new.

- [ ] **Step 1: Build the tree in `ReloadAsync`**

Keep the three filters as they are, but collect the matching ids (`HashSet<Guid>`) instead of the ordered list; drop the page's own `OrderBy` (the rules order it). Then, with `all = project.Tasks.ToList()` (`ICollection` doesn't convert to `IReadOnlyCollection`):
`nodes = TaskTreeRules.NodesFor(all, t => matchIds.Contains(t.Id))`; `rows = TaskTreeRules.Build(nodes)`; `byId = all.ToDictionary(t => t.Id)`; `idsByParent = all.ToLookup(t => t.ParentTaskId, t => t.Id)`; `_tree = TaskTreeRules.Entries(rows, byId, titleOnly: _ => false, summary: t => BranchSummaryRules.Summarise(t, BranchSummaryRules.BranchOf(t.Id, idsByParent, byId), today))`. Every project task counts in a branch (spec §4.1), and context rows show their values (spec §3.2). `_waiting` from `_tree.Select(e => e.Task)`.

- [ ] **Step 2: Use the tree in the markup**

The Tasks card header shows `(@_matchCount of @p.Tasks.Count)` where `_matchCount = matchIds.Count`; `TaskTable` takes `Tree="_tree"` instead of `Tasks="_tasks"`, other parameters unchanged.

- [ ] **Step 3: Build and run every test**

Run: `dotnet build Orbit.slnx -c Release` then `dotnet test Orbit.slnx -c Release`
Expected: build succeeds; all tests pass.

- [ ] **Step 4: Update the spec**

§6.1, the *Project detail page* bullet: its task table is the tree of §6.2 / §6.15 (same order, context rows and summary line, no paging; every task on the project counts in a branch). Make sure §13 item 145 and §14 item 129 mention the project page.

- [ ] **Step 5: Run the app and check a project page**

Open `/Projects/Details/{id}` for a project with a parent and subtasks, including a cross-department subtask.
Expected: the tree and summary line as on the Tasks page; the Status, Assignee and Department filters show greyed parents with their values; a parent collapsed on the Tasks page is collapsed here too.

- [ ] **Step 6: Commit**

```bash
git add Orbit.Web/Components/Pages/Projects/ProjectDetails.razor orbit-spec.md README.md
git commit -m "Project page shows subtasks under their parent with branch summaries (decision 145)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Done and Cancelled greyed on a parent with open subtasks

**Files:**
- Modify: `Orbit.Web/Application/BranchSummaryRules.cs` (add `ClosingBlocked`)
- Modify: `Orbit.Web/Components/Tasks/TaskStatusSelect.razor`, `Orbit.Web/Components/Tasks/TaskTable.razor`
- Modify: `orbit-spec.md` (§6.15 *Closing a parent*, the acceptance line, §13 item 145)
- Test: `Orbit.Tests/Tasks/BranchSummaryRulesTests.cs`

**Interfaces:**
- Consumes: `BranchSummary.HasOpenSubtasks` (Task 2), `TaskTreeEntry.Summary` in `TaskTable` (Task 3).
- Produces:
  ```csharp
  public static bool ClosingBlocked(TaskItemStatus option, TaskItemStatus current, BranchSummary? summary); // BranchSummaryRules
  public const string ClosingBlockedReason = "Close its open subtasks first.";                              // BranchSummaryRules
  [Parameter] public BranchSummary? Branch { get; set; }                                                    // TaskStatusSelect
  ```

- [ ] **Step 1: Write the failing test**

Add to `BranchSummaryRulesTests`:

```csharp
/// TREE-017: Done and Cancelled are blocked while a counted subtask is open; the current value and the open statuses never are.
[Fact] public void Done_and_cancelled_are_blocked_while_a_subtask_is_open()
{
    var open = Summ([T(InProgress)]);
    Assert.True(BranchSummaryRules.ClosingBlocked(Done, InProgress, open));
    Assert.True(BranchSummaryRules.ClosingBlocked(Cancelled, InProgress, open));
    Assert.False(BranchSummaryRules.ClosingBlocked(Waiting, InProgress, open));
    Assert.False(BranchSummaryRules.ClosingBlocked(Done, Done, open));
    Assert.False(BranchSummaryRules.ClosingBlocked(Done, InProgress, Summ([T(Done)])));
    Assert.False(BranchSummaryRules.ClosingBlocked(Done, InProgress, null));
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~BranchSummaryRulesTests"`
Expected: build error, `ClosingBlocked` not found.

- [ ] **Step 3: Implement `ClosingBlocked` and the reason constant**

True only when `option.IsClosed()`, `option != current` and `summary?.HasOpenSubtasks == true`.

- [ ] **Step 4: Run it to see it pass**

Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~BranchSummaryRulesTests"`
Expected: all pass.

- [ ] **Step 5: Disable the items in the control**

`TaskStatusSelect`: each `MudSelectItem` gets `Disabled="BranchSummaryRules.ClosingBlocked(s, Task.Status, Branch)"` and, when disabled, `title="@BranchSummaryRules.ClosingBlockedReason"`. `TaskTable` passes `Branch="entry?.Summary"` in tree mode (null in flat mode, so other pages are unchanged). The service check stays as the backstop.

- [ ] **Step 6: Update the spec**

§6.15 *Closing a parent*: add that on the Tasks list and project page the inline status control shows Done and Cancelled disabled, with the tooltip *Close its open subtasks first.*, while a subtask the page counts is open; the server rule is unchanged. Add TREE-017 to the acceptance line; make sure §13 item 145 records it.

- [ ] **Step 7: Build, test, and check in the app**

Run: `dotnet build Orbit.slnx -c Release`, `dotnet test Orbit.slnx -c Release`, then `dotnet run --project Orbit.Web -c Release`.
Expected: all tests pass. On `/Tasks`, a parent with an open subtask lists Done and Cancelled greyed with the tooltip; once its subtasks are closed they can be picked; the task page's own status control is unchanged.

- [ ] **Step 8: Commit**

```bash
git add Orbit.Web/Application/BranchSummaryRules.cs Orbit.Web/Components/Tasks/TaskStatusSelect.razor Orbit.Web/Components/Tasks/TaskTable.razor Orbit.Tests/Tasks/BranchSummaryRulesTests.cs orbit-spec.md
git commit -m "Grey out Done and Cancelled on a parent with open subtasks (TREE-017)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
