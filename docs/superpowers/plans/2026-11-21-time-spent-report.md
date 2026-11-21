# Time spent report Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new *Time spent* report (tiles, five single-colour charts, a By person table and a By task table that opens on each task's people, with a PDF), then the removal of *Time by person*.

**Architecture:** The same shape as Sprint status. `ReportingService.TimeSpentAsync` reads the window's time once in SQL, summed by task, person and day. The pure `TimeSpentRules.Build` turns that into a `TimeSpentReport`. `TimeSpentCharts` draws it as SVG through `SvgCharts` for the page (`ReportTimeSpent.razor`) and the PDF (`ReportPdfEndpoint`). `ReportRun` and `ReportView` gain a Person filter and a sprint quick-pick that fills the date range.

**Tech Stack:** .NET 10, Blazor Server + MudBlazor, EF Core + PostgreSQL, QuestPDF, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-21-time-spent-report-design.md` (the design). The project's specification is `orbit-spec.md` (§12 Reporting, §6.10 Time tracking).

## Global Constraints

- Namespaces follow `RootNamespace` `Orbit`: `Orbit.Application`, `Orbit.Application.Models`, `Orbit.Application.Services`, `Orbit.Components.Pages.Reports`, never `Orbit.Web.*`.
- British spelling in prose, comments and identifiers (organisation, catalogue, colour).
- Keep each file's line endings (`git ls-files --eol <file>`). CRLF on disk: `ReportModels.cs`, `ReportingService.cs`, `ReportPdfEndpoint.cs`, `ReportText.cs`, `TimeReportRules.cs`, `TimeReportRulesTests.cs`, `app.css`, `orbit-spec.md`, `README.md`. LF: `ReportRun.cs`, `ReportView.razor`, `ReportsIndex.razor`, `SprintCharts.cs`, `SvgChartsTests.cs`. New files: LF.
- If an `Orbit.Web` process is running it locks `bin/Debug`: build and test with `-c Release` (`dotnet test Orbit.slnx -c Release`).
- Tests are xUnit over pure code and cite the acceptance ID in their doc comment (`/// <summary>RPT-044: ...</summary>`).
- No migration, no new permission (`reports.view` only), no MCP tool.
- Blazor components never inject a service: `ReportView` already calls `ReportRun.RunAsync` inside `Load(async s => ...)`.
- Thresholds: daily up to **62** days, weekly up to **366** days, monthly beyond (inclusive day counts). Charts fold past **12** bars (`SprintCharts.MaxBars`).
- Copy fixed by the spec: report title **Time spent**; description *"Where the time logged in the period went: by project, person, day, task type and source, then every task worked on, most time first, with who logged it."*; chart titles *Time per day* / *Time per week* / *Time per month*, *Time by project*, *Time by person*, *Time by task type*, *Time by task source*; empty chart text *No time logged in the period.*; empty task table *No time logged for the selected filters.*; address key `PersonId`.

## Review Focus

1. **An entry by a user with no name row** (deactivated and purged, or a bad id): the report names them *Unknown* and doesn't throw. Test in Task 1.
2. **An entry whose task isn't in the task facts** (deleted between the two reads): it's dropped from every figure, not counted under a phantom task. Test in Task 1.
3. **The Person filter on someone with no time in the range**: a covered person (their department is the one reported on) gets one zero row and *1 logged nothing*; someone from another department gets nothing. Test in Task 1.
4. **An ISO week across New Year** (Mon 2026-12-28 to Sun 2027-01-03): stays one bucket, labelled by its first day. Test in Task 2.
5. **Picking a sprint makes both date pickers echo `DateChanged`** with the new dates (§13 item 143): the page must not bounce back to the old range. `GoDate`'s guard covers it. Checked in Chrome in Task 7.

---

### Task 1: Time spent models and `TimeSpentRules.Build`

**Files:**
- Modify: `Orbit.Web/Application/Models/ReportModels.cs` (new records after `PersonTaskTimeRow`)
- Create: `Orbit.Web/Application/TimeSpentRules.cs`
- Test: `Orbit.Tests/Reports/TimeSpentRulesTests.cs`

**Interfaces:**
- Consumes: `TimeReportRules.Compare(IEnumerable<TaskTimeFacts>)`, `TaskTimeFacts`, `EstimateComparison`, `ReportPerson(Guid Id, string Name, bool IsActive)`.
- Produces (in `Orbit.Application.Models`):
  ```csharp
  public sealed record TimeSpentEntry(Guid TaskId, Guid UserId, DateOnly Date, int Minutes);
  public sealed record TimeSpentPersonTime(Guid TaskId, Guid UserId, int Minutes);           // a person's time on a task, to date
  public sealed record TimeSpentTaskFacts(Guid Id, string Number, string Title, TaskItemStatus Status, TaskType Type,
      TaskSource Source, Guid? ProjectId, IReadOnlyList<Guid> AssigneeIds, int? EstimateMinutes, int TotalMinutes)
  { public TaskTimeFacts AsTimeFacts() => new(Id, Number, Title, Status, AssigneeIds, EstimateMinutes, TotalMinutes); }
  public sealed record TimeSpentProjectFacts(Guid Id, string Number, string Name);
  public sealed record TimeSpentSummary(int Minutes, int People, int LoggedNothing, int Tasks, int UnestimatedTasks,
      EstimateComparison Estimate);
  public sealed record TimeSpentPersonRow(Guid UserId, string Name, int Minutes, int Tasks) { public bool LoggedNothing => Minutes == 0; }
  public sealed record TimeSpentTaskPerson(Guid UserId, string Name, int Minutes, int ToDateMinutes, bool IsAssignee);
  public sealed record TimeSpentTaskRow(Guid TaskId, string Number, string Title, TaskItemStatus Status, TaskType Type,
      TimeSpentProjectFacts? Project, int? EstimateMinutes, int Minutes, int TotalMinutes, IReadOnlyList<TimeSpentTaskPerson> People)
  {
      public bool IsCancelled => Status == TaskItemStatus.Cancelled;
      public bool IsOver => !IsCancelled && EstimateMinutes is int e && TotalMinutes > e;
  }
  public sealed record TimeSpentReport(DateOnly From, DateOnly To, TimeSpentSummary Summary,
      IReadOnlyList<TimeSpentPersonRow> People, IReadOnlyList<TimeSpentTaskRow> Tasks, IReadOnlyList<ReportPerson> PersonOptions);
  ```
