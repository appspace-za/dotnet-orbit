# Time spent report

Date: 2026-11-21. Status: design approved in conversation, awaiting review of this written spec.
Branch: `feature/time-spent-report`. Spec sections affected: §6.10, §12. Decision log entry: 149. Build order step: 133.

## 1. Problem and goal

*Time by person* (§12) answers "how much did each person log?" but not "what is taking their time?". It lists people, and a
person's tasks only one person at a time, so the tasks eating the most time across a team, and the kinds of work behind
them, don't show.

The goal is a new report, **Time spent**, built like *Sprint status*: summary tiles and charts at the top, then the detail.
The detail is one row per **task**, most time first, each opening on the **people** who logged time on it. (Against Sprint
status, its project row becomes the task row, and its *People and tasks* becomes just people.) Once it is built, *Time by
person* is removed.

### Decisions taken in conversation

| Question | Decision |
|---|---|
| How does the sprint filter work? | A **quick-pick for the date range**, as on My Time (decision 148): picking a sprint fills *From* and *To*. Time counts by entry date in the window, whatever sprint the task is in, so support and unplanned work show. Any custom range still works. |
| What does the person filter select? | **A person's own time**: the time entries they logged. Labelled *Person*, not *Assignee*. |
| People who logged nothing? | Kept (decision 44): counted on the People tile and listed, greyed, in the *By person* table. |
| Charts stacked or single colour? | **Single colour.** Planned/unplanned stacking was considered and dropped: with a free date range "planned" has no single sprint to be judged against. The type and source charts carry the breakdown; the Person filter gives one person's. |
| Long ranges on the per-day chart? | **Days up to 62 days, ISO weeks up to 366, months beyond.** |
| Build approach | **A new report kind**, then retire *Time by person* in a separate, last commit. |
| Per-person totals | A compact **By person** table, since the person chart folds past 12. |
| Assignees with no time on a task | **Not listed** in a task's opened row. |

### What this is not

- No planned/unplanned split. Sprint status stays the place for that, for one sprint.
- No new permission (`reports.view`, as every report), no migration, no MCP tool.
- Sprint status, including its own *Time by person* chart, is unchanged.

## 2. What's counted

- Every `TimeEntry` whose `Date` is between *From* and *To*, **both days included**.
- An entry takes its **project and department from its task**, as *Time by person* does (§6.2.1, §12). A Department-scoped
  `reports.view` fixes the department to the viewer's own (§6.5).
- Each task stands alone: time on a subtask isn't rolled into its parent (§6.10).
- *To date* on a task is **all time logged on it, by anyone, on any date**. It exists for the estimate comparison, which is
  over the whole task. It is never narrowed by the person filter.
- Estimate comparisons are `TimeReportRules.Compare`, unchanged: estimated tasks that aren't Cancelled, against their time to
  date, each task once.

## 3. Filters

In this order on the page:

1. **From / To.** Default the last 30 days (31 days inclusive), as the other date-range reports (`ReportAddress.Period`).
   Dates are handled as the report page does today: the pickers' `Date` comes from the address, never from loaded data
   (§13 item 143).
2. **Sprint (quick-pick).** Lists the started sprints (active and completed), newest first, labelled *Sprint 14 (Active)*,
   from `SprintService.ListStartedAsync`. Picking one sets *From* and *To* to its first and last days in one navigation. It
   **shows the sprint while the range is exactly one sprint's** (`TimeRules.SprintForRange`), so it has no address key of
   its own; changing a date by hand blanks it. Picking a sprint leaves the department, project and person filters alone.
3. **Department.** As on every report; hidden under a Department-scoped `reports.view`.
4. **Project.** The picker the date-range reports have today (`ListOpenForPickerAsync(includeArchived: true)`), with a
   project chosen from a link kept under its name.
5. **Person.** Keeps only the time entries that person logged. Every tile, chart and row then reads their time, except *To
   date* (§2).
   - **Options:** the people who logged time in the range under the department and project filters, worked out **before**
     the person filter applies, by name. Inactive people are marked *(inactive)*. Someone chosen but not offered (a pasted
     link) stays selected and finds nothing, as the other reports' person filters behave (`ReportRun.PersonOptions`).
   - **Address key** `PersonId` (`ReportRun.PersonKey`), with a new `ReportDefinition.ByPerson` flag. `AssigneeId` isn't
     reused: on Sprint status and Project status it means the task's assignee.

