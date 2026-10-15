using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Pages.Tasks;

/// <summary>Endpoint behind the inline status control: the POST that changes the status, and the check site.js makes before Done.</summary>
public class StatusModel(TaskService tasks, TimeEntryService time) : OrbitPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Tasks/Index");

    /// <summary>
    /// Asked by site.js before a status control sets the task Done: the confirmation to show, or null (§6.10), and the task's
    /// solution notes, kept from an earlier close, to start the solution-notes dialog with (§6.2.4).
    /// </summary>
    public async Task<IActionResult> OnGetDoneCheckAsync(Guid id, CancellationToken ct)
    {
        var warning = await time.AskBeforeDoneWithoutTimeAsync(id, ct)
            ? "You haven't logged any time on this task. Mark it Done anyway?"
            : null;
        var task = await tasks.GetAsync(id, ct);
        return new JsonResult(new { warning, solutionNotes = task.SolutionNotes });
    }

    /// <summary>The status control's change. <paramref name="solutionNotes"/> comes from its dialog when it sets the task Done (§6.2.4).</summary>
    public async Task<IActionResult> OnPostAsync(Guid id, TaskItemStatus status, string? solutionNotes, string? returnUrl, CancellationToken ct)
    {
        try
        {
            var task = await tasks.ChangeStatusAsync(id, status, solutionNotes, ct);
            Success($"\"{Ui.Truncate(task.Title, 40)}\" is now {status.Label()}.");
        }
        catch (ValidationException ex)
        {
            Error(ex.Message);
        }
        return LocalRedirect(SafeReturnUrl(returnUrl, "/Tasks"));
    }
}
