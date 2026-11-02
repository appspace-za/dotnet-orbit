using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The timestamps a status change stamps on a task (spec §6.2): <c>FirstRespondedAt</c> the first time it leaves Todo, and
/// <c>CompletedAt</c> set on Done and cleared on any other status. Shared by a manual status change and a parent's roll-up (§3.2).
/// </summary>
public static class TaskStatusStamps
{
    public static void Apply(TaskItem task, TaskItemStatus status, DateTime now)
    {
        if (task.Status == TaskItemStatus.Todo && status != TaskItemStatus.Todo && task.FirstRespondedAt is null)
            task.FirstRespondedAt = now;
        task.CompletedAt = status == TaskItemStatus.Done ? now : null;
        task.Status = status;
    }
}
