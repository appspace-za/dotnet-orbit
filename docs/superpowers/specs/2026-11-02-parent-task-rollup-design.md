# Parent tasks as containers: roll-up, tree view and counting

Date: 2026-11-02. Status: design approved in conversation, awaiting review of this written spec.

## 1. Problem and goal

With many tasks in a list it is hard to see which are subtasks of what, and every count and estimate sum counts a parent
and its subtasks both, so work is double counted. The goal:

- A task with subtasks becomes a **container**. Work is recorded on the subtasks; the parent's figures are rolled up from
  them. Comments, attachments and dependencies can still go on the parent.
- The **Tasks page** and the **project page** show subtasks directly under their parent, indented one step per level, with
  parents that collapse and stay collapsed across navigation.
- Every **count and estimate sum** in the app leaves parents out, so nothing is counted twice.
- A task with no subtasks looks and behaves exactly as it does today.

This reverses spec §6.15's "No roll-up" rule and decision 30's counting rule ("counts and reports keep counting every task
once, parents included"), and the part of decision 64 that records time on a parent and its subtask both.

## 2. Data model

- New column `TaskItem.HasSubtasks` (bool, not null, default false, indexed). True while at least one task has this task
  as its `ParentTaskId`. Tasks are never deleted, so it changes only when a subtask is attached or moved off.
- The rolled-up values are stored in the parent's **existing** columns, so every existing reader (filters, sorting, the
  dashboard, reports, the Gantt, the critical path, MCP, due-date emails) sees them without change: `Status`, `Priority`,
  `StartDate`, `DueDate`, `EstimateMinutes`, `SprintId`, `InBacklog`, `CompletedAt`, `FirstRespondedAt`.
- A parent has **no `TaskAssignment` rows**. Its assignees are shown, not stored (§4), so nobody gains edit rights on a parent
  (§6.5) through a subtask, and parents never receive assignment emails.

## 3. Roll-up rules (stored values)

A pure rule class, `ParentRollUpRules` (in `Application/`, no DB access), computes a parent's values from its **direct**
subtasks. A subtask that is itself a parent already holds its own rolled-up values, and every rule below composes, so the
result equals applying the rule over the whole tree.

**Cancelled subtasks are ignored** by every rule unless every subtask is cancelled.

### 3.1 Status

Evaluated top to bottom over the non-cancelled subtasks:

| Subtasks | Parent status |
|---|---|
| Every subtask is Cancelled | Cancelled |
| All Done | Done |
| All Todo | Todo |
| Every open one is Blocked | Blocked |
| Every open one is Waiting | Waiting |
| Every open one is Waiting or Blocked, mixed | Blocked |
| Anything else (any In Progress, or Todo mixed with Done, Waiting or Blocked) | In Progress |

"Open" means not Done and not Cancelled. Examples: {Done, Blocked} → Blocked; {Todo, Done} → In Progress;
{Todo, Blocked} → In Progress.

### 3.2 Other fields

| Field | Rule |
|---|---|
| Priority | Highest among open subtasks; if none is open, highest overall. |
| StartDate | Earliest `StartDate`; null if no subtask has one. |
| DueDate | Latest `DueDate`; subtasks without one are skipped; null if none has one. |
| EstimateMinutes | Sum of the subtasks' estimates; null if no subtask has one. |
| Sprint (`SprintId`, `InBacklog`) | The earliest sprint, by `Sprint.StartDate`, that an open subtask is in. If no open subtask is in a sprint: Backlog if any open subtask is on the Backlog, else Not Planned. If no subtask is open, the same rule over all non-cancelled subtasks. |
| CompletedAt | Stamped exactly as a manual status change stamps it today (`TaskService.ApplyStatus`): set to now on Done, cleared on any other status. |
| FirstRespondedAt | Set to now the first time the rolled-up status leaves Todo; never cleared. |

### 3.3 When it runs

- A **save hook** in `ApplicationDbContext`'s save path runs before the write. It finds tracked `TaskItem` entries that:
  were added with a parent; changed `Status`, `Priority`, `StartDate`, `DueDate`, `EstimateMinutes`, `SprintId` or
  `InBacklog` while having a parent; or changed `ParentTaskId` (both the old and the new parent are recalculated).
- For each affected parent it loads the parent's direct subtasks (and the sprints they are in), applies
  `ParentRollUpRules`, sets `HasSubtasks`, and writes any changed values. It then moves to that parent's parent, and stops
  when nothing changes or the top is reached.
