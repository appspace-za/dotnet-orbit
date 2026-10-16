using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Tasks;

public class MyModel(TaskService tasks, TaskStructureService structure, SprintService sprints, IActorProvider actors) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public bool IncludeClosed { get; set; }
    public Actor Actor { get; private set; } = null!;
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];
    public IReadOnlyDictionary<Guid, Orbit.Application.Models.WaitingSummary> Waiting { get; private set; } = new Dictionary<Guid, Orbit.Application.Models.WaitingSummary>();
    /// <summary>The sprints the inline sprint control offers (§6.3): the planned and active ones.</summary>
    public IReadOnlyList<Sprint> OpenSprints { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Actor = await actors.GetAsync(ct);
        Tasks = await tasks.GetMyTasksAsync(openOnly: !IncludeClosed, ct);
        Waiting = await structure.GetWaitingAsync(Tasks, ct);
        OpenSprints = await sprints.ListOpenAsync(ct);
    }
}