`ReportFilter` gains an optional `PersonId`, read only by Time spent. A new `ReportDefinition.SprintQuickPick` flag shows the
sprint quick-pick beside the dates.

The summary line above the result names the filters: `2026-10-16 to 2026-10-29 · Sprint 14 · Department: IT · Project:
Website · Person: Alice Smith`. The sprint is named only when the range is exactly one sprint's.

## 4. People who logged nothing

- The same rule as today (decision 44): **active people, never the Claude user, whose own department is the one reported
  on**. That is the chosen department, the viewer's own under a Department-scoped `reports.view`, the project's department
  when only a project is chosen, or everyone when neither is (`ReportingService.PeopleAsync`, reused).
- With the Person filter on, only that person is considered: they are listed as having logged nothing when they did and are
  among the people above.
- They are counted on the People tile and listed in the *By person* table (§7). They are not on the charts.

## 5. Summary tiles

Four tiles in a row (`orbit-stats`), as Sprint status's:

| Tile | Value | Beneath |
|---|---|---|
| **Logged** | Total time in the range | *on 37 tasks* |
| **People** | How many people logged time | *2 logged nothing* (only when there are some) |
| **Tasks** | Distinct tasks with time | *5 unestimated* (only when there are some) |
| **Estimate variance** | Variance over the estimated, non-cancelled tasks worked on, signed (`TimeFormat.Signed`); `-` when none is estimated | *40h estimated · 46h logged · 3 over estimate* |

## 6. Charts

Five SVG charts drawn by `SvgCharts`, so the page and the PDF show the same picture.

- **Single colour, no legend.** One new series (`oc-time`) with a light fill and a dark-theme shade in `app.css`, checked
  for contrast in both themes.
- **Each bar's tip** reads its time and its share of the total: `12h 30m · 24%`. Shares, here and in the tables, are whole
  percentages rounded to the nearest; a share above nothing that rounds to 0 reads `<1%`. They needn't add up to 100%.
- **Layout:** *Time per day* across the top at full width; then *Time by project* | *Time by person*; then *Time by task type*
  | *Time by task source*. On narrow screens they are one to a row.

| Chart | Bars | Order and folding |
|---|---|---|
| **Time per day** (or *week* / *month*) | One column per bucket (§6.1) | In date order. Days: weekends greyed. Hovering shows the bucket's dates. |
| **Time by project** | One per project with time, then *No project*, muted, last | Most time first. Past 12 bars, the 11 largest keep theirs and the rest share one (*19 other projects*), as `SprintCharts.Fold` does. A project bar's hover shows its number and name. |
| **Time by person** | One per person who logged time | Most time first, folded past 12 as above (*8 others*). |
| **Time by task type** | One per type with time | Most time first, then the type's declared order. At most eight bars, so no folding. |
| **Time by task source** | One per source with time, labelled as `Ui` labels them (*Manual*, *Claude* for `Api`, *Recurring*, *Request*, *Nagios*) | Most time first, then the source's declared order. At most five bars. |

Every chart shows its own empty message when no time was logged.

### 6.1 Buckets for the per-day chart

A pure rule picks the bucket from the length of the range, *From* to *To* inclusive:

- **Up to 62 days: days.** Title *Time per day*. One column per day, weekends greyed, labelled weekday and day (`Mon\n14`),
  as Sprint status's.
- **63 to 366 days: weeks.** Title *Time per week*. One column per ISO week (Monday to Sunday); the first and last weeks are
  clipped to the range. Labelled by the week's first day in the range (`14\nOct`); hovering shows the dates it covers
  (`2026-10-14 to 2026-10-20`).
- **More than 366 days: months.** Title *Time per month*. One column per calendar month, clipped to the range, labelled
  `Oct\n2026`.

Every minute in the range falls in exactly one bucket.

The wide chart is drawn twice, as Sprint status's *Planned work outcome* is: at full width beside two columns of charts, and
at half width for the one-column layout, so its text matches the others'. The PDF takes the wide one.

## 7. By person table

Between the charts and the task table. One row per person who logged time, then the people who logged nothing.

| Column | Shows |
|---|---|
| Person | Name |
| Logged | Their time in the range, in bold |
| Share | Their share of the total |
| *(bar)* | A bar scaled to the person with the most time |
| Tasks | How many distinct tasks they logged time on |

- Ordered by time, most first, then name. People who logged nothing come last, by name, greyed, reading *0m* and *0*, as
  *Time by person*'s zero rows do.