- When a task's `HasSubtasks` goes false → true, its `TaskAssignment` rows are removed and any running clock on it is stopped,
  logging its time as the clock's own stop does today.
- Each parent whose values change gets an `Updated` audit row as **System** (`Actor.System`), with the parent's title as
  summary and a `ChangeSet` field-by-field diff marked `fromSubtasks: true`. Removed assignments are in the same diff.
- Derived changes send **no emails**.
- Known limit: two saves on sibling subtasks at the same moment could each recalculate the parent from what it saw. The next
  change under that parent, or the startup step (§3.4), corrects it.

### 3.4 Deploy and repair

- The migration adds `HasSubtasks` and fills it with SQL (`EXISTS` a child). It is tested on an empty DB and on a DB at the
  previous migration, forward, back and forward again (spec §13 item 37).
- A **startup step** (beside `DbInitializer`) recalculates every parent, deepest first, through the same rule and audit path.
  On the first run it overwrites existing parents' own values and removes their assignments (old values stay in the audit
  history). It writes nothing for a parent that is already right, so later runs are no-ops that also repair any drift.

## 4. Display-only values (worked out when a page draws them)

Computed per page with one extra query over the descendants of the parents shown (like the Gated badge), by a pure rule
class, `ParentDisplayRules`:

- **Status chips:** one small chip per status present among the **bottom-level** subtasks (the ones without subtasks of
  their own), with its count, coloured like the status badges, e.g. `3 Done` `2 In progress` `1 Blocked`. They replace
  the status badge and dropdown on a parent row.
- **Due date range:** the earliest due date among open subtasks → the stored (latest) due date, e.g. "10 Nov → 15 Dec".
  One date when the two are equal or nothing is open.
- **Overdue:** the parent row takes the overdue colour when any open subtask is overdue.
- **Sprint range:** the earliest → latest sprint among open subtasks ("Sprint 12 → Sprint 14"), one name when they share a
  sprint, followed by a small "+N unplanned" when N open subtasks are in no sprint. With no open subtask in a sprint, it
  shows Backlog or Not Planned as today.
- **Assignees:** the distinct people assigned to open subtasks, by name.

On narrow screens a range wraps to two lines (start, then "→ end" in small print).

## 5. What a parent can and can't do

- **Editable:** title, description, type, requestee, asset, project, department and parent (moving a parent still moves its
  whole tree, §6.15), comments, attachments, dependencies, solution notes (optional) and *Add subtask*.
- **Derived, not editable:** status, priority, start and due date, estimate, sprint and assignees.
  - The edit form shows them read-only, marked "from subtasks".
  - Table rows show the chips and ranges of §4 instead of inline controls.
  - Every service method that sets one of these fields refuses a parent with a `ValidationException`, e.g.
    *"T-26-00012 has subtasks; its due date comes from them."* That covers `UpdateAsync` (when a derived field differs),
    `ChangeStatusAsync`, the assignee methods, `TakeAsync`, `ChangeDueDateAsync`, `ChangeEstimateAsync`, `ChangeDatesAsync`
    (the Gantt drag), `ChangeSprintAsync`, `MoveToSprintAsync` and `SetPlannedForAsync`.
  - Completing a sprint (`SprintService`, both the roll-forward and the return to the Backlog) moves only open tasks
    **without** subtasks; each parent's sprint then follows its moved subtasks through the save hook.
- **No work recorded:** no Today tick, Take button, clock or time logging on a parent. Time already logged on it stays and
  still counts. On the Planning page a parent can't be selected; its subtasks can.
- **Closing:** a parent closes by itself when its subtasks do; solution notes are not required on it. The rule "a parent
  can't be closed while it has open subtasks" (`EnsureStatusChangeAllowedAsync`) goes away.
- **Reopening:** adding an open subtask to a closed parent is now allowed and reopens it. The parent picker lists closed
  tasks too, labelled with their status (it already labels a current closed parent).
- **Add subtask pre-fill:** when the parent has no subtasks yet, *Add subtask* copies the parent's assignees, priority, due
  date, estimate and sprint into the new subtask's form (a sprint that has since closed is skipped). The person can change
  any of it before saving. Claude's `create_task` gets no pre-fill; its description says that the parent's own values are
  replaced.
