# Time utilisation and availability

Date: 2026-11-21. Status: design approved in conversation, awaiting review of this written spec.
Branch: `feature/time-utilisation`. Spec sections affected: §3, §5.1, §6.5, §6.10, new §6.22, §10, §12. Decision log entry:
150. Build order step: 134.

## 1. Problem and goal

*Time spent* (decision 149) shows where logged time went, but not whether people logged all of it. A department head checking
timesheets wants to know, for each person and period: **of the time they were available, how much did they account for in
Orbit?** Orbit doesn't know when anyone is available. The organisation's working calendar (§6.17) holds the working week and
public holidays only, and one working-day length in configuration (`CriticalPath:HoursPerWorkingDay`) stands in for everyone.

The goal:

1. Record each person's **availability**: a dated **work pattern** of hours per weekday, and **unavailable time** (leave,
   sickness, training) at finer than whole days.
2. Rename *Time spent* to **Time utilisation** and make it about **people**: their available time against all the time they
   logged, day by day, with where the logged time went kept as it is.

The purpose is **timesheet completeness**: a low figure means unlogged time to follow up. It is not capacity planning: the
Planning page's capacity and the Today page's *overloaded* flag stay on `HoursPerWorkingDay` (a natural follow-up; see §13
below).

### Decisions taken in conversation

| Question | Decision |
|---|---|
| What is utilisation for? | **Timesheet completeness**: logged ÷ available. Not capacity planning, not performance review. |
| How is a week shaped? | **Hours per weekday** on the person (some staff work only Monday and Tuesday). The weekly total is their sum. |
| What happens when a pattern changes? | **Dated patterns**: each in effect from a date, so past periods keep the pattern they had. |
| How is part of a day recorded? | **Hours**, not clock times. An entry is a date range, all day or N hours a day. No time zones. |
| Who maintains it? | A new permission, **`availability.manage`** (Own / Department / All). Own records your own unavailable time and never changes a pattern. Member Own, Department Admin Department by default. No approval step. |
| Who sees the reason? | **The person and whoever manages their availability.** Reports and the Activity Log show hours only. |
| What does the Department filter pick? | **People, by home department, and all their time**, on any task in any department. The report becomes person-centred. |
| The Project filter? | **Dropped.** Utilisation needs all of a person's time; *Project status* covers a project's time by person. |
| What do existing and new users start with? | **The standard week**: the organisation's working days × `HoursPerWorkingDay`, dated from the account's creation. |
| Where is availability edited? | An **Availability** page: a **team grid** (people × days) and a **person page** (pattern and entries). |
| How does the report show gaps? | A **person × day grid** of logged against available, as well as tiles, charts and By person columns. |
| When is something "short"? | Below **one target** in configuration, `Reports:UtilisationTargetPercent`, default 80. |
| How is availability worked out? | **On the fly by a pure rule** (`AvailabilityRules`), never stored per day. |
| My Time? | Gains a line: *Logged 32h of 40h available · 80%*. |

### What this is not

- No capacity planning: the Planning page, the Today page and critical path analysis don't read availability. §3's "no
  individual resource calendars or leave" stays true **for scheduling**.
- No clock times, time zones, approvals, leave balances, accrual or emails.
- No MCP tool yet. Asking Claude to record *"Thandi is off sick tomorrow"* is a natural follow-up.
- No history of a person's department: the report takes the department a person is in now.

## 2. Data

### 2.1 `WorkPattern`

| Field | Notes |
|---|---|
| `Id` | Guid |
| `UserId` | FK to `AspNetUsers`, cascade on delete (users are never hard-deleted, §6.5) |
| `EffectiveFrom` | `DateOnly`. Unique together with `UserId`. |
| `MondayMinutes` … `SundayMinutes` | `int`, 0 to 1,440 in steps of 15 |
| `CreatedAt`, `CreatedById` | |

