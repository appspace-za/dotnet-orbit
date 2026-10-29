using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Components.Shared;
using Orbit.Data.Entities;

namespace Orbit.Components.Pages.Tasks;

/// <summary>
/// A Project picker choice (§6.2): the project (Guid.Empty for "(standalone task)"), its text, its number (P-26-0003), which the
/// searchable picker matches too (§13 item 121), and its department, which the form's department follows.
/// </summary>
public sealed record ProjectChoice(Guid Id, string Text, string? Number, Guid? DepartmentId)
{
    /// <summary>The choice as the searchable picker lists it: the text, over the number.</summary>
    public PickOption<Guid> ToPick() => new(Id, Text, Number);
}

/// <summary>Select-list data for the task create/edit forms, built for the current actor.</summary>
public sealed class TaskFormLookups
{
    public required Actor Actor { get; init; }
    /// <summary>The department picker is offered to whoever may create tasks in every department (§6.2.1); everyone else files in the default department.</summary>
    public bool CanChooseDepartment { get; init; }
    public IReadOnlyList<SelectListItem> Departments { get; init; } = [];
    /// <summary>"(standalone task)", then the projects the actor may file the task under (plus the one it is already on).</summary>
    public IReadOnlyList<ProjectChoice> Projects { get; init; } = [];
    /// <summary>
    /// The people chosen in the Assignees picker (§6.2.3), for its chips: the saved assignees, or the ones just posted. Anyone
    /// assigned is among them whether or not they could still be picked, so a save never drops a colleague who has since been
    /// deactivated or changed department. The picker searches for the rest (UserDirectoryService.SearchAssignableAsync).
    /// </summary>
    public IReadOnlyList<UserSummary> SelectedAssignees { get; init; } = [];
    /// <summary>The department a task belongs to while the form names neither one nor a project: the caller's own.</summary>
    public Guid? DefaultDepartmentId { get; init; }
    /// <summary>The Sprint options (§6.3): Not Planned, the Backlog, then the planned and active sprints.</summary>
    public IReadOnlyList<(SprintPlacement Value, string Text)> Sprints { get; init; } = [];
    /// <summary>
    /// The task's current parent (§6.15), closed or not, for the Parent task picker to name; the picker searches for the rest
    /// (TaskStructureService.SearchParentCandidatesAsync). Null for a top-level task, or a parent that no longer exists.
    /// </summary>
    public ParentCandidate? SelectedParent { get; init; }
    /// <summary>
    /// The task being edited and every task below it, which the Parent task picker never offers (§6.15): worked out once when
    /// the form opens rather than on every search. Empty for a new task. The save still refuses a parent below the task.
    /// </summary>
    public IReadOnlyList<Guid> ExcludedParentIds { get; init; } = [];
    /// <summary>The asset picker is offered to whoever can see assets (assets.view, §6.19); the service checks the asset chosen.</summary>
    public bool CanPickAsset { get; init; }
    /// <summary>The chosen asset's number and name, for the picker's chip - or the read-only line for someone without the picker.</summary>
    public string? SelectedAssetLabel { get; init; }
    /// <summary>Whether the form carries the Requestee field (§6.2.2): the task forms do, the recurring form doesn't.</summary>
    public bool OfferRequestee { get; init; }
    /// <summary>Whether the caller may change the requestee here (<see cref="CanChangeRequestee"/>); otherwise it is shown and kept as it is.</summary>
    public bool CanChooseRequestee { get; init; }
    /// <summary>
    /// The people the task may be created for, "(nobody else)" (Guid.Empty) first; the current requestee is always among them.
    /// Each carries the person's email as its detail line, which the searchable picker matches too.
    /// </summary>
    public IReadOnlyList<PickOption<Guid>> Requestees { get; init; } = [];
    /// <summary>The current requestee's name, for the read-only line when the caller can't change it.</summary>
    public string? RequesteeLabel { get; init; }

    /// <summary>The asset a task or recurring task is linked to now, as the builders below take it.</summary>
    public static AssetRef? RefOf(Asset? asset) => asset is null ? null : new AssetRef(asset.Id, asset.AssetNumber, asset.Name);

    /// <summary>
    /// Whether the caller may set or change a task's requestee (§6.2.2): tasks.create_for above Own, and reaching the current
    /// requestee too - a requestee set by someone with a wider reach is shown and kept, not offered for change.
    /// </summary>
    public static bool CanChangeRequestee(Actor actor, ApplicationUser? current) =>
        AccessPolicy.CanCreateTasksForOthers(actor)
        && (current is null || AccessPolicy.CanCreateTaskFor(actor, current.Id, current.DepartmentId));

    private static string PersonName(ApplicationUser u) => u.IsActive ? u.DisplayName : $"{u.DisplayName} (deactivated)";

    /// <summary>Lookups without the Parent task picker - for the recurring-definition form, which shares these fields but has no parent (§6.15).</summary>
    public static Task<TaskFormLookups> BuildAsync(
        Actor actor,
        DepartmentService departments,
        ProjectService projects,
        UserDirectoryService users,
        SprintService sprints,
        AssetService assets,
        TaskForm form,
        AssetRef? currentAsset,
        CancellationToken ct) =>
        BuildAsync(actor, departments, projects, users, sprints, null, null, assets, form, currentAsset, false, null, ct);

