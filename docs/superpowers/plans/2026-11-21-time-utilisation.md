# Time Utilisation and Availability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record each person's availability (a dated work pattern plus unavailable time) and turn the *Time spent* report into
*Time utilisation*: each person's available time against all the time they logged.

**Architecture:** Two new tables (`WorkPatterns`, `UnavailableTimes`) feed a pure rule (`AvailabilityRules`) that turns a person's
patterns, the organisation's holidays, their unavailable entries and their employment periods into available minutes per day.
An `AvailabilityService` serves two Blazor pages (a team grid and a person page) and My Time. `ReportingService` feeds the same
per-day availability into the renamed, person-centred `TimeUtilisationRules`.

**Tech Stack:** .NET 10, ASP.NET Core Blazor Server + MudBlazor, EF Core + PostgreSQL (Npgsql), xUnit, QuestPDF, `SvgCharts`.

**Spec:** `docs/superpowers/specs/2026-11-21-time-utilisation-design.md` (read it with this plan; § numbers below are the
design's unless they say `orbit-spec.md`).

## Global Constraints

- Namespaces start `Orbit.` (RootNamespace `Orbit`), never `Orbit.Web.`: `Orbit.Application.Availability`, `Orbit.Data.Entities`, `Orbit.Components.Pages.Availability`.
- British spelling in identifiers, copy and docs: *utilisation*, *organisation*, *colour*.
- Blazor components never inject a service: derive from `OrbitComponentBase`, call services in `Load(async s => ...)` / `Act(async s => ...)` with `s.Get<T>()`. Injecting `IOptions<T>` is fine (Today.razor does).
- All authorisation in services; throw `NotFoundException` / `ForbiddenException` / `ValidationException`.
- Every write is audited in the same unit of work: `audit.Add(...)` before the caller's `SaveChangesAsync`.
- Enums are stored by name (the existing convention in `ApplicationDbContext.OnModelCreating`), so raw SQL uses `'Leave'`.
- "Today" is the UTC date: `DateOnly.FromDateTime(DateTime.UtcNow)`.
- Date pickers take their `Date` from the address or the form's own state, never from loaded data (orbit-spec §13 item 143). Convert with `PickerDate`.
- Deletes confirm with a MudBlazor dialog, never a browser `confirm`.
- Reasons: `Leave, Sick, Training, Other`; short forms `Lve, Sick, Trn, Oth`. A reason or note never appears in a report, a PDF or an audit row.
- Target: `Reports:UtilisationTargetPercent`, default **80**, valid 1 to 100 (out of range → 80, warning logged at startup).
- Hours are entered in quarter hours, 0 to 24 a day; unavailable hours a day 0.25 to 24. Spans at most 366 days; notes at most 500 characters.
- Tests are xUnit over pure code, each test's doc comment cites its ID (`AVL-00n`, `RPT-0nn`, `NAV-00n`).
- If `dotnet build` fails with MSB3027 (an `Orbit.Web` process holds `bin/Debug`), add `-c Release` to every build and test command.
- Keep each touched file's line endings (`git ls-files --eol <file>`); most are CRLF in the working tree.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `orbit-spec.md` is updated in the same task as the code it describes (CLAUDE.md).

## Review Focus

Inputs the spec implies but its test list doesn't pin, most likely to bite first. Each has a test in the task named:

1. **Comma decimals.** Staff in a comma-decimal locale type `7,5`. Expected: read as 7.5 hours, the same as `7.5` (Task 3).
2. **Leave across a pattern change.** All-day leave from Friday 27 Feb to Tuesday 3 Mar 2026 with a Monday-and-Tuesday pattern from 1 March. Expected: each day removes that day's own pattern hours (8h, 0, 0, 8h, 8h) (Task 2).
3. **Leave across employment boundaries.** An entry that runs past a deactivation. Expected: days outside employment remove nothing, so the entry's *Removed* figure counts only employed days (Task 2).
4. **Removing the pattern in effect.** Expected: the previous pattern takes over from that date, and if the earliest one is removed the next becomes the one that covers earlier dates (Task 2).
5. **A person with time but no pattern** (created by a path that skipped the default). Expected: listed as *No work pattern*, utilisation *no availability*, sorted last, no division by zero (Task 11).

---

### Task 1: Permission and access rules

**Files:**
- Modify: `Orbit.Web/Application/Permission.cs`, `Orbit.Web/Application/PermissionCatalog.cs`, `Orbit.Web/Application/DefaultRoles.cs`, `Orbit.Web/Application/AccessPolicy.cs`
- Modify: `Orbit.Tests/Access/PermissionCatalogTests.cs:20` (31 → 32)
- Create: `Orbit.Tests/Access/AvailabilityAccessTests.cs`
- Docs: `orbit-spec.md` §6.5 (catalogue row, "(32 permissions)", the default grants, the upgrade paragraph naming `AddAvailability`)

**Interfaces:**
- Produces: `Permission.AvailabilityManage = "availability.manage"`; `PermissionCatalog.AvailabilityGroup = "Availability"`;
  `AccessPolicy.CanManageAvailability(Actor a, Guid personId, Guid? personDepartmentId) : bool`;
  `AccessPolicy.CanChangeWorkPattern(Actor a, Guid personId, Guid? personDepartmentId) : bool`.
  Lists of manageable people use the existing `Scoping.People(q, actor, Permission.AvailabilityManage)`.

- [ ] **Step 1: Write the failing tests** in `AvailabilityAccessTests.cs` (class doc: "Availability access (spec §6.22)")

```csharp
/// <summary>AVL-008: Own reaches yourself, Department your department's people and yourself, All everyone; Own never changes a pattern.</summary>
[Fact]
public void Managing_availability_follows_the_scope_and_patterns_need_department()
{
    var it = Guid.NewGuid(); var ops = Guid.NewGuid();
    var member = TestActors.Member(it);
    var admin = TestActors.DepartmentAdmin(it);
    var sys = TestActors.SystemAdmin();
    var colleague = Guid.NewGuid(); var outsider = Guid.NewGuid();

    Assert.True(AccessPolicy.CanManageAvailability(member, member.UserId!.Value, it));
    Assert.False(AccessPolicy.CanManageAvailability(member, colleague, it));
    Assert.False(AccessPolicy.CanChangeWorkPattern(member, member.UserId!.Value, it));

    Assert.True(AccessPolicy.CanManageAvailability(admin, colleague, it));
    Assert.True(AccessPolicy.CanChangeWorkPattern(admin, colleague, it));
    Assert.True(AccessPolicy.CanChangeWorkPattern(admin, admin.UserId!.Value, it));
    Assert.False(AccessPolicy.CanManageAvailability(admin, outsider, ops));
    Assert.False(AccessPolicy.CanManageAvailability(admin, outsider, null));

    Assert.True(AccessPolicy.CanChangeWorkPattern(sys, outsider, null));
    Assert.False(AccessPolicy.CanManageAvailability(TestActors.Nobody(it), member.UserId!.Value, it));
}

/// <summary>AVL-008: the catalogue entry allows Own, Department and All; Member gets Own and Department Admin Department.</summary>
[Fact]
public void The_permission_is_in_the_catalogue_with_its_default_grants()
{
    var p = PermissionCatalog.ByKey[Permission.AvailabilityManage];
    Assert.Equal([PermissionScope.Own, PermissionScope.Department, PermissionScope.All], p.AllowedScopes);
    Assert.Equal(PermissionCatalog.AvailabilityGroup, p.Group);
    Assert.False(p.SystemAdministratorOnly);
    Assert.Equal(PermissionScope.Own, DefaultRoles.MemberGrants[Permission.AvailabilityManage]);
    Assert.Equal(PermissionScope.Department, DefaultRoles.DepartmentAdminGrants[Permission.AvailabilityManage]);
}

/// <summary>AVL-008: the people a Department-scoped manager may pick are themselves and their department's active people.</summary>
[Fact]
public void Scoping_people_by_availability_reaches_the_department()
{
    var it = Guid.NewGuid();
    var admin = TestActors.DepartmentAdmin(it);
    ApplicationUser U(Guid? d, bool active = true, bool system = false) => new() { Id = Guid.NewGuid(), DepartmentId = d, IsActive = active, IsSystemAccount = system };
    var self = new ApplicationUser { Id = admin.UserId!.Value, DepartmentId = it, IsActive = true };
    var mate = U(it); var other = U(Guid.NewGuid()); var gone = U(it, active: false); var claude = U(it, system: true);
    var picked = Scoping.People(new[] { self, mate, other, gone, claude }.AsQueryable(), admin, Permission.AvailabilityManage).ToList();
    Assert.Equal([self.Id, mate.Id], picked.Select(u => u.Id));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Orbit.Tests --filter "FullyQualifiedName~AvailabilityAccessTests"`
Expected: FAIL to compile (`Permission.AvailabilityManage` not defined).

- [ ] **Step 3: Implement.**
  - `Permission.AvailabilityManage`.
  - `PermissionCatalog`: `AvailabilityGroup = "Availability"`; an entry after `TimeLog`: label *Manage availability*, description *"Record unavailable time (leave, sickness, training) and, from Department up, set work patterns: the hours a person is expected to work each weekday. Own = your own unavailable time; Department = your department's people; All = everyone."*, `OwnDeptAll`.
  - `DefaultRoles`: Member `Own`, Department Admin `Department`, each with a comment citing §6.22.
  - `AccessPolicy.CanManageAvailability`: by `a.ScopeOf(Permission.AvailabilityManage)`: All → true; Department → `personId == a.UserId || (personDepartmentId is Guid d && a.DepartmentId == d)`; Own → `personId == a.UserId`; else false. `CanChangeWorkPattern`: scope ≥ Department and `CanManageAvailability`.
  - `PermissionCatalogTests`: 32.

- [ ] **Step 4: Run the Access tests**

Run: `dotnet test Orbit.Tests --filter "FullyQualifiedName~Orbit.Tests.Access"`
Expected: PASS (all Access tests, including `DefaultRolesTests.Default_grants_are_valid_against_the_catalogue`).

- [ ] **Step 5: Update `orbit-spec.md` §6.5** as listed under Files.

- [ ] **Step 6: Commit** — `git commit -m "Availability: the availability.manage permission and its access rules"` (with the trailer).

---

### Task 2: The availability rule

**Files:**
- Modify: `Orbit.Web/Data/Entities/Enums.cs` (add `UnavailableReason { Leave, Sick, Training, Other }`, documented)
- Create: `Orbit.Web/Application/Models/AvailabilityModels.cs`
- Create: `Orbit.Web/Application/Availability/AvailabilityRules.cs`, `Orbit.Web/Application/Availability/EmploymentRules.cs`
- Create: `Orbit.Tests/Availability/AvailabilityRulesTests.cs`, `Orbit.Tests/Availability/EmploymentRulesTests.cs`

**Interfaces:**
- Produces (all in `Orbit.Application.Models` unless noted):

```csharp
public sealed record WeekMinutes(int Monday, int Tuesday, int Wednesday, int Thursday, int Friday, int Saturday, int Sunday)
{   public int For(DayOfWeek day);   public int Total { get; }   }
public sealed record PatternFacts(Guid Id, DateOnly EffectiveFrom, WeekMinutes Week);
public sealed record UnavailableFacts(Guid Id, DateOnly From, DateOnly To, int? MinutesPerDay, UnavailableReason Reason, DateTime CreatedAt);
public sealed record EmploymentPeriod(DateOnly From, DateOnly? To);           // To null = still employed
public sealed record EmploymentEvent(DateTime At, bool Active);               // Deactivated = false, Reactivated = true
public sealed record PersonAvailabilityFacts(Guid UserId, bool IsSystemAccount, IReadOnlyList<PatternFacts> Patterns,
    IReadOnlyList<UnavailableFacts> Unavailable, IReadOnlyList<EmploymentPeriod> Employment);
public sealed record RemovedPart(Guid EntryId, UnavailableReason Reason, int Minutes);
public sealed record AvailabilityDay(DateOnly Date, bool Employed, int PatternMinutes, int AvailableMinutes,
    IReadOnlyList<RemovedPart> Removed, string? Holiday)
{   public int UnavailableMinutes { get; }   public IReadOnlyDictionary<UnavailableReason, int> UnavailableByReason { get; }   }
```

  - `Orbit.Application.Availability.AvailabilityRules.Days(PersonAvailabilityFacts person, IReadOnlyDictionary<DateOnly, string> holidays, DateOnly from, DateOnly to) : IReadOnlyList<AvailabilityDay>` (one per date, both ends included; `holidays` holds only non-working exceptions, by name).
  - `AvailabilityRules.PatternOn(IReadOnlyList<PatternFacts> patterns, DateOnly day) : PatternFacts?`
  - `Orbit.Application.Availability.EmploymentRules.Periods(DateTime createdAtUtc, bool isActiveNow, IEnumerable<EmploymentEvent> events) : IReadOnlyList<EmploymentPeriod>`

- [ ] **Step 1: Write the failing tests.** Shared fixtures in `AvailabilityRulesTests`:

```csharp
private static readonly WeekMinutes FullTime = new(480, 480, 480, 480, 480, 0, 0);
private static readonly WeekMinutes MonTue = new(480, 480, 0, 0, 0, 0, 0);
private static readonly IReadOnlyDictionary<DateOnly, string> NoHolidays = new Dictionary<DateOnly, string>();
private static readonly EmploymentPeriod Always = new(new DateOnly(2020, 1, 1), null);
private static PersonAvailabilityFacts Person(PatternFacts[] patterns, UnavailableFacts[]? off = null, EmploymentPeriod[]? employment = null, bool system = false) =>
    new(Guid.NewGuid(), system, patterns, off ?? [], employment ?? [Always]);
private static PatternFacts P(int y, int m, int d, WeekMinutes w) => new(Guid.NewGuid(), new DateOnly(y, m, d), w);
private static UnavailableFacts Off(DateOnly from, DateOnly to, int? minutes, UnavailableReason reason, int order = 0) =>
    new(Guid.NewGuid(), from, to, minutes, reason, new DateTime(2026, 1, 1, 0, 0, order, DateTimeKind.Utc));
private static AvailabilityDay On(IReadOnlyList<AvailabilityDay> days, int y, int m, int d) => days.Single(x => x.Date == new DateOnly(y, m, d));
```

Tests (name → assertions; 23 Nov 2026 is a Monday, 24 Sep 2026 a Thursday):

| Test (ID) | Setup | Assertions |
|---|---|---|
| `The_pattern_in_effect_is_the_latest_on_or_before_the_day` (AVL-001) | patterns `P(2026,1,1,FullTime)`, `P(2026,3,1,MonTue)`; days 2025-12-29..2026-03-04 | Wed 2025-12-31 = 480 (first covers earlier); Wed 2026-02-25 = 480; Tue 2026-03-03 = 480; Wed 2026-03-04 = 0; `PatternOn(…, 2026-03-04).EffectiveFrom == 2026-03-01` |
| `A_saturday_worker_has_saturday_hours` (AVL-001) | `new WeekMinutes(0,0,0,0,0,240,0)` | Sat 2026-11-28 available 240, Mon 2026-11-23 0 |
| `Removing_the_pattern_in_effect_falls_back` (AVL-001, Review Focus 4) | only `P(2026,1,1,FullTime)`; then only `P(2026,3,1,MonTue)` | first: Wed 2026-03-04 = 480; second: Wed 2026-02-25 = 0 and Tue 2026-02-24 = 480 (the only pattern covers earlier days) |
| `A_holiday_is_zero_and_named` (AVL-002) | FullTime; holidays `{2026-09-24: "Heritage Day"}` | that day: `PatternMinutes` 480, `AvailableMinutes` 0, `UnavailableMinutes` 0, `Holiday == "Heritage Day"`; 2026-09-23 = 480 with `Holiday == null` |
| `Unavailable_time_is_subtracted_and_capped` (AVL-003) | FullTime; Leave all day 2026-11-24; Sick 180 on 11-25; Training 120 (order 0) and Other 420 (order 1) on 11-26 | 11-24: available 0, unavailable 480, by reason {Leave: 480}; 11-25: 300 / {Sick: 180}; 11-26: available 0, unavailable 480, by reason {Training: 120, Other: 360} |
| `Leave_across_a_pattern_change_takes_each_days_own_hours` (AVL-003, Review Focus 2) | `P(2026,1,1,FullTime)`, `P(2026,3,1,MonTue)`; Leave all day 2026-02-27..2026-03-03 | unavailable on 02-27 = 480, 02-28 = 0, 03-01 = 0, 03-02 = 480, 03-03 = 480; sum = 1440 |
| `Days_outside_employment_have_nothing` (AVL-004, Review Focus 3) | FullTime; employment `[(2026-11-01, 2026-11-24)]`; Leave all day 11-23..11-27 | 11-24: `Employed` true, unavailable 480; 11-25: `Employed` false, `PatternMinutes` 0, available 0, `Removed` empty |
| `System_accounts_and_people_without_a_pattern_have_no_availability` (AVL-005) | `system: true` with FullTime; and `Person([])` | every day available 0; system account days `Employed` false; no-pattern days `Employed` true, `PatternMinutes` 0 |

`EmploymentRulesTests` (AVL-004):
- `Periods_follow_deactivation_and_reactivation`: created `2026-03-10T14:00Z`, active now, events `[(2026-05-15T09:00Z, false), (2026-06-01T08:00Z, true)]` → `[(2026-03-10, 2026-05-15), (2026-06-01, null)]`.
- `An_inactive_account_with_no_deactivation_ends_on_its_creation_day`: created `2026-03-10T14:00Z`, `isActiveNow: false`, no events → `[(2026-03-10, 2026-03-10)]`.
- `Deactivated_and_reactivated_the_same_day_is_one_period`: events on 2026-05-15 false then true → `[(2026-03-10, null)]`.
- `Repeated_events_are_ignored`: a second `false` while inactive and a `true` while active change nothing.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Orbit.Tests --filter "FullyQualifiedName~Orbit.Tests.Availability"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement** the records and the two classes. `Days` follows §3's order: employment, then pattern (`PatternOn`; earliest pattern when none is on or before the day), then holiday, then the entries covering the day in `CreatedAt` order, each removing `min(remaining, MinutesPerDay ?? base)`. A part with 0 minutes isn't added to `Removed`. `EmploymentRules.Periods` sorts events by `At`, ignores no-change events, closes a period on the deactivation's UTC day, opens one on the reactivation's day, and merges periods that touch or overlap.

- [ ] **Step 4: Run the tests** — same command. Expected: PASS.

- [ ] **Step 5: Commit** — `"Availability: the rule that turns patterns, holidays, leave and employment into available minutes"`.

---

### Task 3: Input, grid and utilisation helpers

**Files:**
- Create: `Orbit.Web/Application/Availability/AvailabilityInputRules.cs`, `AvailabilityGridRules.cs`, `UtilisationRules.cs`
- Modify: `Orbit.Web/Application/Models/AvailabilityModels.cs` (grid records below)
- Create: `Orbit.Tests/Availability/AvailabilityInputRulesTests.cs`, `AvailabilityGridRulesTests.cs`, `UtilisationRulesTests.cs`

**Interfaces:**
- Consumes: `AvailabilityDay`, `WeekMinutes`, `UnavailableReason` (Task 2).
- Produces:
  - `AvailabilityInputRules`: `const int MaxSpanDays = 366, MaxNoteLength = 500, Step = 15, MaxDayMinutes = 1440`;
    `string? CheckUnavailable(DateOnly from, DateOnly to, int? minutesPerDay, string? note)`; `string? CheckPattern(WeekMinutes week)`;
    `string? CheckRemovePattern(int patternCount)`; `bool TryParseHours(string? text, out int minutes)`; `string FormatHours(int minutes)`;
    `string ShortReason(UnavailableReason r)`.
  - `enum AvailabilityView { Week, Month }` (Models).
  - `record AvailabilityCell(DateOnly Date, string Text, bool Reduced, bool Holiday, bool Blank, IReadOnlyList<string> Reasons, string Hover)` (Models).
  - `AvailabilityGridRules`: `(DateOnly From, DateOnly To) Range(AvailabilityView view, DateOnly anchor)`; `DateOnly Step(AvailabilityView view, DateOnly from, int direction)`; `AvailabilityCell Cell(AvailabilityDay day)`.
  - `UtilisationRules`: `int? Percent(int logged, int available)`; `bool UnderTarget(int logged, int available, int targetPercent)`; `string MyTimeLine(int logged, int available)`.

- [ ] **Step 1: Write the failing tests.**

```csharp
/// <summary>AVL-006: unavailable time's dates, hours and note are checked; the messages say what to fix.</summary>
[Fact]
public void Unavailable_time_is_checked()
{
    var d = new DateOnly(2026, 11, 23);
    Assert.Null(AvailabilityInputRules.CheckUnavailable(d, d, null, null));
    Assert.Equal("The end date can't be before the start date.", AvailabilityInputRules.CheckUnavailable(d, d.AddDays(-1), null, null));
    Assert.Null(AvailabilityInputRules.CheckUnavailable(d, d.AddDays(366), null, null));
    Assert.Equal("Unavailable time can cover at most 366 days.", AvailabilityInputRules.CheckUnavailable(d, d.AddDays(367), null, null));
    const string hours = "Hours a day must be between 15 minutes and 24 hours, in quarter hours.";
    Assert.Equal(hours, AvailabilityInputRules.CheckUnavailable(d, d, 0, null));
    Assert.Equal(hours, AvailabilityInputRules.CheckUnavailable(d, d, 50, null));
    Assert.Equal(hours, AvailabilityInputRules.CheckUnavailable(d, d, 1455, null));
    Assert.Null(AvailabilityInputRules.CheckUnavailable(d, d, 15, new string('x', 500)));
    Assert.Equal("The note can't exceed 500 characters.", AvailabilityInputRules.CheckUnavailable(d, d, null, new string('x', 501)));
}

/// <summary>AVL-006: a pattern's days are 0 to 24 hours in quarters (a 0-hour week is allowed); the only pattern can't be removed.</summary>
[Fact]
public void Patterns_are_checked()
{
    Assert.Null(AvailabilityInputRules.CheckPattern(new WeekMinutes(0, 0, 0, 0, 0, 0, 0)));
    Assert.Equal("Each day's hours must be between 0 and 24, in quarter hours.", AvailabilityInputRules.CheckPattern(new WeekMinutes(470, 0, 0, 0, 0, 0, 0)));
    Assert.Equal("A person needs a work pattern: change it instead of removing their only one.", AvailabilityInputRules.CheckRemovePattern(1));
    Assert.Null(AvailabilityInputRules.CheckRemovePattern(2));
}

/// <summary>AVL-006 and Review Focus 1: hours read with a point or a comma, in quarters, 0 to 24.</summary>
[Theory]
[InlineData("7.5", 450)] [InlineData("7,5", 450)] [InlineData("7.25", 435)] [InlineData(" 8 ", 480)] [InlineData("0", 0)] [InlineData("24", 1440)]
public void Hours_are_read(string text, int minutes) { Assert.True(AvailabilityInputRules.TryParseHours(text, out var m)); Assert.Equal(minutes, m); }

[Theory]
[InlineData("7.3")] [InlineData("24.25")] [InlineData("-1")] [InlineData("abc")] [InlineData("")] [InlineData(null)]
public void Bad_hours_are_refused(string? text) => Assert.False(AvailabilityInputRules.TryParseHours(text, out _));
```

Plus: `FormatHours(480) == "8h"`, `FormatHours(450) == "7.5h"`, `FormatHours(435) == "7.25h"`, `FormatHours(0) == "0h"`; `ShortReason` gives `Lve, Sick, Trn, Oth`.

`AvailabilityGridRulesTests` (AVL-007):
- `Range(Week, 2026-11-25) == (2026-11-23, 2026-11-29)`; `Range(Month, 2026-11-25) == (2026-11-01, 2026-11-30)`; `Step(Week, 2026-11-23, -1) == 2026-11-16`; `Step(Month, 2026-11-01, 1) == 2026-12-01`.
- `Cell`, from hand-built `AvailabilityDay`s: a full day → `Text "8h"`, not `Reduced`, `Hover "Pattern 8h · 8h available"`; a day with Leave 180 → `Text "5h"`, `Reduced`, `Reasons ["Lve"]`, `Hover "Pattern 8h · Leave 3h → 5h available"`; a holiday → `Holiday`, `Text "0h"`, `Hover "Public holiday: Heritage Day"`; a pattern-0 day → `Text "-"`, `Hover "Not a working day"`; not employed → `Blank`, `Text ""`, `Hover "Not employed"`.

`UtilisationRulesTests` (AVL-009):
- `Percent(1920, 2400) == 80`; `Percent(2700, 2400) == 113` (112.5 rounds away from zero; never clamped); `Percent(60, 0) == null`; `Percent(0, 0) == null`.
- `UnderTarget(1919, 2400, 80)` true; `UnderTarget(1920, 2400, 80)` false; `UnderTarget(0, 0, 80)` false.
- `MyTimeLine(1920, 2400) == "Logged 32h of 40h available · 80%"`; `MyTimeLine(180, 0) == "Logged 3h · no availability in this range"`.

- [ ] **Step 2: Run to verify they fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~Orbit.Tests.Availability"`. Expected: FAIL to compile.

- [ ] **Step 3: Implement.** `TryParseHours` trims, accepts `.` or `,` as the decimal mark (parse with `CultureInfo.InvariantCulture` after replacing `,`), requires a multiple of 0.25 in 0..24. `Percent` is `Math.Round(logged * 100.0 / available, MidpointRounding.AwayFromZero)`, not `Percentage.Of` (that clamps to 100). `MyTimeLine` writes times with `TimeFormat.Minutes`.

- [ ] **Step 4: Run the tests** — expected PASS.

- [ ] **Step 5: Commit** — `"Availability: input checks, the team grid's cells and the utilisation figures"`.

---

### Task 4: Tables, migration, configuration and the default pattern

**Files:**
- Create: `Orbit.Web/Data/Entities/WorkPattern.cs` (`WorkPattern`), `Orbit.Web/Data/Entities/UnavailableTime.cs` (`UnavailableTime`), fields exactly as design §2.1 and §2.2, each `User` navigation
- Create: `Orbit.Web/Application/Availability/WorkPatterns.cs`
- Modify: `Orbit.Web/Data/ApplicationDbContext.cs` (two `DbSet`s and their configuration), `Orbit.Web/Application/Models/AuditModels.cs` (actions), `Orbit.Web/Application/OrbitOptions.cs` (`ReportOptions`), `Orbit.Web/Program.cs`, `Orbit.Web/appsettings.json`
- Modify: `Orbit.Web/Application/Services/UserAdminService.cs` (`CreateAsync`), `Orbit.Web/Data/DbInitializer.cs` (seeded admin)
- Create: migration `AddAvailability` (generated, then edited), `Orbit.Tests/Availability/WorkPatternsTests.cs`
- Docs: `orbit-spec.md` §5.1 (both entities), §10 (`Reports__UtilisationTargetPercent`, default 80)

**Interfaces:**
- Produces: `DbSet<WorkPattern> WorkPatterns`, `DbSet<UnavailableTime> UnavailableTimes`;
  `WorkPattern.Week : WeekMinutes` (not mapped, from the seven columns); `AuditAction.WorkPatternSet`, `WorkPatternRemoved`,
  `UnavailableAdded`, `UnavailableUpdated`, `UnavailableRemoved` (string constants equal to their names);
  `ReportOptions { const string Section = "Reports"; int UtilisationTargetPercent = 80; int EffectiveTarget { get; } }`;
  `WorkPatterns.Default(Guid userId, IReadOnlySet<DayOfWeek> workingDays, int hoursPerDay, DateOnly effectiveFrom) : WorkPattern`.

- [ ] **Step 1: Write the failing test** (`WorkPatternsTests`)

```csharp
/// <summary>AVL-001 (§11): a new person's pattern is the organisation's working days times a working day, dated from their account.</summary>
[Fact]
public void The_default_pattern_is_the_working_week_times_a_working_day()
{
    var id = Guid.NewGuid();
    var p = WorkPatterns.Default(id, new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday }, 7, new DateOnly(2026, 11, 21));
    Assert.Equal((id, new DateOnly(2026, 11, 21)), (p.UserId, p.EffectiveFrom));
    Assert.Equal(new WeekMinutes(420, 420, 420, 420, 0, 0, 0), p.Week);
}
```

Also: `new ReportOptions { UtilisationTargetPercent = 0 }.EffectiveTarget == 80`, `= 101` → 80, `= 75` → 75.

- [ ] **Step 2: Run to verify it fails** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~WorkPatternsTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Implement the entities, `WorkPatterns.Default`, `ReportOptions`, audit constants and DbContext configuration.** Configuration: FKs to `AspNetUsers` with `DeleteBehavior.Cascade`; `CreatedById` / `UpdatedById` with `SetNull`; unique index (`UserId`, `EffectiveFrom`); index (`UserId`, `From`) on `UnavailableTimes`; `Note` max length 500; `Week` ignored. Register `builder.Services.Configure<ReportOptions>(config.GetSection(ReportOptions.Section))` next to `CriticalPathOptions`, add `"Reports": { "UtilisationTargetPercent": 80 }` to `appsettings.json`, and after `app` is built log a warning when the configured value is outside 1..100.

- [ ] **Step 4: Wire the default pattern into user creation.** In `UserAdminService.CreateAsync`, after the user is created and before the save that persists the audit row, add `WorkPatterns.Default(user.Id, calendar.WorkingDays, criticalPath.HoursPerWorkingDay, DateOnly.FromDateTime(user.CreatedAt))`, where `calendar` is the `WorkingCalendars` row by `WellKnownIds.WorkingCalendarId` or `new WorkingCalendar()`, and `criticalPath` is a new `IOptions<CriticalPathOptions>` constructor parameter. *Import from directory* calls `CreateAsync`, so it's covered. In `DbInitializer`, give the seeded admin the same pattern after `CreateAsync` succeeds and save.

- [ ] **Step 5: Generate and edit the migration.**

Run: `dotnet ef migrations add AddAvailability --project Orbit.Web`

At the end of `Up`, add two data steps. The comment says SQL can't read `HoursPerWorkingDay`, so 8 hours is written in:

```sql
INSERT INTO "WorkPatterns" ("Id", "UserId", "EffectiveFrom", "MondayMinutes", "TuesdayMinutes", "WednesdayMinutes",
    "ThursdayMinutes", "FridayMinutes", "SaturdayMinutes", "SundayMinutes", "CreatedAt", "CreatedById")
SELECT gen_random_uuid(), u."Id", (u."CreatedAt" AT TIME ZONE 'UTC')::date,
    CASE WHEN COALESCE(c."MondayWorking", TRUE) THEN 480 ELSE 0 END, CASE WHEN COALESCE(c."TuesdayWorking", TRUE) THEN 480 ELSE 0 END,
    CASE WHEN COALESCE(c."WednesdayWorking", TRUE) THEN 480 ELSE 0 END, CASE WHEN COALESCE(c."ThursdayWorking", TRUE) THEN 480 ELSE 0 END,
    CASE WHEN COALESCE(c."FridayWorking", TRUE) THEN 480 ELSE 0 END, CASE WHEN COALESCE(c."SaturdayWorking", FALSE) THEN 480 ELSE 0 END,
    CASE WHEN COALESCE(c."SundayWorking", FALSE) THEN 480 ELSE 0 END, now(), NULL
FROM "AspNetUsers" u
LEFT JOIN "WorkingCalendars" c ON c."Id" = '{WellKnownIds.WorkingCalendarId}'
WHERE NOT u."IsSystemAccount";
```

Then the grant step: `AddAssets`'s `INSERT INTO "RolePermissions" … JOIN (VALUES …) … WHERE NOT r."IsBuiltIn" ON CONFLICT DO NOTHING` with `('Member', 'availability.manage', 'Own'), ('Department Admin', 'availability.manage', 'Department')`. At the start of `Down`, add `DELETE FROM "RolePermissions" WHERE "Permission" = 'availability.manage';` before the generated table drops. If `CreatedAt` is `timestamp without time zone` in the snapshot, use `u."CreatedAt"::date` instead.

- [ ] **Step 6: Build and test**

Run: `dotnet build Orbit.slnx` then `dotnet test Orbit.slnx`. Expected: build succeeds, all tests pass.

- [ ] **Step 7: Verify the migration** (CLAUDE.md; orbit-spec §13 item 37). Set `ConnectionStrings__DefaultConnection` to a scratch database, never the dev one. Use (a) an empty database and (b) a copy of the dev database (`CREATE DATABASE orbit_migtest TEMPLATE orbit;`, with the app stopped). For each:
  - `dotnet ef database update --project Orbit.Web` → every non-system user has one `WorkPatterns` row (`SELECT count(*) FROM "AspNetUsers" WHERE NOT "IsSystemAccount"` equals `SELECT count(DISTINCT "UserId") FROM "WorkPatterns"`), and the *Member* / *Department Admin* roles have the grant.
  - `dotnet ef database update AddNotPlanned --project Orbit.Web` → both tables and the grants are gone.
  - Forward again → as the first step. Drop the scratch databases afterwards.

- [ ] **Step 8: Docs, then commit** — `orbit-spec.md` §5.1 and §10; `"Availability: work pattern and unavailable time tables, default patterns and the utilisation target"`.

---

### Task 5: Availability service

**Files:**
- Create: `Orbit.Web/Application/Services/AvailabilityFactsReader.cs`, `Orbit.Web/Application/Services/AvailabilityService.cs`
- Modify: `Orbit.Web/Application/Models/AvailabilityModels.cs` (inputs and views below), `Orbit.Web/Program.cs` (`AddScoped` both, beside `WorkingCalendarService`)

**Interfaces:**
- Consumes: Tasks 1 to 4.
- Produces:

```csharp
public sealed record WorkPatternInput(DateOnly EffectiveFrom, WeekMinutes Week);
public sealed record UnavailableInput(DateOnly From, DateOnly To, int? MinutesPerDay, UnavailableReason Reason, string? Note);
public enum UnavailableFilter { Upcoming, Past, All }
public sealed record AvailabilityPerson(Guid Id, string Name, string? DepartmentName);
public sealed record AvailabilityGridRow(AvailabilityPerson Person, IReadOnlyList<AvailabilityCell> Cells, int AvailableMinutes);
public sealed record AvailabilityGrid(AvailabilityView View, DateOnly From, DateOnly To, IReadOnlyList<AvailabilityGridRow> Rows, bool CanChooseDepartment, Guid? DepartmentId, bool OwnOnly);
public sealed record PatternHistoryRow(Guid Id, DateOnly EffectiveFrom, WeekMinutes Week, string SetBy, DateTime SetAt);
public sealed record UnavailableRow(Guid Id, DateOnly From, DateOnly To, int? MinutesPerDay, UnavailableReason Reason, string? Note, int RemovedMinutes);
public sealed record PersonAvailability(AvailabilityPerson Person, int WeeklyMinutesToday, WeekMinutes? Current,
    IReadOnlyList<PatternHistoryRow> History, IReadOnlyList<UnavailableRow> Unavailable, bool CanChangePattern);
```

  - `AvailabilityFactsReader(ApplicationDbContext db)`: `Task<IReadOnlyDictionary<Guid, PersonAvailabilityFacts>> ReadAsync(IReadOnlyCollection<Guid> userIds, DateOnly from, DateOnly to, CancellationToken ct)` (every pattern of those users, entries overlapping the range, employment from `CreatedAt`, `IsActive` and the `AuditEntity.User` rows with action `Deactivated` / `Reactivated`); `Task<IReadOnlyDictionary<DateOnly, string>> HolidaysAsync(DateOnly from, DateOnly to, CancellationToken ct)` (non-working exceptions only).
  - `AvailabilityService(ApplicationDbContext db, IActorProvider actors, AuditService audit, AvailabilityFactsReader facts)`:
    `GridAsync(AvailabilityView view, DateOnly anchor, Guid? departmentId, CancellationToken ct = default) : Task<AvailabilityGrid>`;
    `PersonAsync(Guid userId, UnavailableFilter filter, CancellationToken ct = default) : Task<PersonAvailability>`;
    `SetPatternAsync(Guid userId, WorkPatternInput input, CancellationToken ct = default) : Task`;
    `RemovePatternAsync(Guid patternId, CancellationToken ct = default) : Task`;
    `AddUnavailableAsync(Guid userId, UnavailableInput input, CancellationToken ct = default) : Task<Guid>`;
    `UpdateUnavailableAsync(Guid id, UnavailableInput input, CancellationToken ct = default) : Task`;
    `RemoveUnavailableAsync(Guid id, CancellationToken ct = default) : Task`;
    `MineAsync(DateOnly from, DateOnly to, CancellationToken ct = default) : Task<int?>` (the actor's own available minutes; null with no user);
    `WeeklyMinutesTodayAsync(CancellationToken ct = default) : Task<IReadOnlyDictionary<Guid, int>>` (people in the actor's `Scoping.People` reach).

- [ ] **Step 1: Implement the reader and the service.** Rules each method enforces:
  - Grid: people from `Scoping.People(db.Users, actor, Permission.AvailabilityManage)`, then the department (ignored unless the actor holds the permission at All), by name. `OwnOnly` is true when the scope is Own (the page redirects). Cells come from `AvailabilityGridRules.Cell` over `AvailabilityRules.Days`.
  - Person: `NotFoundException("Person not found.")` for an unknown or system user; `ForbiddenException("You can't manage this person's availability.")` unless `CanManageAvailability`. `Upcoming`: `To >= today` by `From`; `Past`: `To < today` by `From` descending; `All`: by `From` descending. `RemovedMinutes` is the sum of that entry's `RemovedPart`s over its own range.
  - Writes: refuse with the `AvailabilityInputRules` message (`ValidationException`); a pattern needs `CanChangeWorkPattern` (`"You can't change this person's work pattern."`). `SetPatternAsync` replaces a row on the same date (audit `replaced: true`). `RemovePatternAsync` refuses the only one. Each write calls `audit.Add(actor, AuditEntity.User, personId, AuditAction.…, person.DepartmentId, person.DisplayName, details)` with details per design §4 (dates and minutes only), then `SaveChangesAsync`. Updates set `UpdatedAt` / `UpdatedById`; inserts set `CreatedAt` / `CreatedById`. Unknown rows → `NotFoundException("Work pattern not found.")` / `("Unavailable time not found.")`.
  - Mine: needs no permission (your own availability).

- [ ] **Step 2: Build and run every test** — `dotnet build Orbit.slnx` and `dotnet test Orbit.slnx`. Expected: success. (DB code is verified through the pages in Tasks 6 to 8.)

- [ ] **Step 3: Commit** — `"Availability: the service behind the pages and My Time"`.

---

### Task 6: The person's availability page

**Files:**
- Create: `Orbit.Web/Components/Pages/Availability/PersonAvailabilityPage.razor`, `WorkPatternDialog.razor`, `UnavailableTimeDialog.razor`, `_Imports.razor` (`@attribute [Authorize]`, as the other folders)
- Modify: `Orbit.Web/wwwroot/css/app.css` (`orbit-availability-*`)

**Interfaces:**
- Consumes: `AvailabilityService.PersonAsync / SetPatternAsync / RemovePatternAsync / AddUnavailableAsync / UpdateUnavailableAsync / RemoveUnavailableAsync`, `AvailabilityInputRules.TryParseHours / FormatHours`.
- Produces: route `/Availability/{UserId:guid}`, query keys `Show` (`upcoming` default, `past`, `all`) and `Add` (a date, opening the add dialog for it).

- [ ] **Step 1: Build the page** per design §5.3: `@attribute [RequirePermission(Permission.AvailabilityManage)]`, `@inherits OrbitComponentBase`, `[SupplyParameterFromQuery] public string? Show / Add`. Header (name, department, *16h a week*); the pattern card with seven days and the total, and **Change from…** only when `CanChangePattern`; history with **Remove** while there's more than one row; the unavailable table with filter chips, **Add**, **Edit**, **Remove** (MudBlazor confirm dialog). The dialogs use text fields for hours (`inputmode` passed as `InputMode="@(InputMode.@decimal)"`), read them with `TryParseHours`. *Change from…*'s date defaults to today, and its hours pre-fill from the pattern in effect on that date (refilled when the date changes). Writes go through `Act`, then the page reloads.

- [ ] **Step 2: Run the app and check it** — `dotnet run --project Orbit.Web`, signed in as a System Administrator on the dev database: set a Monday-and-Tuesday pattern from a date; replace it on the same date; remove a newer row; add a half day and a multi-day leave; edit and remove one; try *To* before *From* (snackbar with the message); open `?Add=2026-11-24` (dialog open on that day). As a Member, open your own page (no *Change from…*) and someone else's (refusal). Both themes, no console errors.

- [ ] **Step 3: Commit** — `"Availability: a person's page with their work pattern and unavailable time"`.

---

### Task 7: The team grid and the menu item

**Files:**
- Create: `Orbit.Web/Components/Pages/Availability/AvailabilityGridPage.razor`
- Modify: `Orbit.Web/Application/Navigation.cs`, `Orbit.Tests/Access/NavigationTests.cs`, `Orbit.Web/wwwroot/css/app.css`
- Docs: `orbit-spec.md` new **§6.22 Availability** (design §2 to §5, with its acceptance list AVL-001 to AVL-009); `orbit-spec.md` §6.5 page doors and menu paragraph (*Availability* with `availability.manage`)

**Interfaces:**
- Consumes: `AvailabilityService.GridAsync`, `AvailabilityGridRules.Range / Step`.
- Produces: route `/Availability`; `Navigation.ShowsAvailability(Actor a) : bool`; menu item `new("Availability", "/Availability", ShowsAvailability)` after *My Time*.

- [ ] **Step 1: Update the failing NAV tests.** In `Shipped_roles_see_their_full_menus` (NAV-001) the full menu becomes `["Dashboard", "Today", "Tasks", "Projects", "Sprints", "Recurring", "My Time", "Availability", "Assets", "Requests"]`. In `Menu_links_are_the_urls_the_pages_answer_on` (NAV-005) add `Assert.Equal("/Availability", hrefs["Availability"]);`. Add to NAV-002 a role holding only `availability.manage` at Own, whose menu is `["Dashboard", "Availability"]`.

- [ ] **Step 2: Run to verify they fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~NavigationTests"`. Expected: FAIL.

- [ ] **Step 3: Implement** `ShowsAvailability` (`a.Has(Permission.AvailabilityManage)`) and the menu item. Then build the page per design §5.2. It inherits `OrbitListPage`: `MemoryKey "availability"`, `FilterKeys ["View", "DepartmentId"]`, `CarriedKeys ["From"]`; ◀ ▶ use `GoCarried("From", …)` with `AvailabilityGridRules.Step`. When `OwnOnly`, `Nav.NavigateTo($"/Availability/{me}", replace: true)`. The table has a fixed name column and scrolls inside an `orbit-table-wrap`. Each cell shows `Text`, the reasons beneath (week view only), the `Hover` as its `title`, and classes for reduced, holiday and blank. A name links to `/Availability/{id}`, and a cell to `/Availability/{id}?Add={date:yyyy-MM-dd}`.

- [ ] **Step 4: Run the tests** — `dotnet test Orbit.slnx`. Expected: PASS.

- [ ] **Step 5: Check in the app**: week and month, ◀ ▶, the department filter as a System Administrator (remembered after leaving and coming back), a holiday from Admin > Working Calendar greyed, a shaded leave day with its reason and hover, a cell click opening the add dialog, a Member sent to their own page, and a phone width (the grid scrolls in its box).

- [ ] **Step 6: Docs, then commit** — `"Availability: the team grid and its menu item"`.

---

### Task 8: Weekly hours on the Users list and the My Time line

**Files:**
- Modify: `Orbit.Web/Components/Pages/Admin/Users/UsersList.razor`, `Orbit.Web/Components/Pages/Time/MyTime.razor`
- Docs: `orbit-spec.md` §6.10 (*My time* bullet), §6.5 (Users list column)

**Interfaces:**
- Consumes: `AvailabilityService.WeeklyMinutesTodayAsync`, `AvailabilityService.MineAsync`, `UtilisationRules.MyTimeLine / UnderTarget`, `IOptions<ReportOptions>.Value.EffectiveTarget`.

- [ ] **Step 1: Users list:** load the dictionary in the same `Load` as the users and add a **Weekly hours** column (`TimeFormat.Minutes`, `-` when absent), linked to `/Availability/{id}`.
- [ ] **Step 2: My Time:** load `MineAsync(from, to)` in the existing `Load`. Above the Entries card, show `UtilisationRules.MyTimeLine(s.TotalMinutes, available)`, with the warning colour when `UnderTarget(…, target)`. Show nothing when `MineAsync` returns null.
- [ ] **Step 3: Check in the app**: the column on Admin > Users; My Time for this week, then for a range with leave in it (the available figure drops).
- [ ] **Step 4: Docs, then commit** — `"Availability: weekly hours on the Users list and logged against available on My Time"`.

---

### Task 9: Rename Time spent to Time utilisation (no behaviour change)

**Files:**
- Rename with `git mv`: `Application/TimeSpentRules.cs` → `TimeUtilisationRules.cs`; `Components/Pages/Reports/TimeSpentCharts.cs` → `TimeUtilisationCharts.cs`; `ReportTimeSpent.razor` → `ReportTimeUtilisation.razor`; `Orbit.Tests/Reports/TimeSpentRulesTests.cs` → `TimeUtilisationRulesTests.cs`; `TimeSpentChartsTests.cs` → `TimeUtilisationChartsTests.cs`
- Modify: `ReportModels.cs`, `ReportingService.cs`, `ReportRun.cs`, `ReportView.razor`, `ReportPdfEndpoint.cs`, `ReportText.cs`, `ReportsIndex.razor`, `Orbit.Tests/Tasks/FilterQueryTests.cs`

**Interfaces:**
- Produces: `ReportKind.TimeUtilisation` (replacing `TimeSpent` in the same position); every `TimeSpent*` type becomes `TimeUtilisation*` (`TimeUtilisationEntry`, `…PersonTime`, `…TaskFacts`, `…ProjectFacts`, `…Summary`, `…PersonRow`, `…TaskPerson`, `…TaskRow`, `…Report`, `…ProjectRow`, `…TypeRow`, `…SourceRow`); `ReportingService.TimeUtilisationAsync`; `ReportRun.TimeUtilisation`; `ReportPdfEndpoint.TimeUtilisationSections`. `TimeBucket` and `TimeBucketSize` keep their names.

- [ ] **Step 1: Update the failing address test** (`FilterQueryTests`): RPT-052 becomes a theory over `"TimeByPerson"`, `" timebyperson "`, `"TimeSpent"`, `"timespent"`, each opening `ReportKind.TimeUtilisation` with dates and department kept. `A_report_address_reads_its_person` uses `"TimeUtilisation"`.
- [ ] **Step 2: Run to verify it fails** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~FilterQueryTests"`. Expected: FAIL to compile.
- [ ] **Step 3: Rename** the files, types, members, the catalogue title (*Time utilisation*; description unchanged until Task 10), and the doc comments' report name. In `ReportAddress.Parse`, map both retired kinds (`"TimeByPerson"`, `"TimeSpent"`, trimmed, any case) to `ReportKind.TimeUtilisation`. The PDF file name follows from the kind (`orbit-timeutilisation-…`).
- [ ] **Step 4: Run every test** — `dotnet test Orbit.slnx`. Expected: PASS with the same count as before plus the new theory rows.
- [ ] **Step 5: Commit** — `"Time utilisation: rename Time spent, old links kept"`.

---

### Task 10: Person-centred counting

**Files:**
- Modify: `TimeUtilisationRules.cs`, `ReportModels.cs` (task facts and row, catalogue entry), `ReportingService.cs`, `TimeUtilisationRulesTests.cs`
- Modify: `ReportTimeUtilisation.razor` and `ReportPdfEndpoint.cs` (restricted task rows only)

**Interfaces:**
- Produces:
  - `TimeUtilisationTaskFacts` gains `string DepartmentName, bool Restricted` (appended).
  - `TimeUtilisationTaskRow` gains `string DepartmentName, bool Restricted` (appended); a restricted row's `Title` is `""`.
  - `TimeUtilisationRules.Build(DateOnly from, DateOnly to, IEnumerable<TimeUtilisationEntry> entries, IReadOnlyDictionary<Guid, TimeUtilisationTaskFacts> tasks, IEnumerable<TimeUtilisationPersonTime> toDate, IReadOnlyDictionary<Guid, TimeUtilisationProjectFacts> projects, IReadOnlyDictionary<Guid, ReportPerson> people, IReadOnlySet<Guid> employed, Guid? personId)`. `people` = everyone in the department scope (§6.3); `employed` = those employed on any day of the range. A person is in the report when employed or with time; entries of anyone not in `people` are dropped. `Summary.LoggedNothing` counts the employed with no time.
  - Catalogue: `ByProject: false`, description *"How much of each person's available time they logged in the period, day by day, and where the logged time went: by project, task type and source, then every task worked on."*

- [ ] **Step 1: Write the failing tests** (replace the Run helper to pass `people` and `employed`; retire the `covered` tests):

| Test (ID) | Setup | Assertions |
|---|---|---|
| `People_in_the_report_are_employed_or_logged_time` (RPT-053) | people Alice, Bob, Carol, Dan; employed {Alice, Bob}; entries Alice 60, Carol 30, and Eve 45 (Eve not in `people`) | `People` names = Alice, Carol, Bob (time first, then 0); Dan absent; Eve's 45 not counted; `PersonOptions` = Alice, Bob, Carol by name; `Summary.LoggedNothing == 1` |
| `All_of_a_persons_time_counts_on_any_departments_tasks` (RPT-054) | Alice's entries on a task with `DepartmentName "Marketing"` and one with `"IT"` | `Summary.Minutes` is both; `ReportCatalog.Get(ReportKind.TimeUtilisation).ByProject == false` |
| `Person_options_come_before_the_person_filter` (RPT-045) | as the existing RPT-045, with `people`/`employed` | options are everyone in the report; the filter keeps that person's time; `TotalMinutes` stays everyone's; someone not in `people` → `Person == null`, no rows |
| `Restricted_tasks_are_shown_by_number_and_department` (RPT-059) | one task `Restricted: true`, `DepartmentName "Marketing"`, title "Secret"; one not restricted | restricted row: `Title == ""`, `Number` kept, `DepartmentName == "Marketing"`, minutes counted; the other keeps its title |

- [ ] **Step 2: Run to verify they fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~TimeUtilisationRulesTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Implement the rules,** then `ReportingService.TimeUtilisationAsync`:
  - The department is `RestrictedDepartment(actor) ?? f.DepartmentId`. People: `db.Users` where not a system account and (no department chosen, or `DepartmentId == d`), inactive included, as `ReportPerson`.
  - Entries: `db.TimeEntries` for those user ids, dated in the range, grouped by task, user and date. No task-department or project filter.
  - Task facts as today, plus the department's name. `Restricted = RestrictedDepartment(actor) is Guid r && task.DepartmentId != r && !visible.Contains(task.Id)`, where `visible` = `Scoping.Tasks(db.Tasks.Where(t => taskIds.Contains(t.Id)), actor)` ids.
  - `employed`: from `AvailabilityFactsReader.ReadAsync` (people with any employment period overlapping the range). Inject the reader into `ReportingService`.
  - Delete `PeopleAsync`, which is now unused.
  - Restricted rows in `ReportTimeUtilisation.razor` and the PDF: `T-26-00012 · Marketing`, no link, the project name as plain text.

- [ ] **Step 4: Run every test** — `dotnet test Orbit.slnx`. Expected: PASS (RPT-044, 048, 049, 050 unchanged).

- [ ] **Step 5: Commit** — `"Time utilisation: people by home department and all their time; restricted tasks; no project filter"`.

---

### Task 11: Availability in the report

**Files:**
- Modify: `TimeUtilisationRules.cs`, `ReportModels.cs`, `ReportingService.cs`, `TimeUtilisationRulesTests.cs`

**Interfaces:**
- Consumes: `AvailabilityDay`, `AvailabilityRules.Days`, `UtilisationRules.Percent / UnderTarget`, `ReportOptions.EffectiveTarget`.
- Produces:

```csharp
public sealed record ReportAvailability(bool HasPattern, IReadOnlyList<AvailabilityDay> Days);
public sealed record TimeBucket(DateOnly From, DateOnly To, int Minutes = 0, int AvailableMinutes = 0, int UnloggedMinutes = 0, int OverMinutes = 0);
public sealed record TimeUtilisationSummary(int Minutes, int AvailableMinutes, int UnavailableMinutes, int HolidayDays, int? UtilisationPercent,
    int UnderTarget, int People, int LoggedNothing, int UnloggedMinutes, int UnloggedPeople, int Tasks, int UnestimatedTasks, EstimateComparison Estimate);
public sealed record TimeUtilisationPersonRow(Guid UserId, string Name, bool IsActive, bool HasPattern, int AvailableMinutes,
    int UnavailableMinutes, int Minutes, int Tasks, bool UnderTarget)
{   public int UnloggedMinutes { get; }   public int OverMinutes { get; }   public int? UtilisationPercent { get; }   public bool LoggedNothing { get; }   }
public sealed record TimeUtilisationCell(DateOnly From, DateOnly To, int Minutes, int AvailableMinutes, int UnavailableMinutes, bool AllHoliday, bool UnderTarget)
{   public bool Unavailable { get; }   public bool Over { get; }   public bool Empty { get; }   }   // Over: logged with no availability; Empty: neither, not holiday
public sealed record TimeUtilisationGridRow(Guid UserId, string Name, IReadOnlyList<TimeUtilisationCell> Cells, int Minutes, int AvailableMinutes)
{   public int? UtilisationPercent { get; }   }
```

  - `TimeUtilisationReport` gains `int TargetPercent`, `TimeBucketSize GridBucketSize`, `IReadOnlyList<TimeUtilisationGridRow> Grid`, `TimeBucketSize PdfGridBucketSize`, `IReadOnlyList<TimeUtilisationGridRow> PdfGrid`.
  - `TimeUtilisationRules.Buckets(DateOnly from, DateOnly to, int maxDailyDays = MaxDailyDays)` and `const int PdfMaxDailyDays = 31`.
  - `Build(...)`: `IReadOnlySet<Guid> employed` is replaced by `IReadOnlyDictionary<Guid, ReportAvailability> availability, int targetPercent`. Employed = any day `Employed`; a person missing from the dictionary has no pattern and no availability.

- [ ] **Step 1: Write the failing tests.** Window Mon 2026-11-23 to Fri 2026-11-27; `Avail(WeekMinutes week, UnavailableFacts[]? off = null, IReadOnlyDictionary<DateOnly,string>? holidays = null)` builds a `ReportAvailability` with `AvailabilityRules.Days`; target 80.

| Test (ID) | Setup | Assertions |
|---|---|---|
| `Utilisation_and_unlogged_are_per_person_not_netted` (RPT-055) | Alice FullTime 1920 logged; Bob FullTime 2700; Dan FullTime 1900 | Alice 80%, unlogged 480, not under; Bob 113%, unlogged 0, over 300; Dan 79%, under; `Summary.UnloggedMinutes == 480 + 500`; `Summary.UnderTarget == 1`; `Summary.UtilisationPercent == Percent(6520, 7200)` |
| `A_person_with_time_but_no_pattern` (RPT-055, Review Focus 5) | Carol absent from `availability`, 60 logged | in `People`, last; `HasPattern` false; `UtilisationPercent` null; `UnderTarget` false; no exception |
| `The_tiles_count_availability` (RPT-056) | Alice FullTime with Leave all day Tue; holiday Thu `"Day of Goodwill"` for everyone; Bob FullTime logs nothing | `AvailableMinutes == 2*(2400-480) - 480` (Bob loses the holiday, Alice the leave and the holiday); `UnavailableMinutes == 480`; `HolidayDays == 1`; `LoggedNothing == 1`; `UnloggedPeople` counts people with a gap |
| `The_task_tiles_count_a_shared_task_once` (RPT-046) | the existing RPT-046 setup, rebuilt with `availability` | unchanged task, unestimated and estimate figures |
| `By_person_puts_the_lowest_utilisation_first` (RPT-057) | utilisations 50% (Zed), 50% (Amy), 90%, no availability (Carol) | order Amy, Zed, the 90% person, Carol |
| `By_person_counts_tasks_and_the_total_counts_each_task_once` (RPT-047) | the existing RPT-047 shared-task setup | per-person `Tasks` and `Summary.Tasks` as before |
| `The_grid_shows_logged_against_available` (RPT-058) | Alice FullTime, Leave all day Tue, holiday Thu; logs Mon 360, Wed 480, Sat 60 in a window to Sun 11-29 | Mon `(360, 480)` `UnderTarget`; Tue `Unavailable`, available 0; Thu `AllHoliday`; Sat `Over`; Sun `Empty`; row total `(900, 1440)` |
| `The_pdf_grid_uses_weeks_past_31_days` (RPT-058) | window 2026-10-01 to 2026-11-14 (45 days) | `GridBucketSize == Day` with 45 cells; `PdfGridBucketSize == Week`; every minute in exactly one PDF cell |
| `Period_buckets_carry_available_unlogged_and_over` (RPT-058) | Alice and Bob from RPT-055 over the week | each day's bucket: `Minutes` = sum, `AvailableMinutes` = sum, `UnloggedMinutes` = per-person gaps summed, `OverMinutes` = per-person excess summed |

- [ ] **Step 2: Run to verify they fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~TimeUtilisationRulesTests"`. Expected: FAIL to compile.

- [ ] **Step 3: Implement the rules,** then the service: read facts and holidays for the people in scope, build each person's `ReportAvailability(patterns.Count > 0, AvailabilityRules.Days(...))`, pass `options.Value.EffectiveTarget` (inject `IOptions<ReportOptions>` into `ReportingService`). With the Person filter, every figure, the grid and the buckets read that person alone. Grid rows are by name; cells sum days within each bucket; a cell is `UnderTarget` when `UtilisationRules.UnderTarget` holds for its totals; `AllHoliday` when every day in it is a holiday. `Summary.HolidayDays` counts the distinct dates in the range that carry a `Holiday` in anyone's days (0 with nobody in the report).

- [ ] **Step 4: Run every test** — `dotnet test Orbit.slnx`. Expected: PASS.

- [ ] **Step 5: Commit** — `"Time utilisation: available, unavailable, utilisation and unlogged time, by person and by day"`.

---

### Task 12: The report page, charts and PDF

**Files:**
- Modify: `TimeUtilisationCharts.cs`, `ReportTimeUtilisation.razor`, `ReportPdfEndpoint.cs`, `ReportsIndex.razor` (subtitle), `Orbit.Web/wwwroot/css/app.css`, `TimeUtilisationChartsTests.cs`
- Docs: `orbit-spec.md` §12: the opening line's filters, the table row, and the **Time utilisation** section and acceptance list in place of *Time spent*'s; §6.10 *Time reports* line

**Interfaces:**
- Consumes: the Task 11 report.
- Produces: `TimeUtilisationCharts.Logged = new("Logged", "oc-time", "#4338ca")`, `Unlogged = new("Unlogged", "oc-unlogged", "#9aa0c3")`, `Over = new("Over", "oc-over", "#d97706")`; `All(r)` = `[Period(r), People(r), Projects(r), Types(r), Sources(r)]`; `PdfSections(r)` unchanged in shape.

- [ ] **Step 1: Write the failing chart tests** (rewrite RPT-051, add RPT-060):
  - RPT-051 `The_five_charts_in_order`: titles `["Logged against available per day", "Utilisation by person", "Time by project", "Time by task type", "Time by task source"]`; a 100-day report's first title `"Logged against available per week"`, a 400-day one's `"… per month"`; the period chart is `Wide` with a `NarrowSvg`; projects fold past twelve with *No project* last; each chart has its empty message with no data.
  - RPT-060 `The_period_and_person_charts_stack_logged_unlogged_and_over`: the period SVG contains the three legend labels; `People` lists the lowest utilisation first, and with 15 people keeps the 11 lowest plus `"4 others"` (fold with `SprintCharts.Fold(rows, r => -(r.UtilisationPercent ?? int.MaxValue))`, so people without availability go first into *others*); a bar's tip reads `"32h of 40h · 80%"` or `"1h · no availability"`.
  - RPT-060 `A_large_report_builds_its_pdf`: 40 people, 200 tasks, 400 days → `ReportPdfBuilder.Build(title, summary, ReportPdfEndpoint.TimeUtilisationSections(r).ToList(), landscape: true)` returns a non-empty byte array.
- [ ] **Step 2: Run to verify they fail** — `dotnet test Orbit.Tests --filter "FullyQualifiedName~TimeUtilisationChartsTests"`. Expected: FAIL.
- [ ] **Step 3: Implement the charts.** Period columns are `[Minutes − OverMinutes, UnloggedMinutes, OverMinutes]` with tip `"{logged} of {available} · {pct}%"`. Person bars are `[Minutes − OverMinutes, UnloggedMinutes, OverMinutes]`. Empty message for the first two: `"No availability or time in the period."`. Add `.orbit-chart .oc-unlogged` / `.oc-over` and dark-theme shades (`#5b6189`, `#f59e0b`) beside `oc-time` in `app.css`, and check their contrast against the card background in both themes.
- [ ] **Step 4: Build the page sections** per design §7: six tiles; *By person* (columns of §7.3, *Under 80%* chip from `TargetPercent`, bar with a target mark, Total row); **Timesheet by day** (sticky name column, scrolling box, `6/8` cells via `AvailabilityInputRules.FormatHours` without the `h`, highlight / *U* / greyed / `-` / *Over*, cell `title` with its parts, Total column `36/37 · 97%`); *By task* as before; the footer note per §7.6.
- [ ] **Step 5: Build the PDF sections** per §7.7 (summary row; *By person*; the grid from `PdfGrid` at `FontSize: 7f`; *By task*).
- [ ] **Step 6: Run every test** — `dotnet test Orbit.slnx`. Expected: PASS.
- [ ] **Step 7: Docs, then commit** — `"Time utilisation: tiles, charts, timesheet grid and PDF"`.

---

### Task 13: Documentation and the end-to-end check

**Files:**
- Modify: `orbit-spec.md` §3, §13 (decision **150**: design §1's decisions table and why), §14 (build step **134**); `README.md` (features, permission table, reports list, configuration); `CLAUDE.md` (add `Application/Availability/*` to the pure rules list)

- [ ] **Step 1: Write the docs.**
- [ ] **Step 2: End-to-end in Chrome against the dev database** (design §9 last paragraph): pattern change from a date shown in the grid from that day; half day and multi-day leave; a holiday greyed; week and month; a cell click; My Time's line; the report under Department and Person with a sprint picked; the grid; an old `Kind=TimeSpent` link; the PDF; both themes; no console errors. Note anything that couldn't be checked (a Department-scoped viewer's restricted tasks, if there's no such role in the dev data).
- [ ] **Step 3: Run every test one last time** — `dotnet test Orbit.slnx`. Expected: PASS.
- [ ] **Step 4: Commit** — `"Time utilisation and availability: spec, README and CLAUDE.md"`.