- The weekly total is the sum of the seven, never stored.
- **Entered in hours** on the form, quarter hours allowed (`7.5`, `7.25`, `0`), up to 24 a day. A week of 0 hours is allowed
  (a sabbatical, say).
- **Setting a pattern from a date** adds a row. Setting one on a date that already has a row **replaces** that row, so a
  mistake can be corrected on its own date. Any row can be **removed** except the person's only one. A correction changes
  past figures, which is the point of correcting it.

### 2.2 `UnavailableTime`

| Field | Notes |
|---|---|
| `Id` | Guid |
| `UserId` | FK to `AspNetUsers`, cascade on delete |
| `From`, `To` | `DateOnly`, both included. `To` ≥ `From`, at most 366 days apart. Index on (`UserId`, `From`). |
| `MinutesPerDay` | `int?`. Null = **all day**. Otherwise 15 to 1,440 in steps of 15. |
| `Reason` | `UnavailableReason`: **Leave, Sick, Training, Other**, stored by name (the enum convention) |
| `Note` | Optional, up to 500 characters |
| `CreatedAt`, `CreatedById`, `UpdatedAt`, `UpdatedById` | |

- Overlapping entries are allowed. The rule caps them (§3).
- Entries can be edited and removed whatever their dates, past ones included (corrections).

### 2.3 Configuration

A new `ReportOptions` (section `Reports`) with `UtilisationTargetPercent`, default **80**, from 1 to 100. A value out of range
falls back to 80 and is logged at startup.

## 3. The availability rule

`AvailabilityRules` (pure, `Orbit.Web/Application/Availability/AvailabilityRules.cs`). For a person and a range it returns one
`AvailabilityDay` per date: the pattern's minutes, the available minutes, the unavailable minutes by reason, whether it is a
holiday (with its name) and whether the person was employed.

A person's **available minutes on day *d***:

1. **Employment.** None unless the account existed and was active on *d*. The account counts from the UTC day it was created,
   stops after the day of a `Deactivated` audit row and counts again from the day of a `Reactivated` one. The periods come from
   the user's own audit trail (`EmploymentRules.Periods(createdAt, rows)`), as Project status rebuilds a status from its trail.
   So a leaver isn't short for the rest of the month and a new joiner isn't short before they started.
2. **The pattern in effect**: the latest one with `EffectiveFrom` on or before *d*, or the person's first pattern when *d* is
   earlier than all of them. Its minutes for *d*'s weekday are the day's **base**. The organisation's working week plays no
   part: the person's own pattern decides, so a Saturday worker has Saturday hours.
3. **Holidays.** A non-working exception in the organisation calendar (a public holiday or shutdown day, §6.17) makes the day's
   availability 0, marked *holiday* with its name. An *exceptional working day* changes nothing.
4. **Unavailable time.** Otherwise every entry covering *d* is subtracted from the base: all day removes all of it, N hours
   removes N. Several entries add up. The result is never below 0. What was actually removed is the day's **unavailable**
   time, split by reason in the order the entries were created, so the parts add up to it.
5. **System accounts** (the Claude user) never have availability.

A person with no pattern at all (none should exist after the migration, §11) has no availability. The report lists them as
*No work pattern* if they logged time.

## 4. Permission and access

**`availability.manage`**, scopes Own / Department / All, in a new *Availability* group of `PermissionCatalog`. Default grants:
Member **Own**, Department Admin **Department**. The built-in role holds All.

| Scope | Unavailable time | Work pattern |
|---|---|---|
| Own | Your own: add, edit, remove | See your own; never change it |
| Department | Anyone whose home department is yours, you included | Change it for the same people |
| All | Everyone | Everyone |

- **`AccessPolicy.CanManageAvailability(actor, person)`**: Own when the person is the actor, Department when the person's
  `DepartmentId` is the actor's, All always. **`CanChangeWorkPattern(actor, person)`**: the same at Department and All, never at
  Own. Lists of people go through a new **`Scoping.People(q, actor)`**. System accounts are never offered.
