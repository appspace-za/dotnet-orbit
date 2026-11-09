# Task tree view with branch summaries

Date: 2026-11-09. Status: design approved in conversation, awaiting review of this written spec.
Branch: `feature/task-tree-view`. Spec sections affected: §6.1, §6.2, §6.15. Decision log entry: 145.

## 1. Problem and goal

With many tasks in the list it is hard to see which tasks are subtasks of what. The goal:

- On the **Tasks page** (`/Tasks`) and a **project's page** (`/Projects/Details/{id}`), every subtask is shown directly under
  its parent, indented one step per level.
- A parent can be **collapsed**, which hides all of its descendants. The collapsed state survives navigation.
- A task with no subtasks looks and behaves exactly as it does today.
- A parent row shows, under its own values, a **summary of its branch**: the distinct statuses, priorities and sprints of its
  subtasks, the range of their due dates, the total estimate and the people working on them.

### What this is not

An earlier attempt (`feature/parent-task-rollup`, not merged) turned a parent into a container: it stored rolled-up values
in the parent's own columns and refused edits, time and planning on it. That is **not** this design. Here:

- **Nothing is stored.** The branch summary is worked out when a page draws it. No migration, no new column, no save hook.
- **A parent is a normal task.** Its own status, priority, due date, estimate, sprint and assignees stay its own and stay
  editable, inline and on the task page. It can have time clocked and logged, comments, attachments and dependencies, can be
  planned into a sprint and ticked for Today.
- The one parent rule is the existing one (§6.15): a parent can't be set Done or Cancelled while any subtask is open
  (`TaskStructureService.EnsureStatusChangeAllowedAsync`). It is unchanged.
- Counts, the dashboard, reports, the MCP tools and the critical path are unchanged: every task is still counted once,
  parents included.

## 2. Rows

- Each subtask sits directly under its parent, indented one step per level. The indent stops growing after **6 levels** so a
  deep tree still fits on a narrow screen; deeper rows are still listed in order.
- A parent row carries a ▸/▾ **toggle** before its number, and the number of subtasks in its branch in brackets, e.g. `(3)`.
  That number is the size of the whole branch (children, grandchildren and so on) that the page may show (§4.1), so it
  equals the sum of the status chip counts (§4.2).
- On these two pages the `in: Parent` line under a subtask's title is not shown, since the parent is the row above. Every
  other task table keeps it.
- A row for a task with no subtasks is drawn exactly as today.

## 3. Collapse, filters, order and paging

### 3.1 Collapse

- Every parent starts **expanded**. Collapsing a parent hides its whole branch, at every depth.
- The set of collapsed task ids is kept in the browser's **local storage**, keyed by user (like the filter memory's owner),
  so it survives navigation and a browser restart. One set covers both pages: a parent collapsed on the Tasks page is
  collapsed on its project's page too.
- The set keeps at most about **1,000** ids; adding one beyond that drops the oldest.
- **Expand all** and **Collapse all** buttons sit above the table. Collapse all collapses every parent on the current page;
  Expand all expands them.
- Toggling re-renders the table only. The page's rows are already loaded, so it costs no database call.
- Pages render without prerendering (`App.razor`), so the set is read through JS interop before the table is first drawn
  and collapsed branches don't flash open.

### 3.2 Filters

- A filter selects tasks exactly as it does today (the same `TaskFilter` on the Tasks page; the same Status, Assignee and
  Department filters on the project page).
- Each **match** is shown under its full chain of ancestors.
- An ancestor that did not match is shown as a greyed **context row** with no controls. It shows what the page would show
  for that task on a normal row, read-only:
  - **Tasks page:** if the viewer may see the task (`AccessPolicy.CanViewTask`), its values are shown and its title links to
    it. Otherwise only its number and title are shown, unlinked, which is what today's `in: Parent` line already reveals.
  - **Project page:** its values are always shown, as on any row of that page, and its title links to it only when the
    viewer may open it.
- Descendants that did not match are hidden.
- The existing *Subtasks of one task* drill-down (`ParentTaskId`) follows the same rule: the children are the matches and the
  parent is shown above them as a context row.

### 3.3 Order (both pages)

- A **branch** (a top-level task and everything shown under it) sits where its **first matching row** would sit in today's
  order: open before closed, then due date (none last), then priority (highest first), then newest first.
- Rows inside a branch, among siblings, follow the same order.

### 3.4 Paging and count (Tasks page)

- A page holds **whole branches**. Branches are taken in order while the page's row count stays within **50**, counting
  context rows and every row of a branch whether expanded or not. A branch is never split across pages; a branch that alone
  has more than 50 rows gets a page of its own.