- A **Total** row: the total time and the distinct tasks (each task once, so it can be less than the rows' sum).
- No expansion: the task table covers who worked on what.

## 8. By task table

One row per task with time in the range, **most time in the range first, then by number**. Every task is listed; there is no
cap.

| Column | Shows |
|---|---|
| Task | Number and title, linked to `/Tasks/Details/{id}`. A type chip when the type isn't *Task*, as the task tables show it. The project beneath, linked to its page, or *No project*. |
| Status | Status chip, now |
| Logged | Time in the range, in bold, with its share of the total beneath (`9%`) |
| *(bar)* | A bar scaled to the task with the most time |
| People | How many people logged time on it in the range: a button that opens the row |
| Estimate | The task's estimate (`ReportText.Estimate`) |
| To date | All time on it, by anyone, ever (§2) |
| *(note)* | *Over estimate* chip, or *No estimate* / *Cancelled* (`ReportText.Marker`) |

- A **Total** row: the total time, the distinct tasks, and the estimate comparison over them, each task once.
- **Opening a row** shows one line per person who logged time on the task in the range: their time in the range, their share
  of the task's time (`2h · 60%`), their time on it to date, and an *Assignee* chip when they are assigned to it. Ordered by
  time in the range, most first, then name. With the Person filter on, it shows just that person.
- **Open rows stay open** when a filter changes and the report reloads, as on Sprint status: the open set is keyed by task id
  and the component is kept across runs (no `@key`).
- With no time in the range, the table shows *No time logged for the selected filters.*; the tiles, charts (with their empty
  messages) and the *By person* table, listing the people who logged nothing, still show.
- A **footer note** under the table states the definitions: the window and the entry date, the department and project taken
  from the task, *To date* being everyone's time ever, the estimate comparison, shared tasks counted once in totals, the
  people who logged nothing, the buckets, and the charts' folding past twelve.

## 9. Page order and PDF

**Page:** filters, the summary line, tiles, charts, *By person*, *By task*, footer note.

**PDF**, landscape A4, as Sprint status's:

1. The title and the summary line (§3), as every report's PDF has.
2. **Charts:** *Time per day* (or week or month) across the page, then the other four two to a row
   (`TimeSpentCharts.PdfSections`, as `SprintCharts.PdfSections`).
3. **Summary:** one row: Logged, People who logged time, Tasks (with unestimated), Estimated, Logged to date, Variance (with
   the number over estimate).
4. **By person:** Person, Logged, Share, Tasks; the people who logged nothing at *0m*; a Total row.
5. **By task:** one table. Columns: Task (number and title, with the project on a second line), Type, Status, Logged, Share,
   Estimate, To date, note, and **People**: one line per person, `Alice Smith 2h 30m (60%) · assignee`. A Total row.
   - One row per task, not a table per task as Sprint status has per project: a month can mean a couple of hundred tasks.

The file name comes from `ReportRun.FileName`: `orbit-timespent-20261016-20261029.pdf`.

## 10. How it is built

The same shape as Sprint status: the service reads in SQL, a pure rules class builds the report, a charts class draws it.

### 10.1 `ReportingService.TimeSpentAsync`

- Checks `reports.view` (`RequireReportsAsync`) and applies the department and project through the existing
  `Apply(IQueryable<TimeEntry>, ...)`, and the date window.
- Reads the time **once**, summed by task, person and day. The person filter is **not** applied in SQL, so the Person options
  can be worked out first.
- Reads, for the tasks that had time: id, number, title, status, type, source, project id, assignee ids, estimate, and all
  time logged to date.
- Reads the projects' number and name, and the names and active flags of the people who logged time
  (`ReportPerson`; the Claude user named *Claude*, as elsewhere).
- Reads the people who logged nothing with `PeopleAsync`.
- Hands it all to `TimeSpentRules.Build`.

### 10.2 `TimeSpentRules` (pure, `Orbit.Web/Application/TimeSpentRules.cs`)

- `Build(...)` returns a `TimeSpentReport`:
  - the Person options, before the person filter;
  - then, with the person filter applied: the tile figures, the *By person* rows (with the zero rows), the task rows with
    their people, the project, type and source totals, and the time buckets.
- `Buckets(from, to)` is the bucket rule of §6.1, on its own so it can be tested alone.
- No database access. New models in `ReportModels.cs`.

### 10.3 Page, charts and PDF

- **`TimeSpentCharts`** (`Components/Pages/Reports/`): the five charts and their PDF layout, reusing `SvgCharts` and
  `SprintCharts.Fold`.
- **`ReportTimeSpent.razor`**: the page section (tiles, charts, *By person*, *By task*, footer).
- **`ReportView.razor`**: the sprint quick-pick beside *From* / *To* when `SprintQuickPick`, and the Person filter when
  `ByPerson`. The quick-pick changes both dates in one navigation; the pickers' echo of the new dates is ignored as today.
- **`ReportRun`**: `PersonKey` and `PersonId`; the sprint options (fetched for this kind even without options, since the
  summary line names the sprint); the Person options from the report; and the summary line.
- **`ReportAddress.Parse`**: reads `PersonId`.
- **`ReportCatalog`**: the *Time spent* entry, with the description *"Where the time logged in the period went: by project,
  person, day, task type and source, then every task worked on, most time first, with who logged it."*
- **`ReportPdfEndpoint`**: the sections of §9, and landscape for this kind.
- **`ReportsIndex.razor`**: the subtitle mentions that Time spent's dates can be set from a sprint.

## 11. Retiring Time by person

The last commit, once the two reports have been compared:

- Delete `ReportKind.TimeByPerson`, `ReportTimeByPerson.razor`, `TimeReportRules.TimeByPerson` and its models
  (`TimeByPersonReport`, `PersonTimeRow`, `PersonTaskTimeRow`, and `LoggedTime` if nothing else uses it),
  `ReportingService.TimeByPersonAsync` and `ReportPdfEndpoint.TimeByPersonTables`. `PeopleAsync` stays: Time spent uses it.
- **Old links:** `Kind=TimeByPerson` in an address maps to Time spent in `ReportAddress.Parse`, keeping its dates, department
  and project, so a bookmark doesn't fall back to *Closed count by person*.
- **Tests:** RPT-001, RPT-003, RPT-006 (its Time by person part) and RPT-008 are rewritten against `TimeSpentRules` under the
  new IDs below and the old ones retired in the spec. RPT-002, RPT-004, RPT-005 and RPT-007 test `Compare`, Estimate accuracy
  and the variance text, and stay.
- Sprint status's own *Time by person* chart keeps its name.

## 12. Testing

xUnit over pure code, as the rest of the suite (`Orbit.Tests/Reports/TimeSpentRulesTests.cs` and the charts tests). Tests cite
the acceptance IDs in their doc comments.

| ID | Covers |
|---|---|
| **RPT-044** | Entries on *From* and on *To* count, those a day outside don't; time counts by entry date; a subtask's time isn't on its parent. |
| **RPT-045** | The Person options are the people with time before the person filter; the filter keeps only that person's entries; *To date* stays everyone's; a person chosen but not offered finds nothing. |
| **RPT-046** | The tiles: total time, people with time, tasks, unestimated tasks, and the estimate comparison counting a shared task once. |
| **RPT-047** | *By person*: ordered by time then name; the people who logged nothing last, by name, at zero, not doubled when they did log time, and leaving the totals alone; the Total row counts each task once; shares. With the Person filter, only that person is considered. |
| **RPT-048** | *By task*: ordered by time in the range then number; each task's people by time then name, with their share of the task and the assignee flag; the Total row counts each task once. |
| **RPT-049** | Project, type and source totals: only those with time, most first, ties in declared order, *No project* last; each adds up to the total. |
| **RPT-050** | Buckets: 62 days daily, 63 weekly, 366 weekly, 367 monthly; ISO weeks from Monday, clipped at both ends; every minute in exactly one bucket. |
| **RPT-051** | The charts: five, in order; the per-day chart wide with a half-width copy, its title following the bucket; projects and people folded past twelve; tips with the share; an empty message with no time; the PDF sections. |
| **RPT-052** | An address with `Kind=TimeByPerson` opens Time spent with its other filters kept (added with §11). |

Then a **read-only check in Chrome** against the dev database, as CLAUDE.md asks for DB-touching changes: a sprint picked
from the quick-pick (and named again after a reload); a month; a 100-day range (weekly buckets); the Department, Project and
Person filters; a task row opened, staying open across a filter change; the PDF; and both themes. After §11, an old
`Kind=TimeByPerson` link.

## 13. Documentation

- **`orbit-spec.md`:**
  - §12: the report table's *Time spent* row in place of *Time by person*; a *Time spent* section, with its acceptance list,
    in place of the *Time by person* half of *Time reports*; the opening line's filters.
  - §6.10: the *Time reports* line.
  - §13: decision **149**, recording the choices in §1's table and why.
  - §14: build step **133**.
- **`README.md`:** the report list and the time-reports line.