- Produces (in `Orbit.Application`):
  ```csharp
  public static class TimeSpentRules
  {
      public static TimeSpentReport Build(DateOnly from, DateOnly to, IEnumerable<TimeSpentEntry> entries,
          IReadOnlyDictionary<Guid, TimeSpentTaskFacts> tasks, IEnumerable<TimeSpentPersonTime> toDate,
          IReadOnlyDictionary<Guid, TimeSpentProjectFacts> projects, IReadOnlyDictionary<Guid, ReportPerson> people,
          IEnumerable<Guid> covered, Guid? personId);
  }
  ```

Rules `Build` implements, in this order (design §2-§4, §7, §8):
1. Drop entries dated outside `from`..`to` (both included) and entries whose task isn't in `tasks`.
2. `PersonOptions`: the distinct people with entries left, as `people[id]` (or `new ReportPerson(id, "Unknown", true)`), by name (`StringComparer.CurrentCultureIgnoreCase`).
3. With `personId`: keep only that person's entries, and narrow `covered` to that person.
4. `Summary`: total minutes; distinct people with entries; `LoggedNothing` = distinct covered people with no entries; distinct tasks; tasks with no estimate; `TimeReportRules.Compare` over the distinct tasks' `AsTimeFacts()`.
5. `People`: per person with entries, their minutes and distinct tasks, by minutes descending then name; then a zero row for each covered person without entries, by name.
6. `Tasks`: per task, its minutes in the window and `TotalMinutes` from its facts. `Project` from `projects` (`null` when the task has no project; `new TimeSpentProjectFacts(id, "", "Unknown project")` when the id isn't found). Its `People`: per person with entries on it, their window minutes, their `toDate` minutes (falling back to their window minutes when `toDate` has no row), and whether they're among `AssigneeIds`, by minutes descending then name. Tasks by minutes descending, then `Number` (`StringComparer.Ordinal`).
7. A name is `people[id].Name`, or `"Unknown"` when the id isn't there.

- [ ] **Step 1: Write the failing tests** in `TimeSpentRulesTests` (class doc: `/// <summary>Time spent (spec §12).</summary>`). Use helpers like `TimeReportRulesTests`' (`Alice`, `Bob`, `Carol`, `Dan`, `Eve` Guids; a `People` dictionary of `ReportPerson`; a `Task(...)` factory numbering `T-26-00001` up; `Run(entries, tasks, covered, personId, toDate)` calling `Build` for `2026-10-01`..`2026-10-31` with an empty projects dictionary unless given).

  ```csharp
  /// <summary>RPT-044: entries on the first and last days count; a day outside either end doesn't.</summary>
  [Fact] public void Entries_on_the_first_and_last_days_count_and_those_outside_do_not()
  // entries on 2026-09-30 (60), 2026-10-01 (30), 2026-10-31 (45), 2026-11-01 (90), all Alice on one task
  // Assert.Equal(75, report.Summary.Minutes); Assert.Equal(75, Assert.Single(report.Tasks).Minutes);

  /// <summary>RPT-045: the Person options are worked out before the person filter, which keeps only that person's time; To date stays everyone's.</summary>
  [Fact] public void Person_options_come_before_the_person_filter_which_keeps_only_their_time()
  // t1 (total 300): Alice 120, Bob 60; t2: Bob 30. personId = Alice
  // Assert.Equal(["Alice", "Bob"], report.PersonOptions.Select(p => p.Name));
  // Assert.Equal(120, report.Summary.Minutes); var row = Assert.Single(report.Tasks); Assert.Equal(120, row.Minutes);
  // Assert.Equal(300, row.TotalMinutes); Assert.Equal("Alice", Assert.Single(row.People).Name);
  // personId = Carol (offered to nobody): Assert.Equal(0, report.Summary.Minutes); Assert.Empty(report.Tasks); same two options

  /// <summary>RPT-046: the summary counts people and tasks, unestimated tasks, and a shared task's estimate once.</summary>
  [Fact] public void Summary_counts_people_tasks_and_a_shared_task_once()
  // shared (estimate 240, total 300, assignees Alice+Bob): Alice 60, Bob 60; unestimated (total 90): Alice 30;
  // cancelled (estimate 60, total 500): Bob 15
  // Assert.Equal((2, 3, 1), (s.People, s.Tasks, s.UnestimatedTasks));
  // Assert.Equal((1, 240, 300, 1), (s.Estimate.EstimatedTasks, s.Estimate.EstimatedMinutes, s.Estimate.ActualMinutes, s.Estimate.OverTasks));

  /// <summary>RPT-047: By person by time then name, then the covered people who logged nothing, by name, at zero and never doubled.</summary>
  [Fact] public void By_person_orders_by_time_then_name_and_lists_who_logged_nothing_last()
  // Alice 60 (t1), Bob 120 (t1, t2), Dan 60 (t2); covered [Alice, Carol, Eve]
  // Assert.Equal(["Bob", "Alice", "Dan", "Carol", "Eve"], report.People.Select(p => p.Name));
  // Assert.Equal((2, 0), (bob.Tasks, carol.Minutes)); Assert.True(carol.LoggedNothing);
  // Assert.Equal((3, 2), (report.Summary.People, report.Summary.LoggedNothing)); Assert.Single(report.People, p => p.Name == "Alice");

  /// <summary>RPT-047: with the Person filter, only that person is considered: a covered person with no time is one zero row; someone from another department with none is nothing.</summary>
  [Fact] public void The_person_filter_narrows_who_logged_nothing_to_that_person()
  // same data; personId = Carol → Assert.Equal(["Carol"], names); Assert.Equal((0, 1), (s.People, s.LoggedNothing));
  // personId = Dan → Assert.Equal(["Dan"], names); Assert.Equal((1, 0), (s.People, s.LoggedNothing));
  // personId = Eve, with Eve removed from covered → Assert.Empty(report.People); Assert.Equal(0, s.LoggedNothing);

  /// <summary>RPT-048: By task by time then number; each task's people by time then name, with their time to date and the assignee flag.</summary>
  [Fact] public void By_task_orders_by_time_then_number_with_each_tasks_people()
  // t1 "T-26-00001": Bob 120; t2 "T-26-00002" (assignee Alice): Alice 90, Bob 30; t3 "T-26-00003": Alice 30
  // toDate: (t2, Alice, 200), (t2, Bob, 30)
  // Assert.Equal(["T-26-00001", "T-26-00002", "T-26-00003"], report.Tasks.Select(t => t.Number));
  // Assert.Equal([("Alice", 90, 200, true), ("Bob", 30, 30, false)], t2.People.Select(p => (p.Name, p.Minutes, p.ToDateMinutes, p.IsAssignee)));

  /// <summary>Review focus: an entry by a user with no name row is Unknown; an entry for a task not in the facts is dropped.</summary>
  [Fact] public void An_unknown_person_is_named_unknown_and_an_unknown_tasks_time_is_dropped()
  // entry by Guid.NewGuid() (not in People) on t1: 30; entry by Alice on a task id not in tasks: 999
  // Assert.Equal(30, report.Summary.Minutes); Assert.Equal("Unknown", Assert.Single(report.People).Name);
  // Assert.Equal("Unknown", Assert.Single(report.PersonOptions).Name);
  ```

- [ ] **Step 2: Run them to see them fail**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TimeSpentRulesTests"`
  Expected: build error, `TimeSpentRules` / `TimeSpentEntry` don't exist.

- [ ] **Step 3: Add the models to `ReportModels.cs` and implement `TimeSpentRules.Build`** as the Interfaces block and the numbered rules above describe. Give the class a doc comment in the style of `TimeReportRules` (what it does, the window, To date being everyone's, a shared task counted once in totals).

- [ ] **Step 4: Run the tests to see them pass**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TimeSpentRulesTests"`
  Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

  ```bash
  git add Orbit.Web/Application/Models/ReportModels.cs Orbit.Web/Application/TimeSpentRules.cs Orbit.Tests/Reports/TimeSpentRulesTests.cs
  git commit -m "Time spent: the report's rules - people, tasks and summary"
  ```

### Task 2: Breakdowns by project, type and source, and the time buckets

**Files:**
- Modify: `Orbit.Web/Application/Models/ReportModels.cs`
- Modify: `Orbit.Web/Application/TimeSpentRules.cs`
- Test: `Orbit.Tests/Reports/TimeSpentRulesTests.cs`

**Interfaces:**
- Consumes: Task 1's `Build` and models.
- Produces:
  ```csharp
  public enum TimeBucketSize { Day, Week, Month }
  public sealed record TimeBucket(DateOnly From, DateOnly To, int Minutes = 0);
  public sealed record TimeSpentProjectRow(TimeSpentProjectFacts? Project, int Minutes);   // Project null: No project
  public sealed record TimeSpentTypeRow(TaskType Type, int Minutes);
  public sealed record TimeSpentSourceRow(TaskSource Source, int Minutes);
  // TimeSpentReport gains init properties, as SprintStatusReport has Sprints and PersonTasks:
  //   IReadOnlyList<TimeSpentProjectRow> Projects, IReadOnlyList<TimeSpentTypeRow> Types, IReadOnlyList<TimeSpentSourceRow> Sources,
  //   TimeBucketSize BucketSize, IReadOnlyList<TimeBucket> Buckets   (lists default to [])
  // TimeSpentRules gains:
  public const int MaxDailyDays = 62, MaxWeeklyDays = 366;
  public static (TimeBucketSize Size, IReadOnlyList<TimeBucket> Buckets) Buckets(DateOnly from, DateOnly to); // Minutes all 0
  ```

Rules (design §6, §6.1), all over the entries left after the person filter:
- `Projects`: one row per project with time, by minutes descending, then project name; the No project row (`Project` null) **last** whatever its time; only when it has time.
- `Types` / `Sources`: one row per value with time, by minutes descending, then the enum's declared order.
- `Buckets(from, to)`: length = `to.DayNumber - from.DayNumber + 1`. Up to `MaxDailyDays`: a bucket per day. Up to `MaxWeeklyDays`: a bucket per ISO week (Monday to Sunday), the first and last clipped to `from` and `to`. Beyond: a bucket per calendar month, clipped likewise. `Build` sets `BucketSize` and fills each bucket's minutes from the entries dated in it.

- [ ] **Step 1: Write the failing tests**

  ```csharp
  /// <summary>RPT-049: projects, types and sources with time, most first (ties in declared order), No project last; each adds up to the total.</summary>
  [Fact] public void Projects_types_and_sources_are_ordered_by_time_and_add_up()
  // a: P1, Meeting, Manual, 60   b: P2, Task, Nagios, 120   c: no project, Request, Nagios, 60   d: no project, Task, Manual, 120
  // Projects → [(P2, 120), (P1, 60), (null, 180)]  - No project last though it has the most
  // Types → [(Task, 240), (Meeting, 60), (Request, 60)]  - Meeting and Request tied, Meeting declared first
  // Sources → [(Manual, 180), (Nagios, 180)]  - tied, Manual declared first
  // Assert.Equal(360, report.Projects.Sum(p => p.Minutes)); the same for Types and Sources
  // Assert.DoesNotContain(report.Types, t => t.Type == TaskType.Audit);

  /// <summary>RPT-050: 62 days are daily, 63 and 366 weekly, 367 monthly.</summary>
  [Theory]
  [InlineData("2026-01-01", "2026-03-03", TimeBucketSize.Day, 62)]
  [InlineData("2026-01-01", "2026-03-04", TimeBucketSize.Week, 10)]
  [InlineData("2026-01-01", "2027-01-01", TimeBucketSize.Week, 53)]
  [InlineData("2026-01-01", "2027-01-02", TimeBucketSize.Month, 13)]
  public void Buckets_switch_from_days_to_weeks_to_months_by_range_length(string from, string to, TimeBucketSize size, int count)

  /// <summary>RPT-050: weeks run Monday to Sunday, clipped at both ends, and one across New Year stays whole.</summary>
  [Fact] public void Weeks_run_monday_to_sunday_clipped_to_the_range()
  // Buckets(2026-10-14 (Wed), 2026-12-31 (Thu)): first (2026-10-14, 2026-10-18), second (2026-10-19, 2026-10-25), last (2026-12-28, 2026-12-31)
  // Buckets(2026-12-02, 2027-02-28): contains (2026-12-28, 2027-01-03)

  /// <summary>RPT-050: months are clipped at both ends, and every minute falls in exactly one bucket.</summary>
  [Fact] public void Every_minute_falls_in_exactly_one_bucket()
  // Build over 2025-01-15..2026-03-10 with entries on 2025-01-15, 2025-06-30, 2025-07-01, 2026-03-10
  // Assert.Equal(TimeBucketSize.Month, report.BucketSize); Assert.Equal(15, report.Buckets.Count);
  // Assert.Equal((new DateOnly(2025, 1, 15), new DateOnly(2025, 1, 31)), (first.From, first.To)); last (2026-03-01, 2026-03-10)
  // Assert.Equal(report.Summary.Minutes, report.Buckets.Sum(b => b.Minutes));
  ```

  Check the 10 in the second row before relying on it: 2026-01-01 is a Thursday, so 2026-01-01..2026-03-04 holds the clipped week 01-01..01-04 and Mondays 01-05 to 03-02, which is 1 + 9 = 10 buckets. Likewise 53 for 2026-01-01..2027-01-01 (clipped first week, 51 whole weeks from 01-05 to 12-27, then 12-28..2027-01-01) and 13 months for 2026-01-01..2027-01-02.

- [ ] **Step 2: Run them to see them fail**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TimeSpentRulesTests"`
  Expected: build error, `TimeBucketSize` doesn't exist.

- [ ] **Step 3: Add the models and implement `Buckets` and the breakdowns in `Build`.** Find a week's Monday with `date.AddDays(-(((int)date.DayOfWeek + 6) % 7))`.

- [ ] **Step 4: Run the tests to see them pass**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TimeSpentRulesTests"`
  Expected: PASS, 7 earlier + 7 new (the theory counts four).

- [ ] **Step 5: Commit**

  ```bash
  git add Orbit.Web/Application/Models/ReportModels.cs Orbit.Web/Application/TimeSpentRules.cs Orbit.Tests/Reports/TimeSpentRulesTests.cs
  git commit -m "Time spent: time by project, type and source, and by day, week or month"
  ```

### Task 3: The five charts

**Files:**
- Create: `Orbit.Web/Components/Pages/Reports/TimeSpentCharts.cs`
- Modify: `Orbit.Web/Components/Pages/Reports/SprintCharts.cs` (a `PdfSections` overload over a chart list)
- Modify: `Orbit.Web/Components/Pages/Reports/ReportText.cs` (`ShareText`)
- Modify: `Orbit.Web/wwwroot/css/app.css` (`oc-time`)
- Test: `Orbit.Tests/Reports/TimeSpentChartsTests.cs`

**Interfaces:**
- Consumes: Task 2's `TimeSpentReport` with its breakdowns; `SvgCharts.HorizontalBars` / `Columns`; `SprintCharts.Chart(string Title, string Svg, bool Wide = false, string? NarrowSvg = null)`, `SprintCharts.Fold`, `SprintCharts.MaxBars`, `WideWidth` (1280), `HalfWidth` (640); `Ui.SourceLabel(TaskSource)`.
- Produces:
  ```csharp
  // ReportText
  public static string ShareText(int part, int whole);   // "24%"; "<1%" above nothing that rounds to 0; "0%" for nothing; "-" when whole is 0
  // SprintCharts: the existing PdfSections(SprintStatusReport) calls this
  public static IEnumerable<ReportPdfBuilder.Charts> PdfSections(IReadOnlyList<Chart> charts);
  // TimeSpentCharts
  public static class TimeSpentCharts
  {
      public static readonly SvgCharts.Series Time = new("Time", "oc-time", "#4338ca");
      public const string Empty = "No time logged in the period.";
      public static IReadOnlyList<SprintCharts.Chart> All(TimeSpentReport r);   // Period, Projects, People, Types, Sources
      public static IEnumerable<ReportPdfBuilder.Charts> PdfSections(TimeSpentReport r);
      public static SprintCharts.Chart Period(TimeSpentReport r);    // wide 1280 with a 640 NarrowSvg
      public static SprintCharts.Chart Projects(TimeSpentReport r);
      public static SprintCharts.Chart People(TimeSpentReport r);
      public static SprintCharts.Chart Types(TimeSpentReport r);
      public static SprintCharts.Chart Sources(TimeSpentReport r);
  }
  ```

What the charts draw (design §6), one series (`Time`), so no legend:
- Every bar's tip: `$"{TimeFormat.Minutes(m)} · {ReportText.ShareText(m, r.Summary.Minutes)}"`.
- `Period`: `SvgCharts.Columns` with tick unit 60, tick text `(v / 60).ToString("0.#", CultureInfo.InvariantCulture) + "h"`, `minTickStep: 0.5`, as `SprintCharts.Days`. Title by `BucketSize`: *Time per day* / *Time per week* / *Time per month*. Labels: a day `"ddd\nd"` (title `"ddd yyyy-MM-dd"`, weekends `Muted`), as `SprintCharts.DayItem`; a week `"d\nMMM"` of its `From`; a month `"MMM\nyyyy"`. A week's or month's title is `"yyyy-MM-dd to yyyy-MM-dd"` (its clipped dates). All invariant culture.
- `Projects`: the rows with a project, folded with `SprintCharts.Fold(rows, p => p.Minutes)`. A bar's label is the project name and its title `$"{Number} {Name}".Trim()`. Folded rows share a muted bar `$"{rest.Count} other projects"`, and the No project row is a muted bar `"No project"`, last.
- `People`: the rows with minutes above 0, folded likewise, the rest as a muted `$"{rest.Count} others"`.
- `Types`: `Type.ToString()` labels. `Sources`: `Ui.SourceLabel` labels. Neither folds.
- `app.css`: after `.orbit-chart .oc-other-sprint`, add `.orbit-chart .oc-time { fill: var(--orbit-chart-planned); }` (indigo, with its dark shade already checked for contrast) and a comment that Time spent's one series takes the planned indigo.

- [ ] **Step 1: Write the failing tests** in `TimeSpentChartsTests` (build reports through `TimeSpentRules.Build`, with an XML-parse helper as in `SvgChartsTests`).

  ```csharp
  /// <summary>RPT-051: five charts in order, the period chart across the top at 1280 with a 640 copy, the rest in pairs in the PDF.</summary>
  [Fact] public void Five_charts_in_order_with_the_period_chart_across_the_top()
  // Assert.Equal(["Time per day", "Time by project", "Time by person", "Time by task type", "Time by task source"], charts.Select(c => c.Title));
  // Assert.Equal([true, false, false, false, false], charts.Select(c => c.Wide)); widths ("1280", "640"); Assert.All(charts, c => XDocument.Parse(c.Svg));
  // Assert.Equal([1, 2], TimeSpentCharts.PdfSections(r).Select(s => s.PerRow)); Assert.Equal([1, 4], ...Select(s => s.Items.Count));

  /// <summary>RPT-051: the period chart's title follows the bucket.</summary>
  [Theory] [InlineData(100, "Time per week")] [InlineData(400, "Time per month")]
  public void The_period_chart_title_follows_the_bucket(int days, string title)

  /// <summary>RPT-051: past twelve, the projects and people charts keep the eleven with the most time; No project last; tips read time and share.</summary>
  [Fact] public void Projects_and_people_fold_past_twelve_with_no_project_last()
  // 14 projects + no-project time, 15 people: Assert.Contains("3 other projects", texts); last bar label "No project";
  // Assert.Contains("4 others", peopleTexts); a 120-minute task of a 240-minute total: Assert.Contains("2h · 50%", texts)

  /// <summary>RPT-051: with no time logged, every chart says so.</summary>
  [Fact] public void Every_chart_says_so_when_nothing_was_logged()
  // Assert.All(TimeSpentCharts.All(empty), c => Assert.Contains(TimeSpentCharts.Empty, c.Svg));

  /// <summary>RPT-051: a share reads as a whole percentage, "<1%" above nothing that rounds to 0, "-" of nothing.</summary>
  [Theory]
  [InlineData(24, 100, "24%")] [InlineData(1, 1000, "<1%")] [InlineData(0, 100, "0%")] [InlineData(5, 0, "-")]
  public void A_share_reads_as_a_whole_percentage(int part, int whole, string text)
  ```

- [ ] **Step 2: Run them to see them fail**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TimeSpentChartsTests"`
  Expected: build error, `TimeSpentCharts` doesn't exist.

- [ ] **Step 3: Implement `ReportText.ShareText`, the `SprintCharts.PdfSections` overload (moving the existing loop into it), `TimeSpentCharts`, and the `app.css` rule.**

- [ ] **Step 4: Run all the report tests to see them pass, the Sprint status ones included**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~Orbit.Tests.Reports"`
  Expected: PASS, with no change in `SvgChartsTests`.

- [ ] **Step 5: Commit**

  ```bash
  git add Orbit.Web/Components/Pages/Reports/TimeSpentCharts.cs Orbit.Web/Components/Pages/Reports/SprintCharts.cs Orbit.Web/Components/Pages/Reports/ReportText.cs Orbit.Web/wwwroot/css/app.css Orbit.Tests/Reports/TimeSpentChartsTests.cs
  git commit -m "Time spent: the five charts"
  ```

### Task 4: The service, the catalogue entry and the report run

**Files:**
- Modify: `Orbit.Web/Application/Models/ReportModels.cs` (`ReportKind`, `ReportFilter`, `ReportDefinition`, `ReportCatalog`)
- Modify: `Orbit.Web/Application/Services/ReportingService.cs`
- Modify: `Orbit.Web/Components/Pages/Reports/ReportRun.cs`
- Modify: `Orbit.Web/Components/Pages/Reports/ReportPdfEndpoint.cs` (the `Parse` call only)
- Modify: `Orbit.Web/Components/Pages/Reports/ReportView.razor` (the `Parse` call and the `PersonId` parameter only)
- Test: `Orbit.Tests/Tasks/FilterQueryTests.cs`

**Interfaces:**
- Consumes: Task 1's `TimeSpentRules.Build` and its input records; `SprintService.ListStartedAsync()`; `TimeRules.SprintForRange(IEnumerable<Sprint>, DateOnly, DateOnly)`.
- Produces:
  ```csharp
  // ReportModels
  public enum ReportKind { ..., SprintStatus, TimeSpent }                       // appended
  public sealed record ReportFilter(DateTime FromUtc, DateTime ToUtc, Guid? ProjectId, Guid? DepartmentId,
      Guid? OwnerId = null, Guid? AssigneeId = null, Guid? PersonId = null);   // PersonId: Time spent only
  public sealed record ReportDefinition(ReportKind Kind, string Title, string Description, bool ByProject = true,
      bool ByOwnerAndAssignee = false, bool BySprint = false, bool ByPerson = false, bool SprintQuickPick = false);
  // ReportCatalog: new(ReportKind.TimeSpent, "Time spent", "<the description in Global Constraints>", ByPerson: true, SprintQuickPick: true),
  //   placed straight after the Time by person entry
  // ReportingService
  public async Task<TimeSpentReport> TimeSpentAsync(ReportFilter f, CancellationToken ct = default);
  // ReportRun
  public const string PersonKey = "PersonId";                                   // added to Keys after AssigneeKey
  public Guid? PersonId { get; private set; }
  public TimeSpentReport? TimeSpent { get; private set; }
  public IReadOnlyList<Sprint> Sprints { get; private set; } = [];              // the quick-pick's sprints, for SprintForRange
  public IReadOnlyList<(string Value, string Text)> PersonFilterOptions { get; private set; } = [];  // not PersonOptions: a private helper has that name
  public string RangeQuery(DateOnly from, DateOnly to);                         // Query() with both FromKey and ToKey set
  // ReportAddress gains Guid? PersonId = null; Parse gains string? personId = null (after sprintId)
  ```

`TimeSpentAsync`, as `TimeByPersonAsync` and `SprintStatusAsync` read (design §10.1):
- `RequireReportsAsync`. Then `from = DateOnly.FromDateTime(f.FromUtc)` and `toExclusive = DateOnly.FromDateTime(f.ToUtc)`, as `TimeByPersonAsync` has them. Pass `toExclusive.AddDays(-1)` to `Build` as `to`.
- Entries: `Apply(db.TimeEntries.AsNoTracking(), f, actor)` dated `>= from && < toExclusive`, grouped by `TaskId, UserId, Date` into `TimeSpentEntry`. **No person filter in SQL.**
- For their task ids: `TimeSpentTaskFacts` (assignee ids from `Assignments`, `TotalMinutes` as `Facts` sums them); `TimeSpentPersonTime` grouped by `TaskId, UserId` over all dates; `TimeSpentProjectFacts` for their project ids. Skip all three queries when there are no entries.
- `covered = await PeopleAsync(f, actor, ct)`.
- `people`: `ReportPerson` for the entries' users and `covered`, named `u.IsSystemAccount ? ClaudeName : u.DisplayName`, active `u.IsActive || u.IsSystemAccount`, as `ProjectReportPeopleAsync` builds them.

`ReportRun.RunAsync` for `ReportKind.TimeSpent`:
- Pass `PersonId` into the `ReportFilter` (set `run.PersonId = null` when `!Definition.ByPerson`).
- When `Definition.SprintQuickPick`: `run.Sprints = await services.Get<SprintService>().ListStartedAsync(ct)`, fetched with or without options, since the summary line needs it. `SprintOptions` with options: `(id, $"{Name} ({Status})")`, as My Time labels them.
- The summary line: the period, then `TimeRules.SprintForRange(run.Sprints, from, to)?.Name` when there is one, then department and project as now, then `"Person: " + name` from the report's `PersonOptions` (`PersonName` falls back to *Unknown*).
- `run.PersonFilterOptions = PersonOptions(report.PersonOptions, run.PersonId)`, through the existing private helper.
- `Query()` writes `PersonKey` from `PersonId`.

- [ ] **Step 1: Write the failing test** in `FilterQueryTests`, beside `A_report_period_defaults_to_the_last_31_days`:

  ```csharp
  [Fact]
  public void A_report_address_reads_its_person()
  {
      var id = Guid.NewGuid();
      Assert.Equal(id, Orbit.Components.Pages.Reports.ReportAddress.Parse("TimeSpent", null, null, null, null, null, null, null, id.ToString()).PersonId);
      Assert.Null(Orbit.Components.Pages.Reports.ReportAddress.Parse("TimeSpent", null, null, null, null, null, null, null, "nobody").PersonId);
      Assert.Equal(ReportKind.TimeSpent, Orbit.Components.Pages.Reports.ReportAddress.Parse("TimeSpent", null, null, null, null, null, null).Kind);
  }
  ```

- [ ] **Step 2: Run it to see it fail**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~FilterQueryTests"`
  Expected: build error, `PersonId` / `ReportKind.TimeSpent` don't exist.

- [ ] **Step 3: Implement the model changes, `TimeSpentAsync`, the `ReportRun` / `ReportAddress` changes, and pass `q[ReportRun.PersonKey]` / a new `[SupplyParameterFromQuery(Name = ReportRun.PersonKey)] public string? PersonId` to `Parse` in `ReportPdfEndpoint` and `ReportView`.**

- [ ] **Step 4: Build and run the whole suite**

  Run: `dotnet build Orbit.slnx -c Release` then `dotnet test Orbit.slnx -c Release`
  Expected: build succeeds with no new warnings; all tests PASS.

- [ ] **Step 5: Commit**

  ```bash
  git add Orbit.Web/Application/Models/ReportModels.cs Orbit.Web/Application/Services/ReportingService.cs Orbit.Web/Components/Pages/Reports/ReportRun.cs Orbit.Web/Components/Pages/Reports/ReportPdfEndpoint.cs Orbit.Web/Components/Pages/Reports/ReportView.razor Orbit.Tests/Tasks/FilterQueryTests.cs
  git commit -m "Time spent: the service, the catalogue entry and the report run"
  ```

### Task 5: The report page

**Files:**
- Create: `Orbit.Web/Components/Pages/Reports/ReportTimeSpent.razor`
- Modify: `Orbit.Web/Components/Pages/Reports/ReportView.razor`
- Modify: `Orbit.Web/Components/Pages/Reports/ReportsIndex.razor` (subtitle)

**Interfaces:**
- Consumes: Task 4's `ReportRun.TimeSpent`, `Sprints`, `SprintOptions`, `PersonFilterOptions`, `RangeQuery`, `PersonKey`; Task 3's `TimeSpentCharts.All` and `ReportText.ShareText`; `ReportText.Share` (bar width), `Estimate`, `Estimated`, `Actual`, `Variance`, `VarianceClass`, `Marker`; `TaskLook.Status`, `TaskLook.Type`, `TaskLook.TypeClass`.
- Produces: `ReportTimeSpent` with `[Parameter, EditorRequired] public TimeSpentReport Report`.

`ReportView.razor`:
- In `OnParametersSetAsync`, beside `_assignees`: `_people = WithAll(run.PersonFilterOptions)`. The quick-pick reads `run.SprintOptions` as it is, with no "All", as My Time's does.
- After the two date pickers, when `run.Definition.SprintQuickPick`: a `FilterSelect` labelled *Sprint* with `Value="@(TimeRules.SprintForRange(run.Sprints, _from, _to)?.Id.ToString() ?? "")"`, `Options="run.SprintOptions"`, `Title="Fill From and To with a sprint's dates"`, as `MyTime.razor` has it. On change, find the sprint in `run.Sprints` (as `MyTime.PickSprint` does) and `Nav.NavigateTo($"/Reports/View?{run.RangeQuery(s.StartDate, s.EndDate)}")`. Do nothing for a value that isn't a sprint, or a sprint whose dates are the range shown (`_from`, `_to`).
- After the project filter, when `run.Definition.ByPerson`: a `FilterSelect` labelled *Person*, `ValueChanged="v => Go(ReportRun.PersonKey, v)"`, `Options="_people"`, `Title="Only the time this person logged"`.
- In the result: `else if (run.TimeSpent is { } spent) { <ReportTimeSpent Report="spent" /> }` with **no `@key`**, so open rows stay open across runs (comment it as Sprint status's is).

`ReportTimeSpent.razor`: copy Sprint status's markup idioms (`orbit-report-summary`, `orbit-stats`, `orbit-chart-grid` with the wide/narrow pair, `orbit-table-wrap`, `MudSimpleTable Class="orbit-tasks orbit-report"`, `orbit-report-detail` rows, `orbit-card-footer`). Contents, with the spec's wording:
- **Tiles** (design §5): Logged (*on N tasks*), People (*N logged nothing* when above 0), Tasks (*N unestimated* when above 0), Estimate variance (value `TimeFormat.Signed`, or `-` with nothing estimated, coloured by `VarianceClass`; beneath, `"{Estimated} estimated · {Actual} logged"` plus `" · {OverTasks} over estimate"` when above 0).
- **Charts** from `TimeSpentCharts.All(Report)`, computed in `OnParametersSet`.
- **By person** (design §7): heading `By person`. Zero rows greyed (`orbit-muted` on each cell, as `ReportTimeByPerson` does). A Total row with `Summary.Minutes` and `Summary.Tasks`.
- **By task** (design §8): heading `By task`. The open set is a `HashSet<Guid>` of task ids. The People count is a `MudButton` with expand icons and `aria-expanded`, as the old report's task-count button. A Total row: `Summary.Minutes`, `Summary.Tasks`, and `Estimated` / `Actual` / `Variance` over `Summary.Estimate`. With no tasks, *No time logged for the selected filters.*
- **Footer** (design §8's list): the window and the entry date; department and project taken from the task; To date being everyone's time ever; the estimate comparison over estimated, non-cancelled tasks; a shared task counted once in totals; the people who logged nothing and whose they are; the Person filter; days, weeks or months by the range's length; the charts' twelve-bar folding.

`ReportsIndex.razor`: append to the subtitle *"Time spent's dates can be filled from a sprint."*

- [ ] **Step 1: Build**

  Run: `dotnet build Orbit.slnx -c Release`
  Expected: build succeeds with no new warnings.

- [ ] **Step 2: Load the page.** Start the app (`dotnet run --project Orbit.Web`, or ask the user if it's already running from the IDE) and open `/Reports`, then the *Time spent* card.
  Expected: the page renders with tiles, five charts and both tables, and there are no errors in the browser console.

- [ ] **Step 3: Commit**

  ```bash
  git add Orbit.Web/Components/Pages/Reports/ReportTimeSpent.razor Orbit.Web/Components/Pages/Reports/ReportView.razor Orbit.Web/Components/Pages/Reports/ReportsIndex.razor
  git commit -m "Time spent: the report page, its sprint quick-pick and Person filter"
  ```

### Task 6: The PDF

**Files:**
- Modify: `Orbit.Web/Components/Pages/Reports/ReportPdfEndpoint.cs`
- Test: `Orbit.Tests/Reports/TimeSpentChartsTests.cs`

**Interfaces:**
- Consumes: Task 3's `TimeSpentCharts.PdfSections`, `ReportText.ShareText`; Task 4's `ReportRun.TimeSpent`.
- Produces: `public static IEnumerable<ReportPdfBuilder.Section> TimeSpentSections(TimeSpentReport r)` on `ReportPdfEndpoint`, which is public so the test can build a PDF.

Sections, in order (design §9):
1. `TimeSpentCharts.PdfSections(r)`.
2. Summary, one row: headers `["Logged", "People", "Tasks", "Estimated", "Logged to date", "Variance"]`. People is `"{People}"` plus `"\n{LoggedNothing} logged nothing"` when above 0. Tasks uses the existing `Tasks(count, unestimated)` helper. Variance is `Variance(e)` plus `"\n{OverTasks} over estimate"` when above 0. Caption `"Summary"`.
3. By person: `["Person", "Logged", "Share", "Tasks"]`, zero rows reading `0m` and `0`, and a `Total` row (Summary minutes, blank share, Summary tasks). Caption `"By person"`.
4. By task: `["Task", "Type", "Status", "Logged", "Share", "Estimate", "To date", "", "People"]`. Task is `$"{Number} {Title}\n{project}"`, where project is `$"{Number} {Name}".Trim()` or `"No project"`. The note is `Marker(Status, EstimateMinutes, IsOver)`. People is one line per person: `$"{Name} {TimeFormat.Minutes(m)} ({ShareText(m, task.Minutes)})"` plus `" · assignee"`. A `Total` row. Caption `"By task"`. Widths `[4.2f, 1.2f, 1.4f, 1.1f, 0.9f, 1.1f, 1.1f, 1.4f, 3.6f]`.
- `Tables(run)`: add `if (run.TimeSpent is not null) tables.AddRange(TimeSpentSections(run.TimeSpent));`. `landscape:` adds `|| run.TimeSpent is not null`.

- [ ] **Step 1: Write the failing test**

  ```csharp
  /// <summary>RPT-051: a large report's PDF builds: 40 people, 200 tasks, 30 projects over 400 days (monthly), its charts folded to fit a page.</summary>
  [Fact]
  public void A_large_reports_pdf_builds()
  {
      // build the report through TimeSpentRules.Build with generated entries spread over 2025-01-01..2026-02-04
      QuestPDF.Settings.License = LicenseType.Community;
      var pdf = ReportPdfBuilder.Build("Time spent", "2025-01-01 to 2026-02-04", [.. ReportPdfEndpoint.TimeSpentSections(report)], landscape: true);
      Assert.NotEmpty(pdf);
  }
  ```

- [ ] **Step 2: Run it to see it fail**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TimeSpentChartsTests.A_large_reports_pdf_builds"`
  Expected: build error, `TimeSpentSections` doesn't exist.

- [ ] **Step 3: Implement `TimeSpentSections` and wire it into `Tables` and `landscape`.**

- [ ] **Step 4: Run the test to see it pass**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~TimeSpentChartsTests"`
  Expected: PASS.

- [ ] **Step 5: Commit**

  ```bash
  git add Orbit.Web/Components/Pages/Reports/ReportPdfEndpoint.cs Orbit.Tests/Reports/TimeSpentChartsTests.cs
  git commit -m "Time spent: the PDF"
  ```

### Task 7: Check it in the app, and document it

**Files:**
- Modify: `orbit-spec.md`
- Modify: `README.md`

- [ ] **Step 1: Check in Chrome against the dev database, read only,** as a System Administrator. Each item, with what it should show:
  - Pick a sprint from the quick-pick: the address carries only `From` and `To` (the sprint's dates) and **stays put**, with no bounce from the pickers' echo (Review Focus 5). The summary line names the sprint. A reload names it again. Changing *From* by hand blanks it.
  - The default 31 days: daily columns with weekends greyed. A 100-day range: *Time per week*.
  - Department, Project and Person filters. The Person options are the people with time. With a person picked, the tiles, charts and rows are theirs, and *To date* doesn't change.
  - Open a task row, then change the Person or the dates: the row stays open while the task is still listed.
  - People who logged nothing: counted on the tile and greyed at the end of *By person*.
  - *Download PDF*: landscape, charts, summary, By person, By task with the People cell.
  - Both themes: the indigo bars readable on each.
  - No console errors.
  Note anything not seen (e.g. a monthly range if the dev data is too short).

- [ ] **Step 2: Update `orbit-spec.md`** (CRLF):
  - §12 opening line: the date-range reports' filters now include *Person* on Time spent, and a sprint quick-pick.
  - §12 table: a **Time spent** row after *Time by person*.
  - §12: a **Time spent** subsection after *Time reports*, stating design §2-§9 as rules, with an **Acceptance** paragraph listing RPT-044 to RPT-051 as in design §12, and an end-to-end paragraph saying what Step 1 saw and didn't.
  - §6.10 *Time reports* line: mention Time spent.
  - §13: decision **149**, recording the choices in design §1's table and why (quote the request), how it was built, and what was verified.
  - §14: step **133**: `TimeSpentRules` (RPT-044 to RPT-050) → `TimeSpentCharts` (RPT-051) → `ReportingService.TimeSpentAsync` → `ReportRun` / `ReportView` (Person, sprint quick-pick) → `ReportTimeSpent.razor` → the PDF → §12, §6.10 and the README. No migration.

- [ ] **Step 3: Update `README.md`** (CRLF): add **Time spent** to the report list beside *Time by person*'s line, with the description.

- [ ] **Step 4: Commit**

  ```bash
  git add orbit-spec.md README.md
  git commit -m "Time spent: spec and README"
  ```

### Task 8: Retire Time by person

**Files:**
- Delete: `Orbit.Web/Components/Pages/Reports/ReportTimeByPerson.razor`
- Modify: `Orbit.Web/Application/Models/ReportModels.cs`, `Orbit.Web/Application/TimeReportRules.cs`, `Orbit.Web/Application/Services/ReportingService.cs`, `Orbit.Web/Components/Pages/Reports/ReportRun.cs`, `ReportView.razor`, `ReportPdfEndpoint.cs`
- Modify: `Orbit.Tests/Reports/TimeReportRulesTests.cs`, `Orbit.Tests/Tasks/FilterQueryTests.cs`
- Modify: `orbit-spec.md`, `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: `ReportAddress.Parse` maps a kind of `"TimeByPerson"` (any case, trimmed) to `ReportKind.TimeSpent`. `ReportKind.TimeByPerson` no longer exists.

- [ ] **Step 1: Write the failing test** in `FilterQueryTests`:

  ```csharp
  /// <summary>RPT-052: an old Time by person link opens Time spent, its other filters kept.</summary>
  [Fact]
  public void An_old_time_by_person_link_opens_time_spent()
  {
      var dept = Guid.NewGuid();
      var a = Orbit.Components.Pages.Reports.ReportAddress.Parse("TimeByPerson", "2026-10-01", "2026-10-31", null, dept.ToString(), null, null);
      Assert.Equal((ReportKind.TimeSpent, new DateOnly(2026, 10, 1), dept), (a.Kind, a.From!.Value, a.DepartmentId!.Value));
  }
  ```

- [ ] **Step 2: Run it to see it fail**

  Run: `dotnet test Orbit.Tests -c Release --filter "FullyQualifiedName~FilterQueryTests.An_old_time_by_person_link_opens_time_spent"`
  Expected: FAIL, the kind is `TimeByPerson`.

- [ ] **Step 3: Remove Time by person:**
  - Delete `ReportKind.TimeByPerson`, its catalogue entry, `TimeByPersonReport`, `PersonTimeRow`, `PersonTaskTimeRow`, `LoggedTime`, `TimeReportRules.TimeByPerson`, `ReportingService.TimeByPersonAsync`, `ReportRun.TimeByPerson` and its `case`, `ReportView`'s branch, `ReportPdfEndpoint.TimeByPersonTables` and its call, and `ReportTimeByPerson.razor`.
  - Add the alias in `ReportAddress.Parse`.
  - Reword the doc comments that named Time by person (`PeopleAsync`: "The people Time spent lists when they logged nothing"; `TimeReportRules`' class comment).
  - Leave Sprint status's *Time by person* chart alone.

- [ ] **Step 4: Rework `TimeReportRulesTests`:**
  - Delete RPT-001 (`Logged_is_all_the_persons_time_and_tasks_count_once`), RPT-003 (`Shared_task_shows_under_each_person_and_counts_once_in_the_total`), RPT-008 (`People_with_no_time_get_a_zero_row`) and the Time by person half of RPT-006 (`Ordering`). RPT-044 to RPT-048 cover them.
  - Rewrite RPT-002 and RPT-005 to call `TimeReportRules.Compare` directly, with the same tasks and the same assertions on the comparison.
  - Change the class summary to *"Estimate comparison and estimate accuracy (spec §12)."*

- [ ] **Step 5: Run the whole suite and check nothing is left**

  Run: `dotnet build Orbit.slnx -c Release`, then `dotnet test Orbit.slnx -c Release`, then `git grep -n "TimeByPerson" -- Orbit.Web Orbit.Tests`
  Expected: build succeeds; all tests PASS; the grep shows only the alias in `ReportRun.cs` and its test.

- [ ] **Step 6: Check in Chrome**: `/Reports` has no *Time by person* card. `/Reports/View?Kind=TimeByPerson&From=2026-10-01&To=2026-10-31` opens Time spent for October.

- [ ] **Step 7: Update `orbit-spec.md` and `README.md`:**
  - §12: remove the *Time by person* table row and its bullets under *Time reports*. Keep *Estimate accuracy*, and point to *Time spent* as the timesheet reading.
  - Mark RPT-001, RPT-003 and RPT-008 as retired in favour of RPT-044 to RPT-048, and note that RPT-002, RPT-005 and RPT-006 are now over `Compare` and Estimate accuracy. Add RPT-052.
  - §6.10: drop *Time by person*.
  - Add a closing bullet to decision 149 recording the removal and the old-link alias.
  - Extend step 133.
  - README: remove *Time by person*'s line.
  - Older decisions (43, 44) stay as history.

- [ ] **Step 8: Commit**

  ```bash
  git add -A Orbit.Web Orbit.Tests orbit-spec.md README.md
  git commit -m "Retire Time by person: Time spent replaces it, old links open it"
  ```