- **Page doors:** the `/Availability` pages carry `[RequirePermission(Permission.AvailabilityManage)]`. The service applies
  the scope and refuses with `ForbiddenException` (*"You can't manage this person's availability."*).
- **Privacy.** The reason and note of an entry are shown only on the Availability pages, which only the person and people
  managing them can open. The report never shows a reason: a day with unavailable time is marked *unavailable*. Audit details
  hold dates and hours, not the reason or note.
- **Audit**, on the person (`AuditEntity.User`, the person's department): new actions `WorkPatternSet` (`{effectiveFrom,
  weeklyMinutes, replaced}`), `WorkPatternRemoved`, `UnavailableAdded`, `UnavailableUpdated` (a `ChangeSet` of `from`, `to`,
  `minutesPerDay`) and `UnavailableRemoved`.

## 5. Pages

### 5.1 Menu

An **Availability** item after *My Time*, shown with `availability.manage` at any scope (`Navigation`, NAV test). Its `Href` is
`/Availability`.

### 5.2 `/Availability`, the team grid (`AvailabilityGridPage`)

- **At Own scope** the page sends you to your own page (`/Availability/{you}`, replacing the history entry): there is no team
  to show.
- **Filters**, read from the address with `[SupplyParameterFromQuery]`: `View` (`week` or `month`), `From` (snapped to the
  week's Monday or the month's 1st; this week or month by default), ◀ ▶ buttons stepping one period, and **Department** at
  All scope (all departments by default; fixed to your own at Department scope). The page inherits `OrbitListPage`;
  `View` and `Department` are remembered with `BrowserFilterMemory`, `From` isn't (it opens on now).
- **Rows:** the active, non-system people in scope, by name. **Columns:** one per day, then **Total**.
- **A cell** shows the day's available hours (`8h`, `4.5h`). `-` when the pattern has no hours that day. **Shaded** when
  unavailable time reduced it, with the reasons' short forms beneath (*Lve*, *Sick*, *Trn*, *Oth*). **Greyed** for a holiday.
  Outside employment it is blank. **Hovering** explains it: *Pattern 8h · Leave 3h → 5h available*, or *Public holiday:
  Heritage Day*.
- Clicking a **name** opens the person's page. Clicking a **cell** opens it at `/Availability/{id}?Add=2026-11-24`, with the
  *Add* form open for that day.
- **Month view** narrows the columns and shows hours without reasons (hover still explains). On a phone the grid scrolls inside
  its own box, the name column fixed.
- `AvailabilityService.GridAsync` reads the people, their patterns, entries and employment rows, and the holidays, once; the
  pure `AvailabilityGridRules` builds the cells.

### 5.3 `/Availability/{userId}`, a person's page (`PersonAvailabilityPage`)

- **Header:** name, department and the weekly hours in effect today (*16h a week*).
- **Work pattern:** the pattern in effect today: seven days and the weekly total. **Change from…** (shown with
  `CanChangeWorkPattern`) opens a dialog with an *In effect from* date (today by default) and seven hour boxes pre-filled from
  the pattern in effect on that date. Beneath, the **history**: in effect from, the seven days, weekly total, set by and when,
  newest first, each with **Remove** while there is more than one.
- **Unavailable time:** a table with Dates (`2026-12-01 → 2026-12-12`, or one date), Hours (*All day* or *3h a day*), Reason,
  Note, and **Removed** (the hours it actually took off, after patterns, holidays and overlaps). Filter chips *Upcoming*
  (default: `To` today or later), *Past* and *All*, as a query parameter. **Add**, **Edit** and **Remove**.
- **The form:** *From*, *To* (defaults to *From*), *All day* or *Hours a day* (with the hours), *Reason*, *Note*. The service
  refuses *To* before *From*, a span over 366 days, hours outside 15 minutes to 24 or not in quarter hours, and a note over 500
  characters (`AvailabilityInputRules`).
- **Remove** asks in a MudBlazor dialog, never a browser `confirm`.
- Date pickers convert with `PickerDate` and take their `Date` from the address or the form's own state, never from loaded data
  (§13 item 143).

### 5.4 Admin > Users

A **Weekly hours** column: the pattern in effect today (*40h*), linked to the person's Availability page. The Create user page
doesn't ask for a pattern: the user starts on the standard week (§11).

### 5.5 My Time

Above the entries table, for the page's range: **Logged 32h of 40h available · 80%**, amber when under the target (§2.3). With
no availability in the range: *Logged 3h · no availability in this range*. It reads the viewer's own availability, so it needs no
permission beyond the page's own (`time.log`).

## 6. Time utilisation: what it counts

### 6.1 Rename

| Before | After |
|---|---|
| `ReportKind.TimeSpent` | `ReportKind.TimeUtilisation` |
| `TimeSpentRules`, `TimeSpentReport` and its models | `TimeUtilisationRules`, `TimeUtilisationReport`, … |
| `TimeSpentCharts` | `TimeUtilisationCharts` |
| `ReportTimeSpent.razor` | `ReportTimeUtilisation.razor` |
| `ReportingService.TimeSpentAsync` | `ReportingService.TimeUtilisationAsync` |
| `orbit-timespent-….pdf` | `orbit-timeutilisation-20261016-20261029.pdf` |

- **Old addresses:** `Kind=TimeSpent` and `Kind=TimeByPerson` (any case) open Time utilisation with their dates, department and
  person kept. A `ProjectId` is ignored and not named in the summary line.
- **Catalogue** description: *"How much of each person's available time they logged in the period, day by day, and where the
  logged time went: by project, task type and source, then every task worked on."*

### 6.2 Filters

From / To (the last 31 days by default), the **Sprint** quick-pick (unchanged, decision 148), **Department** and **Person**. The
Project filter is removed (`ReportDefinition` no longer offers it for this kind). The summary line: `2026-10-16 to 2026-10-29 ·
Sprint 14 · Department: IT · Person: Alice Smith`.

### 6.3 Who is in it

- The people whose **home department** is the one reported on: the chosen department, the viewer's own under a
  Department-scoped `reports.view`, or everyone (people with no department included) when none is chosen.
- Of those, the ones with **availability or logged time** in the range. A leaver with time up to their last day is in it;
  someone deactivated before the range isn't.
- Never the Claude user.
- The **Person options** are these people, by name, inactive ones marked *(inactive)*, worked out before the Person filter
  applies. Someone picked but not among them (a pasted link) shows as *Unknown* and finds nothing.

### 6.4 What counts

- **All** of those people's time entries dated in the range, on any task in any department. With the Person filter, that
  person's.
- Per person per day: *available* and *unavailable* (§3) and *logged*.
- **Utilisation** = logged ÷ available over the period, a whole percentage rounded to the nearest. With no availability and
  some time logged it reads *no availability*. It can exceed 100%.
- **Unlogged** = available − logged **per person**, never below 0, then added up. One person's overtime doesn't hide another's
  gap. In the grid and the period chart it is worked out per cell.
- **Under target**: utilisation below `Reports:UtilisationTargetPercent`, for a person over the period and for a cell in the
  grid. Only where there is availability.
- **Restricted tasks.** A task is shown in full when it is filed in a department the viewer's `reports.view` reaches (every
  department at All, so an All-scope viewer sees every task as Time spent showed them) or the viewer can open it
  (`Scoping.Tasks`). Otherwise (a Department-scoped viewer's people working on another department's tasks) it is listed by
  number and department only: no title, no link, no project link. Projects are named, as Sprint status names them.
- *To date* and the estimate comparison are unchanged (`TimeReportRules.Compare`), over every task worked on.

## 7. Time utilisation: layout

**Page order:** filters, summary line, tiles, charts, *By person*, *Timesheet by day*, *By task*, footer note.

### 7.1 Tiles

Six, in a row (`orbit-stats`):

| Tile | Value | Beneath (each part only when above zero) |
|---|---|---|
| **Available** | Total available | *24h unavailable · 2 holidays* (holidays: holiday or shutdown days in the range) |
| **Logged** | Total logged | *on 41 tasks* |
| **Utilisation** | Total logged ÷ total available | *3 under 80% · 1 logged nothing* |
| **Unlogged** | The per-person gaps added up | *across 5 people* |
| **Tasks** | Distinct tasks with time | *5 unestimated* |
| **Estimate variance** | As Time spent's | As Time spent's |

### 7.2 Charts

Five, drawn by `SvgCharts`, the page and the PDF alike.

| Chart | Bars | Order and folding |
|---|---|---|
| **Logged against available per day** (*week*, *month*; buckets as RPT-050) | A stacked column per bucket: **Logged** (up to available), **Unlogged** (the gap) and **Over** (logged beyond available). Its height is the larger of logged and available. Legend shown. Tip: *30h of 40h · 75%*. | Date order. Full width, with a half-width copy for the one-column layout, as today. |
| **Utilisation by person** | The same three series per person | **Lowest utilisation first**, then name; people with no availability last. Past 12, the 11 lowest keep their bars and the rest share one (*8 others*), its figures their sums. |
| **Time by project** | Unchanged | Unchanged |
| **Time by task type** | Unchanged | Unchanged |
| **Time by task source** | Unchanged | Unchanged |

Layout: the period chart across the top, then *Utilisation by person* | *Time by project*, then *by task type* | *by task
source*. Two new series in `app.css`: **Unlogged**, a muted grey-indigo, and **Over**, amber, each with a dark-theme shade,
checked for contrast and colour-blind separation from *Logged* (`oc-time`) in both themes, as the Sprint status series were.

### 7.3 By person

| Column | Shows |
|---|---|
| Person | Name; *(inactive)* when deactivated; *No work pattern* when they have none |
| Available | Their availability in the range |
| Unavailable | Their unavailable time in the range (no reasons) |
| Logged | Their time in the range, in bold |
| Utilisation | The percentage, with an **Under 80%** chip when under target |
| Unlogged | Their gap |
| *(bar)* | Logged against available, scaled to the largest of anyone's logged or available, with a mark at the target |
| Tasks | Distinct tasks they logged time on |

- Ordered by utilisation, lowest first, then name. People with no availability come last, by name.
- People who logged nothing are ordinary rows at 0% (first, not greyed).
- A **Total** row: the sums, utilisation over the totals, distinct tasks counted once.

### 7.4 Timesheet by day

- One row per person, **by name**; one column per bucket (§7.2); a last **Total** column (*36/37 · 97%*).
- A cell reads **logged / available** in hours (`6/8`, `7.5/8`). **Highlighted** when under target. A small **U** when the
  bucket has unavailable time (no reason). **Greyed** when it is all holiday. `-` when the person has no availability and logged
  nothing in it. Logged time against no availability shows as `2/0` with an **Over** mark.
- Hovering a cell lists its parts: *Pattern 8h · unavailable 3h · logged 4h*.
- With the Person filter on, one row.

### 7.5 By task

As Time spent's (the task row with its people), with §6.4's restricted tasks shown by number and department.

### 7.6 Footer note

States the definitions: entries dated in the window, both days; people by home department now; all of their time on any task;
availability from the work pattern less holidays and unavailable time, within employment; utilisation and unlogged per person;
the target; *To date* as everyone's time ever; the buckets; the folding past twelve.

### 7.7 PDF

Landscape A4:

1. The title and the summary line.
2. **Charts**: the period chart across the page, the other four two to a row.
3. **Summary**: Available, Unavailable, Logged, Utilisation, Unlogged, Tasks, Estimated, Logged to date, Variance.
4. **By person**: the page's columns without the bar; a Total row.
5. **Timesheet by day**: as the page, at 7pt. A landscape page fits about 31 day columns beside the names, so the PDF's grid uses
   **days up to 31, ISO weeks up to 366 and months beyond**; the page keeps 62. Highlighted cells take a light fill.
6. **By task**: as Time spent's one table, restricted tasks by number and department.

## 8. How it is built

- **`AvailabilityRules`, `EmploymentRules`, `AvailabilityInputRules`, `AvailabilityGridRules`** (pure,
  `Orbit.Web/Application/Availability/`). New models in `Application/Models/AvailabilityModels.cs`.
- **`AvailabilityFactsReader`** (scoped): reads, for a set of people and a range, their patterns, entries, employment audit
  rows, and the holidays (`WorkingCalendarService.BuildAsync`), in four queries. Used by `AvailabilityService` and
  `ReportingService`.
- **`AvailabilityService`** (scoped, `Application/Services/`): `GridAsync`, `PersonAsync`, `SetPatternAsync`,
  `RemovePatternAsync`, `AddUnavailableAsync`, `UpdateUnavailableAsync`, `RemoveUnavailableAsync`, and `MineAsync(from, to)` for
  My Time. Every write checks access, audits, and saves in one unit of work.
- **`WorkPatterns.Default(workingDays, hoursPerDay, effectiveFrom)`**: one helper for a new user's pattern, called by
  `UserAdminService` (Create user), the directory import, and `DbInitializer` (the first admin).
- **`ReportingService.TimeUtilisationAsync`**: picks the people (§6.3), reads all their time in the range once (summed by task,
  person and day) and the task facts as today, the availability facts, and the viewer's visible task ids; hands everything to
  `TimeUtilisationRules.Build`.
- **Components**: `AvailabilityGridPage`, `PersonAvailabilityPage`, an `UnavailableTimeDialog` and a `WorkPatternDialog`, all on
  `OrbitComponentBase` with `Load` / `Act` (no injected services). Styles in `css/app.css` (`orbit-availability-*`).

## 9. Testing

xUnit over pure code. Tests cite their IDs in doc comments.

| ID | Covers |
|---|---|
| **AVL-001** | The pattern in effect: the latest on or before the day; the first covers earlier days; a replaced row; the weekday's minutes. |
| **AVL-002** | A holiday or shutdown day is 0 and named; an exceptional working day changes nothing; the organisation week plays no part. |
| **AVL-003** | Unavailable time: all day, N hours, several on one day, overlaps capped, never below 0; the parts by reason add up to what was removed. |
| **AVL-004** | Employment: nothing before creation; the deactivation day counts, the days after don't; counting again from reactivation; several cycles. |
| **AVL-005** | System accounts and people with no pattern have no availability. |
| **AVL-006** | Input rules: `To` before `From`, a span over 366 days, hours out of range or not in quarters, the note's length, a 0-hour week allowed, removing the only pattern refused. |
| **AVL-007** | The team grid: week and month ranges snapped, cells (hours, `-`, shaded with reasons, greyed holiday, blank outside employment), totals, the hover text. |
| **AVL-008** | Access: `CanManageAvailability` and `CanChangeWorkPattern` at each scope (Own never changes a pattern), `Scoping.People`, the catalogue entry and default grants. |
| **AVL-009** | My Time's line: logged of available, the percentage, under target, no availability. |
| **RPT-044 to RPT-052** | Kept, moved to the renamed classes. RPT-044, RPT-048, RPT-049 and RPT-050 unchanged. RPT-045 (the Person options), RPT-046 (the tiles, now §7.1's six), RPT-047 (By person, now §7.3) and RPT-051 (the charts, now §7.2's) rewritten for the person-centred rules. RPT-052 also covers `Kind=TimeSpent`. |
| **RPT-053** | Who is in it: home department, availability or time, a leaver with time, someone deactivated before the range, never the Claude user; the Person options. |
| **RPT-054** | All of a person's time counts, on another department's tasks too; a `ProjectId` in the address is ignored. |
| **RPT-055** | Utilisation and unlogged per person, not netted across people; over 100%; *no availability*; under target at the person and the cell. |
| **RPT-056** | The tiles: available, unavailable, holidays, logged, utilisation, under target, logged nothing, unlogged across N people. |
| **RPT-057** | By person: ordered lowest utilisation first, no availability last; the Total row. |
| **RPT-058** | Timesheet by day: cells, highlights, *U*, holidays, `Over`, totals; the page's buckets and the PDF's 31-day cut. |
| **RPT-059** | Restricted tasks: a Department-scoped viewer sees their own department's tasks and tasks they can open in full, others by number and department only; an All-scope viewer sees every task in full. |
| **RPT-060** | The charts: the period chart's three series and its height, *Utilisation by person* lowest first and folded past twelve, tips; a 40-person, 400-day report's PDF builds. |

Then a **check in Chrome against the dev database**: set a part-timer's pattern from a date and see the grid change from that
day; add a half-day and a multi-day leave; a public holiday greyed; week and month views; a cell click opening the form; My
Time's line; the report under the Department and Person filters with a sprint picked, the grid, an old `Kind=TimeSpent` link,
the PDF; both themes; no console errors. What couldn't be checked is said.

## 10. Documentation

- **`orbit-spec.md`:**
  - §3: "no individual resource calendars or leave" qualified as **for scheduling**; availability for timesheets is §6.22.
  - §5.1: `WorkPattern` and `UnavailableTime`.
  - §6.5: the catalogue's `availability.manage` row (32 permissions), the Availability page door, and the upgrade note.
  - **§6.22 Availability**: §2 to §5 of this document, with its acceptance list.
  - §6.10: the My Time line; the *Time reports* line.
  - §10: `Reports__UtilisationTargetPercent`.
  - §12: the *Time utilisation* row and section in place of *Time spent*; the opening line's filters.
  - §13: decision **150**, recording §1's table and why.
  - §14: build step **134**.
- **`README.md`:** the features, the permission table, the reports list and the configuration.
- **`CLAUDE.md`:** `Application/Availability/*` added to the pure rules list.

## 11. Migration `AddAvailability`

- Creates `WorkPatterns` and `UnavailableTimes` (§2).
- **Default patterns:** every non-system user gets one row: the `WorkingCalendar` row's working weekdays (Monday to Friday if
  there is no row) at **480 minutes**, `EffectiveFrom` the UTC date of their `CreatedAt`. SQL can't read
  `HoursPerWorkingDay`, so 8 hours is written into the migration and noted in its comment; users created afterwards take
  `HoursPerWorkingDay` from configuration.
- **Grants:** `availability.manage` at **Own** for the non-built-in role named *Member* and at **Department** for *Department
  Admin*, where they exist, as `AddAssets` did.
- `Down` drops both tables and removes the grants.
- Tested on an empty database and on one at `AddNotPlanned`: forward, back and forward again (spec §13 item 37).

## 12. Build order

1. Data, migration, `AvailabilityRules` and its tests.
2. Permission, access and audit.
3. The person page, then the team grid, then the Users column and My Time's line.
4. The report: rename (one commit, behaviour unchanged), then people-centred counting, then the availability figures, grid,
   charts and PDF.
5. Spec, README and CLAUDE.md alongside each step; the Chrome check last.

## 13. Follow-ups, not in this change

- The Planning page's capacity and the Today page's *overloaded* flag reading availability instead of `HoursPerWorkingDay`.
- MCP tools to read availability and record unavailable time.
- A history of a person's department, if reports need it.
