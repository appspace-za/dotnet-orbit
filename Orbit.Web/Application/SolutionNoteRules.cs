using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// Solution notes (spec §6.2.4): how a task was resolved, as distinct from its comment thread. Setting a task <c>Done</c> needs
/// them; <c>Cancelled</c> may carry them (a reason) and the open statuses never need them. Reopening a task keeps them.
/// </summary>
public static class SolutionNoteRules
{
    public const string RequiredMessage = "Solution notes are required to mark a task Done: say how it was resolved.";
    public const string CantClearMessage = "A Done task's solution notes can't be cleared.";

    /// <summary>
    /// SOL-001 to SOL-004: refuses a save that leaves the task Done without notes - moving it to Done, or clearing the notes of a
    /// task that is Done. A task that was already Done with none (closed before §6.2.4) may still be saved without them.
    /// </summary>
    public static void Require(TaskItemStatus from, TaskItemStatus to, string? before, string? after)
    {
        if (to != TaskItemStatus.Done || !string.IsNullOrWhiteSpace(after)) return;
        if (from != TaskItemStatus.Done) throw new ValidationException(RequiredMessage);
        if (!string.IsNullOrWhiteSpace(before)) throw new ValidationException(CantClearMessage);
    }
}