- **Dependencies:** links on parents still work, and start gates are still inherited by subtasks (§6.15). A finish gate
  (FF/SF) on a parent moves to its **last open subtask**: closing that subtask is refused, naming the parent's link, while the
  predecessor is unmet.
- **Gantt:** a parent's bar is its summary span (the two now coincide) and doesn't drag.
- **Critical path:** the engine is unchanged; a parent is an activity spanning its subtasks. Parents are left out of the
  critical and near-critical counts. The "parent's dates don't cover its subtasks" warning (`ParentWindow`) is removed, as it
  can no longer occur.
- **Emails:** parents get no due-soon notices (`DueDateNotificationJob`) and no email on an automatic status change.

## 6. Tree view on the Tasks page and the project page

- **Rows:** each subtask directly under its parent, indented one step per level, the indent capped at four levels. Parent
  rows carry a ▸/▾ toggle and a small "(n)" subtask count, plus the §4 values; the priority shows as a normal chip. In tree
  mode the "in: Parent" line goes, since the parent is right above.
- **Filters (both pages):** a filter selects tasks as today. Each match is shown under its full parent chain. Parents that
  did not match are shown as greyed **context rows** with no controls; their title still links to the task where the viewer
  may open it.
- **Order:** top-level groups in today's order (open first, due date, priority, newest), using the parents' stored values;
  subtasks in the same order within their parent.
- **Paging (Tasks page):** a page holds whole top-level groups up to about 50 rows (expanded or not) and never splits a
  group; a group larger than that has a page to itself. Collapsing never moves page boundaries.
  - Sketch: run the filter for the matching ids with their parent ids and sort keys (no paging), load ancestors level by
    level, build the forest with the tree rule, page the groups, then load the full rows for that page only.
