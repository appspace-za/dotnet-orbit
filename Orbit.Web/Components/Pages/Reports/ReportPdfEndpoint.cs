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
                q[ReportRun.DepartmentKey], q[ReportRun.OwnerKey], q[ReportRun.AssigneeKey]);
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
                landscape: run.ProjectReport is not null || run.AssetReport is not null);
            return Results.File(bytes, "application/pdf", run.FileName);
        }).RequireAuthorization(Policies.Permission(Permission.ReportsView));
        return endpoints;
    }

    private static List<ReportPdfBuilder.Table> Tables(ReportRun run)
    {
        var tables = new List<ReportPdfBuilder.Table>();
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
        return tables;
    }

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
