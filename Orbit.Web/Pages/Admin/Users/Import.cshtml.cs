using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Users;

/// <summary>
/// Import from directory (spec §6.13): list the directory's users through an agent, tick people, choose a department
/// for each directory department Orbit couldn't link, pick one role, import. Load and Import both post and re-render,
/// like Test connection on the Directory page: the list is never kept between requests. The same list offers the
/// directory's manager to the people already in Orbit whose manager differs (§6.13.1): tick, Update managers.
/// </summary>
// A take-on ticks hundreds of people, each one a form value; the default limit is 1,024 values.
[RequestFormLimits(ValueCountLimit = 20_000)]
public class ImportModel(DirectoryImportService import, DepartmentService departments, RoleService roles) : OrbitPageModel
{
    [BindProperty] public DirectoryImportQuery Query { get; set; } = new();
    [BindProperty] public List<string> Selected { get; set; } = [];
    [BindProperty] public Guid RoleId { get; set; }
    [BindProperty] public List<MappingInput> Map { get; set; } = [];
    /// <summary>Each a <see cref="DirectoryManagerChange.Tick"/>: a person already in Orbit and the manager offered for them.</summary>
    [BindProperty] public List<string> ManagerTicks { get; set; } = [];

    public DirectoryImportSetup Setup { get; private set; } = default!;
    public DirectoryImportPreview? Preview { get; private set; }
    public DirectoryImportOutcome? Outcome { get; private set; }
    public DirectoryManagersOutcome? ManagersOutcome { get; private set; }
    public IReadOnlyList<RolePickerItem> RoleItems { get; private set; } = [];
    public IReadOnlyList<Department> Departments { get; private set; } = [];

    public HashSet<string> SelectedKeys => Selected.ToHashSet(StringComparer.Ordinal);
    public HashSet<string> TickedManagers => ManagerTicks.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The department chosen for an unlinked directory value, kept when the page re-renders after a refusal.</summary>
    public Guid? MappedTo(string key) => Map.FirstOrDefault(m => (m.Key ?? string.Empty) == key)?.DepartmentId;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        Query = Setup.Defaults;
        RoleId = UserForm.DefaultRole(RoleItems);
    }

    public async Task<IActionResult> OnPostLoadAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        Selected = [];
        Map = [];
        ManagerTicks = [];
        await PreviewAsync(ct);
        if (RoleId == Guid.Empty) RoleId = UserForm.DefaultRole(RoleItems);
        return Page();
    }

    // Import and Managers are two buttons of one form, so each posts the other's ticks and choices too and the page
    // comes back with them. The service reads the directory once and hands the list back with its answer, whichever it is.
    public async Task<IActionResult> OnPostManagersAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        try
        {
            ManagersOutcome = await import.UpdateManagersAsync(new DirectoryManagersRequest { Query = Query, Selected = ManagerTicks }, ct);
            ManagerTicks = [];
            Show(ManagersOutcome.Preview);
        }
        catch (DirectoryImportRefusedException ex)
        {
            // Nothing was changed: the list again with every tick, so the admin can fix what was refused.
            ModelState.AddModelError(string.Empty, ex.Message);
            Show(ex.Preview);
        }
        catch (ValidationException ex)
        {
            // The directory couldn't be read, so there is no list to show. It isn't tried a second time.
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        if (RoleId == Guid.Empty) RoleId = UserForm.DefaultRole(RoleItems);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        try
        {
            Outcome = await import.ImportAsync(new DirectoryImportRequest
            {
                Query = Query,
                Selected = Selected,
                RoleId = RoleId,
                Mappings = Map.Select(m => new DirectoryImportMapping(m.Key ?? string.Empty, m.DepartmentId)).ToList()
            }, ct);
            // The department choices and the role stay, for the next people ticked.
            Selected = [];
            Show(Outcome.Preview);
        }
        catch (DirectoryImportRefusedException ex)
        {
            // Nothing was created: the list again with the admin's ticks and choices, so they can fix what was refused.
            ModelState.AddModelError(string.Empty, ex.Message);
            Show(ex.Preview);
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        return Page();
    }

    private async Task PreviewAsync(CancellationToken ct)
    {
        try
        {
            Show(await import.PreviewAsync(Query, ct));
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
    }

    private void Show(DirectoryImportPreview preview)
    {
        Preview = preview;
        Query = preview.Query;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Setup = await import.GetSetupAsync(ct);
        RoleItems = await roles.ListForPickerAsync(ct);
        Departments = await departments.ListAsync(false, ct);
    }
}

public sealed class MappingInput
{
    /// <summary>The directory value's comparison key; empty is "no department in the directory".</summary>
    public string? Key { get; set; }
    public Guid? DepartmentId { get; set; }
}
