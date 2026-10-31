using System.Globalization;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Components.Infrastructure;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// A report's filters as the address gives them (spec §12), read leniently as the Razor page's binding did: a bad value is no
/// filter. No kind is the first report, Closed count by person.
/// </summary>
public sealed record ReportAddress(
    ReportKind Kind, DateOnly? From, DateOnly? To, Guid? ProjectId, Guid? DepartmentId, Guid? OwnerId, Guid? AssigneeId,
    Guid? SprintId = null)
{
    public static ReportAddress Parse(
        string? kind, string? from, string? to, string? projectId, string? departmentId, string? ownerId, string? assigneeId,
        string? sprintId = null) =>
        new(FilterQuery.Enum<ReportKind>(kind) ?? ReportKind.ClosedByPerson, FilterQuery.Date(from), FilterQuery.Date(to),
            FilterChoice.Id(projectId), FilterChoice.Id(departmentId), FilterChoice.Id(ownerId), FilterChoice.Id(assigneeId),
            FilterChoice.Id(sprintId));
}

/// <summary>
/// One run of a report (spec §12): its filters as they apply, after defaulting, its result and the line that sums the filters
/// up. The report page and its PDF (<see cref="ReportPdfEndpoint"/>) both build one, so the screen and the PDF can't drift apart.
/// Each call takes the services from the caller's scope: an <see cref="OrbitRunner"/> operation's, or the PDF request's.
/// </summary>
public sealed class ReportRun
{
    // The address keys, as the Razor page bound them, so existing links and bookmarks keep working.
    public const string KindKey = "Kind", FromKey = "From", ToKey = "To", ProjectKey = "ProjectId", DepartmentKey = "DepartmentId",
        OwnerKey = "OwnerId", AssigneeKey = "AssigneeId", SprintKey = "SprintId";

    private static readonly string[] Keys = [KindKey, SprintKey, FromKey, ToKey, ProjectKey, DepartmentKey, OwnerKey, AssigneeKey];

    private ReportRun() { }

    public ReportKind Kind { get; private set; }
    /// <summary>The period; for Sprint status, the sprint's first and last days.</summary>
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? DepartmentId { get; private set; }
    public Guid? OwnerId { get; private set; }
    public Guid? AssigneeId { get; private set; }
    /// <summary>The sprint reported on (<see cref="ReportDefinition.BySprint"/>): the one chosen, or the one the report defaulted to.</summary>
    public Guid? SprintId { get; private set; }

    /// <summary>reports.view at Department scope fixes the department to the viewer's own (§6.5): the picker is hidden.</summary>
    public bool CanChooseDepartment { get; private set; }
    public ReportDefinition Definition { get; private set; } = null!;
    public PersonCountReport? Counts { get; private set; }
    public MeanTimeReport? MeanTime { get; private set; }
    public TimeByPersonReport? TimeByPerson { get; private set; }
    public EstimateAccuracyReport? EstimateAccuracy { get; private set; }
    public ProjectStatusReport? ProjectReport { get; private set; }
    public AssetStatusReport? AssetReport { get; private set; }
    public SprintStatusReport? SprintReport { get; private set; }
    public string FilterSummary { get; private set; } = string.Empty;

    /// <summary>The Project filter's projects (archived ones too, as reports look back), without "All". Only with options.</summary>
    public IReadOnlyList<(string Value, string Text)> ProjectOptions { get; private set; } = [];
    /// <summary>The Department filter's departments, without "All". Only with options, and when <see cref="CanChooseDepartment"/>.</summary>
    public IReadOnlyList<(string Value, string Text)> DepartmentOptions { get; private set; } = [];
    /// <summary>The Owner and Assignee filters (<see cref="ReportDefinition.ByOwnerAndAssignee"/>), from <see cref="ReportingService.ProjectReportPeopleAsync"/>.</summary>
    public IReadOnlyList<(string Value, string Text)> OwnerOptions { get; private set; } = [];
    public IReadOnlyList<(string Value, string Text)> AssigneeOptions { get; private set; } = [];
    /// <summary>The Sprint filter's sprints (<see cref="ReportDefinition.BySprint"/>), with no "All". Only with options.</summary>
    public IReadOnlyList<(string Value, string Text)> SprintOptions { get; private set; } = [];

    public string FileName => $"orbit-{Kind.ToString().ToLowerInvariant()}-{From:yyyyMMdd}-{To:yyyyMMdd}.pdf";

