using Orbit.Application.Services;
using Orbit.Components.Infrastructure;
using Orbit.Data.Entities;

namespace Orbit.Components.Pages.Sprints;

/// <summary>Starting and completing a sprint (§6.3), offered by the Sprints list and a sprint's page alike.</summary>
public abstract class SprintPageBase : OrbitComponentBase
{
    /// <summary>Starts a planned sprint, after asking. Returns true when it started.</summary>
    protected async Task<bool> StartSprintAsync(Sprint sprint)
    {
        if (!await Confirm("Start sprint", "Start this sprint? If another sprint is active it will be completed and its open tasks rolled forward into this one.", "Start"))
            return false;
        Sprint? started = null;
        if (!await Act(async s => started = await s.Get<SprintService>().StartAsync(sprint.Id))) return false;
        Success($"Sprint \"{started!.Name}\" is now active.");
        return true;
    }

    /// <summary>Completes the active sprint, after asking. Returns true when it completed.</summary>
    protected async Task<bool> CompleteSprintAsync(Sprint sprint)
    {
        if (!await Confirm("Complete sprint", "Complete this sprint? Open tasks will return to the backlog.", "Complete"))
            return false;
        Sprint? completed = null;
        if (!await Act(async s => completed = await s.Get<SprintService>().CompleteAsync(sprint.Id))) return false;
        Success($"Sprint \"{completed!.Name}\" completed. Open tasks returned to the backlog.");
        return true;
    }
}
