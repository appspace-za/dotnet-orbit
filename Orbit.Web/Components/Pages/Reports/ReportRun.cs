using System.Globalization;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Components.Infrastructure;
using Orbit.Data.Entities;

namespace Orbit.Components.Pages.Reports;

/// <summary>
/// A report's filters as the address gives them (spec §12), read leniently as the Razor page's binding did: a bad value is no
/// filter. No kind is the first report, Closed count by person.
/// </summary>
public sealed record ReportAddress(
    ReportKind Kind, DateOnly? From, DateOnly? To, Guid? ProjectId, Guid? DepartmentId, Guid? OwnerId, Guid? AssigneeId)
{
    public static ReportAddress Parse(
        string? kind, string? from, string? to, string? projectId, string? departmentId, string? ownerId, string? assigneeId) =>
        new(FilterQuery.Enum<ReportKind>(kind) ?? ReportKind.ClosedByPerson, FilterQuery.Date(from), FilterQuery.Date(to),
            FilterChoice.Id(projectId), FilterChoice.Id(departmentId), FilterChoice.Id(ownerId), FilterChoice.Id(assigneeId));
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
        OwnerKey = "OwnerId", AssigneeKey = "AssigneeId";

    private static readonly string[] Keys = [KindKey, FromKey, ToKey, ProjectKey, DepartmentKey, OwnerKey, AssigneeKey];

    private ReportRun() { }

    public ReportKind Kind { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? DepartmentId { get; private set; }
    public Guid? OwnerId { get; private set; }
    public Guid? AssigneeId { get; private set; }

    /// <summary>reports.view at Department scope fixes the department to the viewer's own (§6.5): the picker is hidden.</summary>
    public bool CanChooseDepartment { get; private set; }
    public ReportDefinition Definition { get; private set; } = null!;
    public PersonCountReport? Counts { get; private set; }
    public MeanTimeReport? MeanTime { get; private set; }
    public TimeByPersonReport? TimeByPerson { get; private set; }
    public EstimateAccuracyReport? EstimateAccuracy { get; private set; }
    public ProjectStatusReport? ProjectReport { get; private set; }
    public AssetStatusReport? AssetReport { get; private set; }
    public string FilterSummary { get; private set; } = string.Empty;

    /// <summary>The Project filter's projects (archived ones too, as reports look back), without "All". Only with options.</summary>
    public IReadOnlyList<(string Value, string Text)> ProjectOptions { get; private set; } = [];
    /// <summary>The Department filter's departments, without "All". Only with options, and when <see cref="CanChooseDepartment"/>.</summary>
    public IReadOnlyList<(string Value, string Text)> DepartmentOptions { get; private set; } = [];
    /// <summary>The Owner and Assignee filters (<see cref="ReportDefinition.ByOwnerAndAssignee"/>), from <see cref="ReportingService.ProjectReportPeopleAsync"/>.</summary>
    public IReadOnlyList<(string Value, string Text)> OwnerOptions { get; private set; } = [];
    public IReadOnlyList<(string Value, string Text)> AssigneeOptions { get; private set; } = [];

    public string FileName => $"orbit-{Kind.ToString().ToLowerInvariant()}-{From:yyyyMMdd}-{To:yyyyMMdd}.pdf";

    /// <summary>
    /// The filters in force as a query string (without "?"), with <paramref name="key"/> changed to <paramref name="value"/>: the
    /// fields the Razor page's form sent, so a fixed department stays out of the address.
    /// </summary>
    public string Query(string? key = null, string? value = null)
    {
        var filters = new Dictionary<string, string?>
        {
            [KindKey] = Kind.ToString(),
            [FromKey] = From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [ToKey] = To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [ProjectKey] = ProjectId?.ToString(),
            [DepartmentKey] = CanChooseDepartment ? DepartmentId?.ToString() : null,
            [OwnerKey] = OwnerId?.ToString(),
            [AssigneeKey] = AssigneeId?.ToString()
        };
        if (key is not null) filters[key] = value;
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
        if (!run.Definition.ByOwnerAndAssignee) run.OwnerId = run.AssigneeId = null;
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
        }

        var parts = new List<string> { $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}" };
        if (run.DepartmentId is Guid d) parts.Add("Department: " + (await departments.GetAsync(d, ct)).Name);
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
            if (run.CanChooseDepartment)
                run.DepartmentOptions = (await departments.ListAsync(true, ct)).Select(x => (x.Id.ToString(), x.Name)).ToList();
        }
        return run;
    }

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