    /// <summary>
    /// The filters in force as a query string (without "?"), with <paramref name="key"/> changed to <paramref name="value"/> and the
    /// keys in <paramref name="clear"/> dropped: the fields the Razor page's form sent, so a fixed department stays out of the
    /// address. A report by sprint carries its sprint and no dates; the others their dates and no sprint.
    /// </summary>
    public string Query(string? key = null, string? value = null, params string[] clear)
    {
        var bySprint = Definition.BySprint;
        var filters = new Dictionary<string, string?>
        {
            [KindKey] = Kind.ToString(),
            [SprintKey] = bySprint ? SprintId?.ToString() : null,
            [FromKey] = bySprint ? null : From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [ToKey] = bySprint ? null : To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [ProjectKey] = ProjectId?.ToString(),
            [DepartmentKey] = CanChooseDepartment ? DepartmentId?.ToString() : null,
            [OwnerKey] = OwnerId?.ToString(),
            [AssigneeKey] = AssigneeId?.ToString()
        };
        if (key is not null) filters[key] = value;
        foreach (var k in clear) filters[k] = null;
        var query = new FilterQuery();
        foreach (var k in Keys) query.Add(k, filters[k]);
        return query.ToString();
    }

    /// <summary>
    /// Runs the report. <paramref name="withOptions"/> also lists the projects and departments the filters offer, which the page
    /// needs and the PDF doesn't.
    /// </summary>
    public static async Task<ReportRun> RunAsync(IServiceProvider services, ReportAddress filters, bool withOptions, CancellationToken ct = default)
    {
        var reporting = services.Get<ReportingService>();
        var projects = services.Get<ProjectService>();
        var departments = services.Get<DepartmentService>();
        var actor = await services.Get<IActorProvider>().GetAsync(ct);

        var run = new ReportRun
        {
            Kind = filters.Kind, ProjectId = filters.ProjectId, DepartmentId = filters.DepartmentId,
            OwnerId = filters.OwnerId, AssigneeId = filters.AssigneeId
        };
        var restricted = ReportingService.RestrictedDepartment(actor);
        run.CanChooseDepartment = restricted is null;
        if (restricted is Guid r) run.DepartmentId = r == Guid.Empty ? null : r; // the service enforces it either way
        run.Definition = ReportCatalog.Get(run.Kind);
        if (!run.Definition.ByProject) run.ProjectId = null; // assets belong to no project
        if (!run.Definition.ByOwnerAndAssignee) run.OwnerId = null;
        if (!run.Definition.ByAssignee) run.AssigneeId = null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var to = filters.To ?? today;
        var from = filters.From ?? to.AddDays(-30);
        if (to < from) (from, to) = (to, from);
        run.From = from;
        run.To = to;

        var fromUtc = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var filter = new ReportFilter(fromUtc, toUtc, run.ProjectId, run.DepartmentId, run.OwnerId, run.AssigneeId);

        switch (run.Kind)
        {
            case ReportKind.ClosedByPerson: run.Counts = await reporting.ClosedByPersonAsync(filter, ct); break;
            case ReportKind.CreatedByPerson: run.Counts = await reporting.CreatedByPersonAsync(filter, ct); break;
            case ReportKind.MeanTimeToRespond: run.MeanTime = await reporting.MeanTimeToRespondAsync(filter, ct); break;
            case ReportKind.MeanTimeToResolve: run.MeanTime = await reporting.MeanTimeToResolveAsync(filter, ct); break;
            case ReportKind.TimeByPerson: run.TimeByPerson = await reporting.TimeByPersonAsync(filter, ct); break;
            case ReportKind.EstimateAccuracy: run.EstimateAccuracy = await reporting.EstimateAccuracyAsync(filter, ct); break;
            case ReportKind.ProjectStatus: run.ProjectReport = await reporting.ProjectStatusAsync(filter, ct); break;
            case ReportKind.AssetStatus: run.AssetReport = await reporting.AssetStatusAsync(filter, ct); break;
            case ReportKind.SprintStatus:
                run.SprintReport = await reporting.SprintStatusAsync(
                    new SprintReportFilter(filters.SprintId, run.ProjectId, run.DepartmentId, run.AssigneeId), ct);
                if (run.SprintReport.Sprint is { } sprint)
                {
                    run.SprintId = sprint.Id;
                    run.From = sprint.StartDate;
                    run.To = sprint.EndDate;
                }
                break;
        }

        if (run.SprintReport is { } sprintReport)
        {
            await SprintFiltersAsync(run, sprintReport, departments, withOptions, ct);
            return run;
        }

        var parts = new List<string> { $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}" };
        await AddDepartmentAsync(run, parts, departments, ct);
        ProjectStatusSummary? chosenProject = null;
        if (run.ProjectId is Guid p)
        {
            chosenProject = await projects.GetStatusAsync(p, ct);
            parts.Add("Project: " + chosenProject.Name);
        }
        if (run.Definition.ByOwnerAndAssignee)
        {
            var people = await reporting.ProjectReportPeopleAsync(ct);
            run.OwnerOptions = PersonOptions(people.Owners, run.OwnerId);
            run.AssigneeOptions = PersonOptions(people.Assignees, run.AssigneeId);
            if (run.OwnerId is Guid o) parts.Add("Owner: " + PersonName(people.Owners, o));
            if (run.AssigneeId is Guid a) parts.Add("Assignee: " + PersonName(people.Assignees, a));
        }
        run.FilterSummary = string.Join(" · ", parts);

        if (withOptions)
        {
            if (run.Definition.ByProject)
            {
                var options = (await projects.ListOpenForPickerAsync(includeArchived: true, ct: ct))
                    .Select(x => (x.Id.ToString(), ProjectText(x.Department.Name, x.Name, x.Status))).ToList();
                // A project chosen but not offered (a shared one, from a pasted link) stays selected under its name.
                if (chosenProject is { } chosen && options.All(x => x.Item1 != chosen.Id.ToString()))
                    options.Add((chosen.Id.ToString(), ProjectText(chosen.DepartmentName, chosen.Name, chosen.Status)));
                run.ProjectOptions = options;
            }
            await DepartmentOptionsAsync(run, departments, ct);
        }
        return run;
    }

