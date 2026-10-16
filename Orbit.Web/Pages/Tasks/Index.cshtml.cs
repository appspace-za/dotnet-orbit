using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Pages.Tasks;

public class IndexModel(
    TaskService tasks,
    IActorProvider actors,
    DepartmentService departments,
    ProjectService projects,
    UserDirectoryService users,
    TaskStructureService structure,
    AssetService assets,
    SprintService sprints) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public TaskFilter Filter { get; set; } = new();
    /// <summary>"Reset": forget the remembered filter and show the plain list.</summary>
    [BindProperty(SupportsGet = true)] public bool Reset { get; set; }
    /// <summary>
    /// The page of results. Not "Page": Razor Pages keeps its own "page" route value (the page's path), which wins over a
    /// "page" query parameter when binding and swallows one when links are built - so every page was page 1.
    /// </summary>
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    /// <summary>
    /// The Sprint filter (§6.2): empty for all, <see cref="BacklogSprint"/> for the tasks in no sprint, or a sprint's id. One
    /// select covers both <see cref="TaskFilter.SprintId"/> and <see cref="TaskFilter.BacklogOnly"/>. Not "Sprint", which
    /// would hide the entity type in this class.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = SprintKey)] public string? SprintChoice { get; set; }
    public const string BacklogSprint = "backlog";
    private const string SprintKey = "Sprint";
    /// <summary>
    /// The Project, Assignee and Requestee filters (§6.2): empty for all, <see cref="FilterChoice.None"/> for the tasks with none,
    /// or an id. Bound under the filter's own key; <see cref="Filter"/>'s Guid binds the same key and fails quietly on "none",
    /// so <see cref="OnGetAsync"/> sets the typed fields from these.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = nameof(TaskFilter.ProjectId))] public string? ProjectChoice { get; set; }
    [BindProperty(SupportsGet = true, Name = nameof(TaskFilter.AssigneeId))] public string? AssigneeChoice { get; set; }
    [BindProperty(SupportsGet = true, Name = nameof(TaskFilter.RequesteeId))] public string? RequesteeChoice { get; set; }

    /// <summary>
    /// The filter fields remembered for the session (§6.2). The page number and the one-off "subtasks of one task"
    /// drill-down are not: a drill-down link neither replaces nor is replaced by the remembered filter.
    /// </summary>
    public static readonly string[] RememberedFilters =
    [
        nameof(TaskFilter.Search), nameof(TaskFilter.DepartmentId), nameof(TaskFilter.ProjectId), nameof(TaskFilter.Status),
        nameof(TaskFilter.AssigneeId), nameof(TaskFilter.Priority), nameof(TaskFilter.Type),
        nameof(TaskFilter.Source), nameof(TaskFilter.DueAfter), nameof(TaskFilter.DueBefore), nameof(TaskFilter.OpenOnly),
        nameof(TaskFilter.PlannedToday), nameof(TaskFilter.PlannedFor), nameof(TaskFilter.AssetId), nameof(TaskFilter.RequesteeId),
        SprintKey
    ];
    private const string MemoryKey = "tasks";

    public Actor Actor { get; private set; } = null!;
    public PagedResult<TaskItem> Result { get; private set; } = null!;
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ProjectItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> AssigneeItems { get; private set; } = [];
    /// <summary>The Requestee filter (§6.2.2): "Me" first, then the same people as the Assignee filter.</summary>
    public IReadOnlyList<SelectListItem> RequesteeItems { get; private set; } = [];
    /// <summary>Candidates for the inline assignee control: everyone the actor may assign to, across all listed departments.</summary>
    public IReadOnlyList<UserSummary> QuickEditAssignees { get; private set; } = [];
    /// <summary>The sprints the inline sprint control offers (§6.3): the planned and active ones.</summary>
    public IReadOnlyList<Sprint> OpenSprints { get; private set; } = [];
    /// <summary>The Sprint filter's sprints (§6.2): the open ones, plus a chosen one that has since been completed.</summary>
    public IReadOnlyList<SelectListItem> SprintItems { get; private set; } = [];
    /// <summary>Tasks on this page whose next move is gated by a dependency (§6.15).</summary>
    public IReadOnlyDictionary<Guid, WaitingSummary> Waiting { get; private set; } = new Dictionary<Guid, WaitingSummary>();
    /// <summary>The Asset filter's picker (§6.19) is offered to whoever can see assets.</summary>
    public bool CanFilterByAsset { get; private set; }
    /// <summary>The filtered asset's number and name, for the filter's chip.</summary>
    public string? AssetFilterLabel { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        if (Reset)
        {
            FilterMemory.Forget(Response, MemoryKey);
            return RedirectToPage();
        }
        if (FilterMemory.IsExplicit(Request, RememberedFilters))
            FilterMemory.Remember(Request, Response, MemoryKey, Actor.UserId, RememberedFilters);
        else if (Filter.ParentTaskId is null && FilterMemory.Recall(Request, MemoryKey, Actor.UserId) is string remembered)
            return LocalRedirect(Request.Path + remembered);

        Filter.BacklogOnly = SprintChoice == BacklogSprint;
        Filter.SprintId = Guid.TryParse(SprintChoice, out var sprintFilter) ? sprintFilter : null;
        Filter.NoProject = FilterChoice.IsNone(ProjectChoice);
        Filter.ProjectId = FilterChoice.Id(ProjectChoice);
        Filter.Unassigned = FilterChoice.IsNone(AssigneeChoice);
        Filter.AssigneeId = FilterChoice.Id(AssigneeChoice);
        Filter.NoRequestee = FilterChoice.IsNone(RequesteeChoice);
        Filter.RequesteeId = FilterChoice.Id(RequesteeChoice);
        Filter.Page = PageNumber;
        Filter.PageSize = 50;
        Result = await tasks.ListAsync(Filter, ct);
        Waiting = await structure.GetWaitingAsync(Result.Items, ct);

        if (Actor.CanAnywhere(Permission.TasksView))
        {
            DepartmentItems = (await departments.ListAsync(true, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == Filter.DepartmentId)).ToList();
        }
        ProjectItems = (await projects.ListOpenForPickerAsync(Filter.DepartmentId, ct: ct))
            .Select(p => new SelectListItem(Actor.CanAnywhere(Permission.TasksView) ? $"{p.Department.Name} / {p.Name}" : p.Name, p.Id.ToString(), p.Id == Filter.ProjectId)).ToList();
        var people = Actor.CanAnywhere(Permission.TasksView)
            ? await users.ListAsync(null, Filter.DepartmentId, false, ct)
            : Actor.DepartmentId is Guid ownDept ? await users.GetAssignableAsync(ownDept, ct) : [];
        AssigneeItems = people.Select(u => new SelectListItem(u.DisplayName, u.Id.ToString(), u.Id == Filter.AssigneeId)).ToList();
        var requesteeItems = new List<SelectListItem>();
        if (Actor.UserId is Guid me) requesteeItems.Add(new SelectListItem("Me", me.ToString(), Filter.RequesteeId == me));
        requesteeItems.AddRange(people.Where(u => u.Id != Actor.UserId)
            .Select(u => new SelectListItem(u.DisplayName, u.Id.ToString(), u.Id == Filter.RequesteeId)));
        // Someone outside the list (another department's person, from a shared link) stays selected rather than silently dropped.
        if (Filter.RequesteeId is Guid requesteeId && requesteeItems.All(i => i.Value != requesteeId.ToString())
            && await users.FindAsync(requesteeId, ct) is { } requestee)
            requesteeItems.Add(new SelectListItem(requestee.DisplayName, requestee.Id.ToString(), true));
        RequesteeItems = requesteeItems;
        // The filter list may be narrowed to one department; the inline control needs candidates for every listed task.
        QuickEditAssignees = await users.GetQuickEditCandidatesAsync(ct);
        OpenSprints = await sprints.ListOpenAsync(ct);
        var sprintItems = OpenSprints
            .Select(s => new SelectListItem($"{s.Name} ({s.Status})", s.Id.ToString(), s.Id == Filter.SprintId)).ToList();
        // A remembered or linked sprint that has since been completed stays selected rather than silently dropped.
        if (Filter.SprintId is Guid chosenSprint && sprintItems.All(i => !i.Selected))
            sprintItems.Add(new SelectListItem(
                await sprints.FindAsync(chosenSprint, ct) is { } sprint ? $"{sprint.Name} ({sprint.Status})" : "Unknown sprint",
                chosenSprint.ToString(), true));
        SprintItems = sprintItems;
        CanFilterByAsset = Actor.Has(Permission.AssetsView);
        if (Filter.AssetId is Guid assetId)
            AssetFilterLabel = await assets.GetRefAsync(assetId, ct) is { } asset ? AssetRules.Label(asset.AssetNumber, asset.Name) : "(an asset you can't see)";
        return Page();
    }

    public string PageUrl(int page) => Url.Page("/Tasks/Index", new
    {
        ProjectId = ProjectChoice, Filter.DepartmentId, Filter.Status, AssigneeId = AssigneeChoice, Filter.Priority, Filter.Type, Filter.Source,
        Filter.DueBefore, Filter.DueAfter, Filter.OpenOnly, Filter.PlannedToday, Filter.PlannedFor, Filter.ParentTaskId, Filter.Search,
        Filter.AssetId, RequesteeId = RequesteeChoice, Sprint = SprintChoice, PageNumber = page
    })!;
}