    /// <param name="structure">Names the current parent for the Parent task picker; null for a form without one.</param>
    /// <param name="task">The task being edited, loaded through TaskService; null for a new one.</param>
    /// <param name="currentAsset">The asset the task is linked to now, named on the form even when the caller can't see it.</param>
    /// <param name="offerRequestee">Whether the form carries the Requestee field (§6.2.2).</param>
    /// <param name="currentRequestee">The task's saved requestee, kept on the form even when deactivated or out of the caller's reach.</param>
    public static async Task<TaskFormLookups> BuildAsync(
        Actor actor,
        DepartmentService departments,
        ProjectService projects,
        UserDirectoryService users,
        SprintService sprints,
        TaskStructureService? structure,
        TaskItem? task,
        AssetService assets,
        TaskForm form,
        AssetRef? currentAsset,
        bool offerRequestee,
        ApplicationUser? currentRequestee,
        CancellationToken ct)
    {
        var canChooseDepartment = actor.CanAnywhere(Permission.TasksCreate);
        var seesEverywhere = actor.CanAnywhere(Permission.TasksView);
        var deptItems = new List<SelectListItem>();
        if (canChooseDepartment)
        {
            deptItems.Add(new SelectListItem("(default)", string.Empty, form.DepartmentId is null));
            deptItems.AddRange((await departments.ListAsync(false, ct))
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == form.DepartmentId)));
        }

        var projectItems = new List<ProjectChoice> { new(Guid.Empty, "(standalone task)", null, null) };
        projectItems.AddRange((await projects.ListOpenForPickerAsync(null, form.ProjectId, ct: ct))
            .Select(p => new ProjectChoice(p.Id, seesEverywhere ? $"{p.Department.Name} / {p.Name}" : p.Name, p.Number, p.DepartmentId)));

        var selectedAssignees = await users.FindManyAsync(form.AssigneeIds, ct);

        var sprintItems = new List<(SprintPlacement Value, string Text)>
        {
            (SprintPlacement.NotPlanned, SprintPlacement.NotPlannedLabel), (SprintPlacement.Backlog, SprintPlacement.BacklogLabel)
        };
        sprintItems.AddRange((await sprints.ListOpenAsync(ct)).Select(s => (SprintPlacement.In(s.Id), $"{s.Name} ({s.Status})")));

        // The picker searches open tasks; the parent the task already sits under is named even when closed, so saving doesn't
        // silently detach it. The task's own subtree is worked out once, here, for the search to leave out.
        var selectedParent = structure is not null && form.ParentTaskId is Guid currentParent
            ? await structure.FindParentCandidateAsync(currentParent, task, ct)
            : null;
        var excludedParents = structure is not null && task is not null ? await structure.SubtreeIdsAsync(task, ct) : [];

        // The chip names the asset chosen: the current link whoever can see it, or another asset the caller can see.
        string? assetLabel = null;
        if (form.AssetId is Guid assetId)
        {
            var chosen = currentAsset?.Id == assetId ? currentAsset : await assets.GetRefAsync(assetId, ct);
            assetLabel = chosen is null ? "(an asset you can't see)" : AssetRules.Label(chosen.AssetNumber, chosen.Name);
        }

        // The requestee (§6.2.2): whom the caller may create tasks for, plus the saved one so a save never drops them silently.
        var canChooseRequestee = offerRequestee && CanChangeRequestee(actor, currentRequestee);
        var requesteeItems = new List<PickOption<Guid>>();
        if (canChooseRequestee)
        {
            var everywhere = actor.CanAnywhere(Permission.TasksCreateFor);
            requesteeItems.Add(new PickOption<Guid>(Guid.Empty, "(nobody else)"));
            var people = await users.PeopleInReachAsync(Permission.TasksCreateFor, ct);
            requesteeItems.AddRange(people.Select(u => new PickOption<Guid>(u.Id,
                (everywhere ? $"{u.DisplayName} ({u.DepartmentName ?? u.Role.Name})" : u.DisplayName) + (u.Id == actor.UserId ? " (you)" : ""),
                u.Email)));
            if (currentRequestee is not null && people.All(p => p.Id != currentRequestee.Id))
                requesteeItems.Insert(1, new PickOption<Guid>(currentRequestee.Id, PersonName(currentRequestee), currentRequestee.Email));
        }

        return new TaskFormLookups
        {
            Actor = actor,
            CanChooseDepartment = canChooseDepartment,
            Departments = deptItems,
            Projects = projectItems,
            SelectedAssignees = selectedAssignees,
            DefaultDepartmentId = actor.DepartmentId,
            Sprints = sprintItems,
            SelectedParent = selectedParent,
            ExcludedParentIds = excludedParents,
            CanPickAsset = actor.Has(Permission.AssetsView),
            SelectedAssetLabel = assetLabel,
            OfferRequestee = offerRequestee,
            CanChooseRequestee = canChooseRequestee,
            Requestees = requesteeItems,
            RequesteeLabel = currentRequestee is null ? null : PersonName(currentRequestee)
        };
    }
}