    /// <summary>
    /// Sprint status's summary line and options (§12): the sprint, then the filters. The sprint, project and assignee options are
    /// the report's own - the sprints it offers, the projects with a row (work of any kind in the sprint), the people on its tasks -
    /// and name the chosen ones, so a Department-scoped viewer is never refused a project they can't open.
    /// </summary>
    private static async Task SprintFiltersAsync(
        ReportRun run, SprintStatusReport report, DepartmentService departments, bool withOptions, CancellationToken ct)
    {
        var parts = new List<string> { report.Sprint is { } s ? SprintText(s) : "No sprint has started yet" };
        await AddDepartmentAsync(run, parts, departments, ct);
        if (run.ProjectId is Guid p) parts.Add("Project: " + (report.ProjectOptions.FirstOrDefault(x => x.Id == p)?.Name ?? "Unknown"));
        if (run.AssigneeId is Guid a) parts.Add("Assignee: " + PersonName(report.AssigneeOptions, a));
        run.FilterSummary = string.Join(" · ", parts);
        if (!withOptions) return;

        var sprints = report.Sprints.Select(x => (x.Id.ToString(), SprintText(x))).ToList();
        // A sprint chosen but not offered (a planned one, from a link) stays selected under its name.
        if (report.Sprint is { } chosen && sprints.All(x => x.Item1 != chosen.Id.ToString()))
            sprints.Add((chosen.Id.ToString(), SprintText(chosen)));
        run.SprintOptions = sprints;

        var projects = report.ProjectOptions.Select(x => (x.Id.ToString(), $"{x.DepartmentName} / {x.Name}")).ToList();
        if (run.ProjectId is Guid chosenProject && projects.All(x => x.Item1 != chosenProject.ToString()))
            projects.Add((chosenProject.ToString(), "Unknown project"));
        run.ProjectOptions = projects;
        run.AssigneeOptions = PersonOptions(report.AssigneeOptions, run.AssigneeId);
        await DepartmentOptionsAsync(run, departments, ct);
    }

    /// <summary>The department chosen, in the summary line.</summary>
    private static async Task AddDepartmentAsync(ReportRun run, List<string> parts, DepartmentService departments, CancellationToken ct)
    {
        if (run.DepartmentId is Guid d) parts.Add("Department: " + (await departments.GetAsync(d, ct)).Name);
    }

    /// <summary>The Department filter's departments, when the viewer may choose one.</summary>
    private static async Task DepartmentOptionsAsync(ReportRun run, DepartmentService departments, CancellationToken ct)
    {
        if (run.CanChooseDepartment)
            run.DepartmentOptions = (await departments.ListAsync(true, ct)).Select(x => (x.Id.ToString(), x.Name)).ToList();
    }

    /// <summary>
    /// The Sprint status report's address: <paramref name="sprintId"/>'s, or with none the report's default sprint, the active one
    /// or the latest completed one. The Reports page and a sprint's page link to it.
    /// </summary>
    public static string SprintLink(Guid? sprintId = null) =>
        $"/Reports/View?{new FilterQuery().Add(KindKey, nameof(ReportKind.SprintStatus)).Add(SprintKey, sprintId)}";

    /// <summary>"Sprint 14 (Active) · 2026-10-19 to 2026-10-30".</summary>
    public static string SprintText(SprintFacts s) => $"{s.Name} ({s.Status}) · {s.StartDate:yyyy-MM-dd} to {s.EndDate:yyyy-MM-dd}";

    private static string ProjectText(string department, string name, ProjectStatus status) =>
        $"{department} / {name}{(status == ProjectStatus.Archived ? " (archived)" : "")}";

    /// <summary>A person filter's options. One chosen but not offered (from a pasted link) stays selected, as "Unknown".</summary>
    private static List<(string Value, string Text)> PersonOptions(IReadOnlyList<ReportPerson> people, Guid? selected)
    {
        var items = people.Select(x => (x.Id.ToString(), x.IsActive ? x.Name : $"{x.Name} (inactive)")).ToList();
        if (selected is Guid s && people.All(x => x.Id != s)) items.Add((s.ToString(), PersonName(people, s)));
        return items;
    }

    private static string PersonName(IReadOnlyList<ReportPerson> people, Guid id) => people.FirstOrDefault(x => x.Id == id)?.Name ?? "Unknown";
}
