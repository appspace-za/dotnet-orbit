using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Auth;
using Orbit.Data.Entities;
using Orbit.Reporting;
using static Orbit.Components.Pages.Reports.ReportText;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// A report's PDF (spec §12), as the Razor page's <c>OnGetPdfAsync</c> handler made it: the same address keys as the report page,
/// the same <see cref="ReportRun"/>, and the tables through <see cref="ReportPdfBuilder"/>. It is a plain request, so the services
/// come from the request's own scope and the actor is the signed-in user. The door is reports.view at any scope, as for the
/// pages; <see cref="ReportingService"/> applies the scope (§6.5).
/// </summary>
public static class ReportPdfEndpoint
{
    public const string Path = "/Reports/Pdf";

    public static IEndpointRouteBuilder MapReportPdfEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, async (HttpContext http) =>
        {
            var q = http.Request.Query;
            var address = ReportAddress.Parse(q[ReportRun.KindKey], q[ReportRun.FromKey], q[ReportRun.ToKey], q[ReportRun.ProjectKey],
                q[ReportRun.DepartmentKey], q[ReportRun.OwnerKey], q[ReportRun.AssigneeKey], q[ReportRun.SprintKey]);
            ReportRun run;
            try
            {
                run = await ReportRun.RunAsync(http.RequestServices, address, withOptions: false, http.RequestAborted);
            }
            // As the Razor handler was answered: not found is a 404, a refusal the access-denied page with its reason.
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (ForbiddenException ex)
            {
                return AccessDeniedReason.Forbid(http, ex);
            }
            var bytes = ReportPdfBuilder.Build(run.Definition.Title, run.FilterSummary, Tables(run),
                landscape: run.ProjectReport is not null || run.AssetReport is not null || run.SprintReport is not null);
            return Results.File(bytes, "application/pdf", run.FileName);
        }).RequireAuthorization(Policies.Permission(Permission.ReportsView));
        return endpoints;
    }

    private static List<ReportPdfBuilder.Section> Tables(ReportRun run)
    {
        var tables = new List<ReportPdfBuilder.Section>();
        if (run.Counts is { } counts)
        {
            var rows = counts.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.Count.ToString()]).ToList();
            rows.Add(["Total", counts.Total.ToString()]);
            tables.Add(new ReportPdfBuilder.Table(["Person", "Count"], rows));
        }
        if (run.MeanTime is { } meanTime)
        {
            var rows = meanTime.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.Count.ToString(), DurationFormat.Hours(r.AverageHours)]).ToList();
            rows.Add(["Overall", meanTime.OverallCount.ToString(), DurationFormat.Hours(meanTime.OverallAverageHours)]);
            tables.Add(new ReportPdfBuilder.Table(["Assignee", "Tasks", "Average"], rows));
        }
        if (run.TimeByPerson is not null) tables.AddRange(TimeByPersonTables(run.TimeByPerson));
        if (run.EstimateAccuracy is not null) tables.AddRange(EstimateAccuracyTables(run.EstimateAccuracy));
        if (run.ProjectReport is not null) tables.AddRange(ProjectStatusTables(run.ProjectReport));
        if (run.AssetReport is not null) tables.AddRange(AssetStatusTables(run.AssetReport));
        if (run.SprintReport is not null) tables.AddRange(SprintStatusSections(run.SprintReport));
        return tables;
    }

    /// <summary>
    /// Sprint status: the seven charts, the summary (planned, each unplanned kind shown, and all), the people, the groups, and each
    /// group's people and tasks - the page's tables, in its order. The Backlog and other-sprint columns and rows show only when such
    /// tasks were worked on (<see cref="SprintStatusSummary.Unplanned"/>), as on the page.
    /// </summary>
    private static IEnumerable<ReportPdfBuilder.Section> SprintStatusSections(SprintStatusReport r)
    {
        if (r.Sprint is null)
        {
            yield return new ReportPdfBuilder.Table(["Sprint"], [], "No sprint has started yet.");
            yield break;
        }
        var s = r.Summary;
        var unplanned = s.Unplanned;
        yield return new ReportPdfBuilder.Table(["Goal", "Where it stands"],
            [[string.IsNullOrWhiteSpace(r.Sprint.Goal) ? "-" : r.Sprint.Goal, AsAt(r)]], Widths: [3, 2]);
        // Planned work outcome across the page, then the pairs: tasks on the left, time on the right.
        foreach (var charts in SprintCharts.PdfSections(r)) yield return charts;

        // A row of the summary: its label, then planned, each unplanned kind shown, and all work.
        IReadOnlyList<string> Cells(string label, string planned, Func<SprintWorkKind, string> each, string all) =>
            [label, planned, .. unplanned.Select(each), all];
        IReadOnlyList<string> Counts(string label, Func<SprintTaskCounts, string> f) =>
            Cells(label, f(s.Planned.Tasks), k => f(s.Of(k).Tasks), f(s.All));
        static string Minutes(int m) => TimeFormat.Minutes(m);
        // Planned's share is what the unplanned share leaves, as the page's figures have it, and the unplanned share is split over
        // its kinds so they add up to it: the row adds up to 100%.
        var shareOf = unplanned.Zip(SprintReportShare.Apportion(s.UnplannedPercent ?? 0, unplanned.Select(k => s.Of(k).SprintMinutes).ToList()))
            .ToDictionary(x => x.First, x => x.Second);
        IReadOnlyList<IReadOnlyList<string>> summary =
        [
            Counts("Tasks", c => c.Total.ToString()),
            Counts("Done", c => c.Total == 0 ? "-" : $"{c.Done} ({c.PercentDone}%)"),
            Counts("Left open", c => c.LeftOpen.ToString()),
            Counts("Estimate left on them", Remaining),
            Counts("Cancelled", c => c.Cancelled.ToString()),
            Counts("Carried in", c => c.CarriedIn.ToString()),
            Cells("Logged in sprint", Minutes(s.Planned.SprintMinutes), k => Minutes(s.Of(k).SprintMinutes), Minutes(s.SprintMinutes)),
            Cells("Share of sprint time", Percent(s.UnplannedPercent is int u ? 100 - u : null),
                k => s.SprintMinutes == 0 ? "-" : Percent(shareOf[k]), s.SprintMinutes == 0 ? "-" : "100%"),
            Cells("Logged to date", Minutes(s.Planned.ToDateMinutes), k => Minutes(s.Of(k).ToDateMinutes), Minutes(s.ToDateMinutes)),
            Cells("Estimated", Estimated(s.Planned.Estimate), k => Estimated(s.Of(k).Estimate), Estimated(s.AllEstimate)),
            Cells("Variance", Variance(s.Planned.Estimate), k => Variance(s.Of(k).Estimate), Variance(s.AllEstimate))
        ];
        yield return new ReportPdfBuilder.Table(["", "Planned", .. unplanned.Select(k => SprintWorkLook.Of(k).Name), "All work"],
            summary, "Summary", [2.2f, 2, .. unplanned.Select(_ => 2f), 2]);

        // A row of the people: the planned tasks, each unplanned kind's tasks, the time on planned work and on each unplanned kind.
        IReadOnlyList<string> PersonCells(string name, string done, string leftOpen, Func<SprintWorkKind, string> tasks,
            Func<SprintWorkKind, string> time, string share) =>
            [name, tasks(SprintWorkKind.Planned), done, leftOpen, .. unplanned.Select(tasks), time(SprintWorkKind.Planned),
             .. unplanned.Select(time), share];
        yield return new ReportPdfBuilder.Table(
            PersonCells("Person", "Done", "Left open", k => SprintWorkLook.Of(k).TasksColumn, k => SprintWorkLook.Of(k).TimeColumn,
                "Unplanned share"),
            r.People.Select(p => PersonCells(p.Name, p.PlannedDone.ToString(), p.PlannedLeftOpen.ToString(),
                k => p.Tasks.Of(k).ToString(), k => Minutes(p.Minutes.Of(k)), Percent(p.UnplannedPercent))).ToList(),
            "By person (time logged in the sprint)",
            [3, 1.3f, 1, 1.1f, .. unplanned.Select(_ => 1.5f), 1.4f, .. unplanned.Select(_ => 1.5f), 1.5f]);

        static IReadOnlyList<string> GroupCells(string name, SprintTaskCounts t, int sprint, int toDate, EstimateComparison e) =>
            [name, Progress(t), t.Done.ToString(), t.LeftOpen.ToString(), t.Cancelled.ToString(), Minutes(sprint), Minutes(toDate),
             Estimated(e), Variance(e)];
        var groups = r.Rows.Select(g => GroupCells(GroupLabel(g), g.Tasks, g.SprintMinutes, g.ToDateMinutes, g.Estimate)).ToList();
        if (groups.Count > 0)
        {
            groups.Add(GroupCells("Planned work", s.Planned.Tasks, s.Planned.SprintMinutes, s.Planned.ToDateMinutes, s.Planned.Estimate));
            foreach (var kind in unplanned)
            {
                var t = s.Of(kind);
                groups.Add(GroupCells(SprintWorkLook.Of(kind).Name, t.Tasks, t.SprintMinutes, t.ToDateMinutes, t.Estimate));
            }
            groups.Add(GroupCells("All work", s.All, s.SprintMinutes, s.ToDateMinutes, s.AllEstimate));
        }
        yield return new ReportPdfBuilder.Table(
            ["Project", "Progress", "Done", "Left open", "Cancelled", "Logged in sprint", "To date", "Estimated", "Variance"],
            groups, "By project", [3.4f, 1.4f, 0.9f, 1.1f, 1.1f, 1.4f, 1.2f, 1.3f, 1.9f]);

        foreach (var g in r.Rows)
        {
            var name = g.Project is { } project ? $"{project.Number} {project.Name}".Trim() : g.Name;
            var people = g.People.Select(x => (IReadOnlyList<string>)
                [x.Name, x.Tasks.ToString(), x.Done.ToString(), x.LeftOpen.ToString(), Estimated(x), Minutes(x.SprintMinutes),
                 Minutes(x.ToDateMinutes)]).ToList();
            yield return new ReportPdfBuilder.Table(["Person", "Tasks", "Done", "Left open", "Estimated", "Logged in sprint", "Logged to date"],
                people, $"{name}: people", [3.4f, 1, 1, 1.1f, 1.3f, 1.5f, 1.5f]);

            var isUnplanned = g.IsUnplanned;
            yield return new ReportPdfBuilder.Table(
                ["Task", isUnplanned ? "Project" : "Status", "Outcome", "Assignee", "Estimate", "In sprint", "To date", ""],
                g.TaskRows.Select(t => (IReadOnlyList<string>)
                    [$"{t.Number} {t.Title}", isUnplanned ? t.ProjectLabel ?? "-" : t.Status.Label(), Label(t.Outcome), t.Assignee,
                     Estimate(t.EstimateMinutes), Minutes(t.SprintMinutes), Minutes(t.TotalMinutes), Note(t)]).ToList(),
                $"{name}: tasks", [4.4f, 1.8f, 1.1f, 1.9f, 1.1f, 1.1f, 1.1f, 2.6f]);
        }
    }

    private static string GroupLabel(SprintGroupRow g) =>
        g.Project is { } p ? $"{p.Number} {p.Name}\n{p.DepartmentName}".Trim() : g.Name;

    private static string Tasks(int count, int unestimated) => unestimated == 0 ? count.ToString() : $"{count} ({unestimated} unestimated)";

    private static IEnumerable<ReportPdfBuilder.Table> TimeByPersonTables(TimeByPersonReport r)
    {
        var rows = r.Rows.Select(p => (IReadOnlyList<string>)
            [p.Name, TimeFormat.Minutes(p.LoggedMinutes), Tasks(p.TaskCount, p.UnestimatedTasks),
             Estimated(p.Estimate), Actual(p.Estimate), Variance(p.Estimate)]).ToList();
        if (rows.Count > 0)
            rows.Add(["Total", TimeFormat.Minutes(r.TotalLoggedMinutes), Tasks(r.TotalTasks, r.TotalUnestimatedTasks),
                Estimated(r.Total), Actual(r.Total), Variance(r.Total)]);
        yield return new(["Person", "Logged", "Tasks", "Estimated", "Actual to date", "Variance"], rows,
            Widths: [3, 1.4f, 2, 1.4f, 1.5f, 2]);

        foreach (var p in r.Rows.Where(p => p.Tasks.Count > 0)) // people with no time have only their summary row
        {
            yield return new(["Task", "Status", "Estimate", "Logged", "Actual to date", ""],
                p.Tasks.Select(t => (IReadOnlyList<string>)
                    [$"{t.Number} {t.Title}", t.Status.Label(), Estimate(t.EstimateMinutes), TimeFormat.Minutes(t.LoggedMinutes),
                     TimeFormat.Minutes(t.TotalMinutes), Marker(t.Status, t.EstimateMinutes, t.IsOver)]).ToList(),
                p.Name, [4.6f, 1.5f, 1.3f, 1.3f, 1.5f, 2.1f]);
        }
    }

    private static IEnumerable<ReportPdfBuilder.Table> EstimateAccuracyTables(EstimateAccuracyReport r)
    {
        var rows = r.Rows.Select(a => (IReadOnlyList<string>)
            [a.Name, a.DoneCount.ToString(), a.Estimate.EstimatedTasks.ToString(), Estimated(a.Estimate),
             Actual(a.Estimate), Variance(a.Estimate), a.Estimate.OverTasks.ToString()]).ToList();
        if (rows.Count > 0)
            rows.Add(["Overall", r.DoneCount.ToString(), r.Overall.EstimatedTasks.ToString(), Estimated(r.Overall),
                Actual(r.Overall), Variance(r.Overall), r.Overall.OverTasks.ToString()]);
        yield return new(["Assignee", "Done", "With estimate", "Estimated", "Actual", "Variance", "Over"], rows,
            Widths: [3, 1, 1.3f, 1.5f, 1.5f, 2, 1]);

        foreach (var a in r.Rows)
        {
            yield return new(["Task", "Estimate", "Actual", "Variance", ""],
                a.Tasks.Select(t => (IReadOnlyList<string>)
                    [$"{t.Number} {t.Title}", Estimate(t.EstimateMinutes), TimeFormat.Minutes(t.ActualMinutes),
                     t.VarianceMinutes is int v ? TimeFormat.Signed(v) : "-", Marker(TaskItemStatus.Done, t.EstimateMinutes, t.IsOver)]).ToList(),
                a.Name, [4.6f, 1.3f, 1.3f, 1.3f, 2.1f]);
        }
    }

    private static IEnumerable<ReportPdfBuilder.Table> ProjectStatusTables(ProjectStatusReport r)
    {
        static string Open(ProjectTaskCounts t)
        {
            var notes = new List<string>();
            if (t.Overdue > 0) notes.Add($"{t.Overdue} overdue");
            if (t.Blocked > 0) notes.Add($"{t.Blocked} blocked");
            return notes.Count == 0 ? t.Open.ToString() : $"{t.Open}\n{string.Join(", ", notes)}";
        }
        static string Status(ProjectStatusHistory h)
        {
            var lines = new List<string> { h.AtEnd.Label() };
            lines.AddRange(h.Changes.Select(Change));
            if (h.Current != h.AtEnd) lines.Add($"now {h.Current.Label()}");
            return string.Join("\n", lines);
        }
        static string Schedule(ProjectStatusRow p)
        {
            var lines = new List<string> { Date(p.Project.TargetDate) + (p.IsPastTarget ? " (past)" : "") };
            if (p.Schedule is { } s)
                lines.Add($"Buffer {s.BufferStatus.Label()}" + (s.IsStale ? " (out of date)" : ""));
            return string.Join("\n", lines);
        }

        var rows = r.Rows.Select(p => (IReadOnlyList<string>)
            [$"{p.Project.Number} {p.Project.Name}\n{p.Project.DepartmentName} · {p.Project.OwnerName}", Status(p.Status),
             Progress(p.Tasks), Open(p.Tasks), p.Tasks.CreatedInPeriod.ToString(), p.Tasks.DoneInPeriod.ToString(),
             TimeFormat.Minutes(p.LoggedInPeriodMinutes), TimeFormat.Minutes(p.LoggedToDateMinutes), Estimated(p.Estimate),
             Variance(p.Estimate), Schedule(p)]).ToList();
        if (rows.Count > 0)
            rows.Add(["All projects", "", Progress(r.Total.Tasks), Open(r.Total.Tasks), r.Total.Tasks.CreatedInPeriod.ToString(),
                r.Total.Tasks.DoneInPeriod.ToString(), TimeFormat.Minutes(r.Total.LoggedInPeriodMinutes),
                TimeFormat.Minutes(r.Total.LoggedToDateMinutes), Estimated(r.Total.Estimate), Variance(r.Total.Estimate), ""]);
        yield return new(["Project", "Status", "Progress", "Open", "Created", "Done", "Logged", "To date", "Estimated", "Variance", "Target"],
            rows, r.Rows.Count == 0 ? null : StatusCounts(r), [3, 2.4f, 1.5f, 1.4f, 1.3f, 1, 1.3f, 1.3f, 1.5f, 1.9f, 1.6f]);

        foreach (var p in r.Rows.Where(p => p.People.Count > 0 || p.TaskRows.Count > 0))
        {
            var name = $"{p.Project.Number} {p.Project.Name}";
            var people = p.People.Select(x => (IReadOnlyList<string>)
                [x.Name, x.OpenTasks.ToString(), x.DoneInPeriod.ToString(), Estimated(x),
                 TimeFormat.Minutes(x.LoggedInPeriodMinutes), TimeFormat.Minutes(x.LoggedToDateMinutes)]).ToList();
            people.Add(["Total", p.Tasks.Open.ToString(), p.Tasks.DoneInPeriod.ToString(), Estimated(p.Estimate),
                TimeFormat.Minutes(p.LoggedInPeriodMinutes), TimeFormat.Minutes(p.LoggedToDateMinutes)]);
            yield return new(["Person", "Open tasks", "Done in period", "Estimated", "Logged in period", "Logged to date"],
                people, $"{name}: people", [3.4f, 1.2f, 1.4f, 1.4f, 1.5f, 1.5f]);

            yield return new(["Task", "Status", "Assignee", "Due", "Estimate", "Logged in period", "Logged to date", ""],
                p.TaskRows.Select(t => (IReadOnlyList<string>)
                    [$"{t.Number} {t.Title}", t.Status.Label(), t.Assignee, Date(t.DueDate) + (t.IsOverdue ? " (overdue)" : ""),
                     Estimate(t.EstimateMinutes), TimeFormat.Minutes(t.PeriodMinutes), TimeFormat.Minutes(t.TotalMinutes),
                     Marker(t.Status, t.EstimateMinutes, t.IsOver)]).ToList(),
                $"{name}: tasks", [5, 1.3f, 2, 1.7f, 1.2f, 1.4f, 1.4f, 1.6f]);
        }
    }

    private static IEnumerable<ReportPdfBuilder.Table> AssetStatusTables(AssetStatusReport r)
    {
        static string Lines(string first, params string?[] more) =>
            string.Join("\n", more.Where(m => !string.IsNullOrEmpty(m)).Prepend(first));
        static string Value(AssetGroupCounts c) => Lines(HeldValue(c),
            c.HeldUnvalued > 0 && c.HeldUnvalued < c.Held ? $"{c.HeldUnvalued} unvalued" : null,
            c.DisposedValue > 0 ? $"{Ui.Money(c.DisposedValue)} disposed of" : null);
        static string Checks(AssetGroupCounts c) => Lines(Checked(c),
            c.CheckOverdue > 0 ? $"{c.CheckOverdue} overdue" : null, c.CheckNotOk > 0 ? $"{c.CheckNotOk} not OK" : null);
        static string Open(AssetTaskCounts t) => Lines(t.Open.ToString(), t.Overdue > 0 ? $"{t.Overdue} overdue" : null);
        static IReadOnlyList<string> Cells(string label, AssetGroupCounts c) =>
        [
            label, c.Active.ToString(), c.InStorage.ToString(), c.Damaged.ToString(), c.Lost.ToString(), c.Disposed.ToString(),
            c.Total.ToString(), c.Registered.ToString(), Value(c), Checks(c), Open(c.Tasks), c.Tasks.Created.ToString(),
            c.Tasks.Done.ToString(), TimeFormat.Minutes(c.Tasks.LoggedMinutes)
        ];
        static string Group(AssetGroupRow g) =>
            Lines(g.Name, string.Join(" · ", new[] { g.Category, g.DepartmentName }.Where(x => !string.IsNullOrEmpty(x))));
        static List<string> Headers(string first) =>
            [first, "Active", "In storage", "Damaged", "Lost", "Disposed", "Total", "Registered", "Value", "Checked", "Open tasks", "Created", "Done", "Logged"];
        // Fourteen columns: 9pt, and each column wide enough for its header (in points on a landscape page).
        float[] widths = [130, 42, 50, 54, 34, 54, 38, 62, 80, 60, 58, 50, 38, 46];
        const float size = 9;

        var byType = new List<IReadOnlyList<string>>();
        foreach (var t in r.ByType)
        {
            byType.Add(Cells(Group(t), t.Counts));
            byType.AddRange(t.Breakdown.Select(l => Cells("  · " + l.Name, l.Counts)));
        }
        if (byType.Count > 0) byType.Add(Cells("All types", r.Total));
        yield return new(Headers("Type"), byType, byType.Count == 0 ? null : AssetStatusCounts(r), widths, size);
        if (byType.Count == 0) yield break;

        var byLocation = r.ByLocation.Select(l => Cells(Group(l), l.Counts)).ToList();
        byLocation.Add(Cells("All locations", r.Total));
        yield return new(Headers("Location"), byLocation, "By location", widths, size);

        // One line per asset (its department is on its type above), so a row never splits across pages.
        if (r.Assets.Count == 0) yield break;
        var assets = r.Assets.Select(a => (IReadOnlyList<string>)
            [AssetLabel(a), a.TypeName, a.LocationName ?? AssetReportRules.NoLocation, a.Status.Label(),
             OpenOnOneLine(a.Tasks), a.Tasks.Created.ToString(), a.Tasks.Done.ToString(), TimeFormat.Minutes(a.Tasks.LoggedMinutes)]).ToList();
        assets.Add(["All assets", "", "", "", OpenOnOneLine(r.Total.Tasks), r.Total.Tasks.Created.ToString(), r.Total.Tasks.Done.ToString(),
            TimeFormat.Minutes(r.Total.Tasks.LoggedMinutes)]);
        yield return new(["Asset", "Type", "Location", "Status", "Open tasks", "Created", "Done", "Logged"], assets,
            "Assets with linked tasks", [3.8f, 2, 2, 1.3f, 1.9f, 1.1f, 1, 1.2f]);

        yield return new(["Asset", "Task", "Status", "Assignee", "Due", "Completed", "Logged in period"],
            r.Assets.SelectMany(a => a.TaskRows.Select(t => (IReadOnlyList<string>)
                [AssetLabel(a), $"{t.Number} {t.Title}", t.Status.Label(), t.Assignee, Date(t.DueDate) + (t.IsOverdue ? " (overdue)" : ""),
                 Day(t.CompletedAt), TimeFormat.Minutes(t.PeriodMinutes)])).ToList(),
            "Linked tasks", [3, 4.3f, 1.3f, 2, 2.6f, 1.4f, 1.4f]);

        static string OpenOnOneLine(AssetTaskCounts t) => t.Overdue > 0 ? $"{t.Open} ({t.Overdue} overdue)" : t.Open.ToString();
    }
}