- **Count:** the Tasks page header counts the matching tasks **without subtasks**, consistent with the dashboard (§7).
- **Collapse:** the set of collapsed task ids is kept in `localStorage` (a small `orbit.collapsed` helper in
  `orbit-interop.js`, read through a C# wrapper like `BrowserFilterMemory`). It is read as the page loads, so rows don't flash
  open. Parents start expanded. "Expand all" and "Collapse all" sit above the table. One state covers both pages.
- **Project page:** the same tree and filter rule for its status, assignee and department filters; no paging.
- **Other task lists stay flat:** Planning, sprint pages, the sprint board and Today leave parents out; their subtasks keep
  "in: Parent". Asset history and any other list is unchanged.
- **Building blocks:** the tree order in `GanttChart.Build` moves into a pure, shared `TaskTreeRules` used by the Gantt and
  the lists. `TaskTable` gains a tree mode (rows with depth, context flag and parent display values) that only these two
  pages switch on.

## 7. Counting without parents

- **Rule:** every task count and estimate sum leaves parents out. Time sums are unchanged: each time entry counts once on the
  task it was logged on.
- **Mechanism:** one query helper, `WorkOnly()` (`q.Where(t => !t.HasSubtasks)`), applied in each count query, so every place
  can be found by searching for it. In-memory rule classes that count or sum skip `HasSubtasks` tasks.
- **Where:**
  - Dashboard (`DashboardService`, `DashboardRules`): status tiles and donuts, overdue and due-soon, up for grabs, per person,
    per department, the 14-day trend, active sprint progress, projects at a glance.
  - `ProjectService` (status summary, list counts, estimate sum), `DepartmentService` counts.
  - `SprintService` list and board counts, `SprintDetails` figures; `SprintPlanningService` / `SprintPlanningRules`
    capacity and committed effort.
  - Today (`TaskService.GetDayPlanAsync`, `DayPlanRules`, `DayPlanModels`).
  - `ReportingService` and the report rules (`TimeReportRules` estimate accuracy, `ProjectReportRules`, `SprintReportRules`):
    closed and created by person, response and resolve times, estimate sums, task counts. The PDFs use the same DTOs. Time
    sums in them keep every task (a parent's time stays in the project's, sprint's and person's figures).
  - MCP counts in `list_projects`, `get_project` and `get_project_status`.
  - Critical path counts (§5).
- **Asset-linked lists and counts keep parents** (ruling R10): the asset page's task history, `get_asset` and the Asset status
  report are unchanged, because subtasks don't inherit `AssetId`, so filtering parents would make the work about an asset
  vanish. `AssetService` and `AssetReportRules` are therefore not in the list above.
- Parents have no assignees, so without this rule they would count as "up for grabs" and match the Unassigned filter; with
  it they don't.

## 8. MCP (Claude's tools)

- Task payloads gain `hasSubtasks`. `list_tasks` takes `hasSubtasks` (`true` / `false`).
- `update_task` refuses derived fields on a parent with the readable message of §5. `create_task` with a `parentTaskId`
  whose task has no subtasks makes it a parent (its own values replaced, its assignees removed).
- `get_project_status` and the project counts follow §7.
- Update the tool descriptions, `ServerInstructions` in `Program.cs` and the tool list in `README.md`.

## 9. Testing

Unit tests in `Orbit.Tests` (pure code only, per the project's convention), citing these acceptance IDs:

| ID | Behaviour |
|---|---|
| SUB-001 | All subtasks cancelled → parent Cancelled |
| SUB-002 | All non-cancelled Done → Done |
| SUB-003 | All Todo → Todo |
| SUB-004 | Open ones all Blocked → Blocked; all Waiting → Waiting; Waiting and Blocked mixed → Blocked |
| SUB-005 | Anything else → In Progress ({Todo, Done}, {Todo, Blocked}, any In Progress) |
| SUB-006 | Priority: highest open; none open → highest overall |
| SUB-007 | Start earliest, due latest, missing dates skipped, cancelled ignored |
| SUB-008 | Estimate sum; null when no subtask has one |
| SUB-009 | Sprint: earliest open by start date; Backlog then Not Planned fallbacks; all closed |
| SUB-010 | A parent of parents gives the same result as the whole tree |
| SUB-011 | CompletedAt and FirstRespondedAt follow the rolled-up status |
| SUB-012 | Status chips count bottom-level subtasks |
| SUB-013 | Due range next-open → latest; single date; overdue flag from open subtasks |
| SUB-014 | Sprint range with "+N unplanned"; Backlog / Not Planned when none in a sprint |
| SUB-015 | Display assignees: distinct people on open subtasks |
| SUB-016 | Tree order and greyed context parents for a filtered match |
| SUB-017 | Paging never splits a group; an oversized group gets its own page |
| SUB-018 | Indent capped at four levels |
| SUB-019 | `WorkOnly()` and the Planning page's query (`SprintPlanningRules.Shown`) leave parents out; the services apply `WorkOnly()` at every count (checked by running) |
| SUB-020 | Derived fields refused on a parent; no Today tick, Take, clock or time on a parent |
| SUB-021 | A finish gate on a parent applies to its last open subtask |
| SUB-022 | An open subtask under a closed parent reopens it |
| SUB-023 | Add subtask pre-fills from a parent with no subtasks, skipping a closed sprint |
| SUB-024 | A clock running on a task when it becomes a parent is stopped and its time logged |
| SUB-025 | The critical path's critical and near-critical counts leave parents out |

Checked by running the app (no DB tests exist): the save hook through the form, inline controls, sprint completion, MCP and
moving a subtask between parents; the startup recalculation; the migration forward, back and forward; both
pages with filters, paging and collapse.

## 10. Documentation

- `orbit-spec.md`: §5.1 (`HasSubtasks`), §6.15 (replace "No roll-up" and its assumptions with §§3-5 here), §6.2 and §6.1
  (tree view), §6.3 and §6.12 (parents left out of Planning and Today), §6.9 (dashboard), §6.10 (no time on parents),
  §6.16 (parent bar), §6.17 (parent activity, counts, removed warning), §7.1 (MCP), §12 (reports count without parents).
- §13 Decisions Log: new numbered entries (from 145) that reverse decision 30's counting rule and decision 64's
  parent-and-subtask time, saying why. §14 Suggested Build Order: a new step.
- `README.md`: the user-visible changes and the MCP tool list.

## 11. Build order

Each step ships on its own:

1. `HasSubtasks`, `ParentRollUpRules`, the save hook, the migration and the startup recalculation.
2. Parent restrictions in the services, the edit form, the Add subtask pre-fill, dependencies' finish gate, MCP.
3. Counting without parents (`WorkOnly()` and the rule classes).
4. `TaskTreeRules`, `TaskTable` tree mode, the display values, collapse memory, the Tasks and project pages.
5. Spec and README alongside each step.

## 12. Out of scope (could follow later)

- A tree on the Planning, sprint and Today pages.
- An email to a parent's requestee when the parent closes on its own.
- Collapse state stored on the user's account (it is per browser).
- The critical path expanding links on a parent onto its subtasks (the parent stays an activity spanning them).