- Collapsing never moves page boundaries.
- The header counts **matching** tasks only; context rows are not counted.
- The project page has no paging, as today.

### 3.5 Status control on a parent

- While a parent has open subtasks, **Done** and **Cancelled** are shown greyed out (disabled) in its inline status control,
  with a tooltip: *"Close its open subtasks first."*
- The service still enforces the rule, so a subtask the viewer can't see, or a change made meanwhile, still gives today's
  error message.

## 4. The branch summary line

### 4.1 Which tasks count

- The parent's **whole branch**: its children, their children and so on.
- Only tasks the page may show:
  - **Tasks page:** tasks inside `Scoping.Tasks` for the viewer, i.e. what the unfiltered Tasks list would show them.
  - **Project page:** every task on the project, which is what that page already lists (it shows every row's values and
    only withholds the title link from a task the viewer can't open).
- Tasks hidden by the **current filter** still count, so filtering never changes a summary.
- A parent none of whose subtasks the page may show is drawn as a task with no subtasks: no toggle, no summary line.

### 4.2 What each cell shows

The summary is a second line inside each cell of the parent row, in the existing small-print style (like the `3h left`
line under Est.). "Subtasks" below means the counted tasks of §4.1, excluding the parent itself unless said otherwise.
"Open" means not Done and not Cancelled.

| Column | Shows | Over |
|---|---|---|
| Status | One chip per status present, with its count (`2 Done`, `1 In progress`), in workflow order (Todo, In progress, Waiting, Blocked, Done, Cancelled), coloured like the status badges (`TaskLook.Status`) | Every subtask, closed ones included |
| Priority | One chip per distinct priority, highest first, coloured like the priority chips | Open subtasks |
| Sprint | One chip per distinct sprint, ordered by sprint start date, then a *Backlog* chip and a *Not Planned* chip when an open subtask is there (`SprintPlacement`) | Open subtasks |
| Due | Earliest → latest due date (`12 Nov → 20 Nov`), one date when they are equal; the overdue colour when the earliest is before today | Open subtasks with a due date |
| Est. | `14h total`, with a tooltip `2h on this task, 12h on its subtasks` | The parent itself plus every subtask that isn't Cancelled (Done ones count: it is the branch's total work) |
| Assignee | The distinct people, by name, in small text | Open subtasks |

### 4.3 Empty cases

- A cell with nothing to show is blank on the summary line. Once every subtask is closed, for example, only the status
  chips and the estimate total remain.
- The estimate total is left out when no counted subtask has an estimate, since it would only repeat the parent's own.
- Project and Department cells have no summary line.

### 4.4 Where it appears

- On every parent row the viewer may see, context rows included.
- Not on a Tasks-page context row that shows only a number and title (§3.2).
- On a narrow screen the chips wrap onto further lines.

## 5. Components and data flow

### 5.1 Pure rules (`Orbit.Web/Application/`, namespace `Orbit.Application`, no DB access)

- **`TaskTreeRules`**
  - Input: nodes with id, parent id, the order fields of §3.3 and whether the node matched the filter.
  - Builds the forest, orders branches and siblings (§3.3), and returns ordered rows with depth, context flag and branch size.
  - Cuts pages from whole branches (§3.4) and reports the page count and the match count.
  - Guards against a loop or runaway depth with a visited set and a depth cap, as `GanttChart.Build` does. A node whose
    parent is not among the nodes is treated as a top-level row.
- **`BranchSummaryRules`**
  - Input: a parent and the tasks of its branch (§4.1), with today's date.
  - Returns a `BranchSummary`: status counts, priorities, sprint chips (sprints plus Backlog / Not Planned), due range and
    overdue flag, estimate total with the parent/subtasks split (or none), assignees, and whether any subtask is open.
  - Used by both pages, so the line reads the same everywhere.

### 5.2 Tasks page: `TaskService.ListTreeAsync(TaskFilter)`

1. Apply the filter and scoping exactly as `ListAsync` does, but select only id, parent id and the order fields, unpaged.
2. Load the matches' ancestors level by level (id, parent id, order fields), for the context rows.
3. Run `TaskTreeRules` and take the requested page.
4. Load full rows (today's `WithIncludes`) for that page's ids only.
5. Load the branches under that page's parent rows level by level, inside `Scoping.Tasks`, selecting only what the summary
   needs; run `BranchSummaryRules` for each parent.

It returns a **`TaskTreePage`**: the rows (task, depth, context flag, branch size, summary), page, page count and match
count. `ListAsync` stays as it is for `list_tasks` (MCP) and the other callers. The Gated badge's lookup
(`TaskStructureService.GetWaitingAsync`) runs on the page's rows as today.

### 5.3 Project page

No new service method. `ProjectService.GetAsync` already loads every task on the project, so `ProjectDetails` runs
`TaskTreeRules` (no paging) and `BranchSummaryRules` on them directly, after applying its filters as today.

### 5.4 Display

- **`TaskTable`** gets an optional **`Tree`** parameter (the ordered rows with depth, context flag, branch size and summary),
  with the collapsed set and a toggle callback. With it set, rows get the indent and toggle, context rows are greyed with no
  controls, and a parent row's cells get the summary line. Without it, the table renders exactly as today, so the Planning,
  sprint, Today and asset pages are untouched.
- Small components in `Components/Tasks/` draw the summary pieces: status chips with counts, plain chips (priority,
  sprint), the due range and the estimate total.
- **`TaskStatusSelect`** gets a parameter that disables Done and Cancelled with a reason (§3.5).
- **`BrowserTreeMemory`** (`Components/Infrastructure/`, beside `BrowserFilterMemory`) reads and writes the collapsed set
  through a new `orbit.tree` object in `wwwroot/js/orbit-interop.js`, using local storage keyed by user, with the cap of §3.1.
- Styles for the indent, toggle, context rows and summary line go in `wwwroot/css/app.css` as `orbit-*` classes.
- Inline edits on any row raise `Changed` and the page reloads its data, as today; the tree is rebuilt from the new data.

### 5.5 Unchanged

No migration, no stored field, no new permission, no MCP change. Counts, the dashboard, reports, the Gantt, the critical
path and the Planning, sprint and Today pages are unchanged. The closing rule stays in `TaskStructureService`.

## 6. Testing

Unit tests in `Orbit.Tests` (pure code only, per the project's convention), citing these acceptance IDs in their doc comments:

| ID | Behaviour |
|---|---|
| TREE-001 | A subtask is placed directly under its parent, one depth step per level |
| TREE-002 | A branch sits where its first match would be in today's order; siblings follow the same order |
| TREE-003 | Filtered: unmatched ancestors become context rows; unmatched descendants are hidden |
| TREE-004 | Paging never splits a branch; a branch over the limit gets its own page; the page count is right |
| TREE-005 | The match count leaves out context rows; a loop or an over-deep chain doesn't hang or overflow |
| TREE-010 | Status chips count every subtask at every level, in workflow order |
| TREE-011 | Priority chips: distinct, open subtasks only, highest first |
| TREE-012 | Sprint chips: by sprint start date, then Backlog and Not Planned, open subtasks only |
| TREE-013 | Due range from open subtasks; a single date when equal; the overdue flag |
| TREE-014 | Estimate total includes Done and excludes Cancelled, splits parent and subtasks, and is left out when no subtask has an estimate |
| TREE-015 | Assignees: the distinct people on open subtasks |
| TREE-016 | A parent with no countable subtasks has no summary; the open-subtask flag is right |

Checked by running the app (there are no database tests):

- Both pages, unfiltered and with filters, including a context row for a parent the viewer can't open.
- Collapse surviving navigation and a browser restart; Expand all and Collapse all; one state across both pages.
- Inline edits on a parent row, and Done / Cancelled disabled on a parent with open subtasks.
- Paging with a branch larger than one page.

## 7. Documentation

- `orbit-spec.md`:
  - §6.15: the *Task tables* bullet under *Where it shows* describes the tree on the Tasks and project pages; the *No
    roll-up* bullet stays true for stored values and notes the display-only branch summary.
  - §6.2 (the Tasks list) and §6.1 (the project page).
  - §13 Decisions Log: entry **145**, what was decided and why, including that the earlier container design was dropped.
  - §14 Suggested Build Order: a new step.
- `README.md`: the Tasks section (the tree, collapse and the summary line).

## 8. Build order

Each step can ship on its own; the spec and README are updated alongside each one.

1. `TaskTreeRules` and `BranchSummaryRules` with their tests.
2. `ListTreeAsync`, `TaskTable`'s tree mode and the Tasks page.
3. The project page.
4. Collapse memory, Expand all and Collapse all.
5. Done and Cancelled disabled on a parent with open subtasks.

## 9. Out of scope (could follow later)

- A tree on the Planning, sprint and Today pages.
- The collapsed state saved on the user's account rather than per browser.
- The branch summary in the MCP tools (`get_task`, `list_tasks`).
- Sortable columns.
