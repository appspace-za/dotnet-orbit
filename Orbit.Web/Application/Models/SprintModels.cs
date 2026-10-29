using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public sealed class SprintInput
{
    public string Name { get; set; } = string.Empty;
    public string? Goal { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}

public sealed record NameCount(string Name, int Count);

public sealed record SprintListItem(Sprint Sprint, int TotalTasks, int OpenTasks, IReadOnlyList<NameCount> ByDepartment)
{
    public int DoneTasks => TotalTasks - OpenTasks;
}

public sealed record SprintBoard(Sprint Sprint, IReadOnlyList<SprintBoardColumn> Columns)
{
    public int Total => Columns.Sum(c => c.Tasks.Count);
}

public sealed record SprintBoardColumn(string Title, TaskItemStatus Status, IReadOnlyList<TaskItem> Tasks);

/// <summary>
/// The details of a sprint's Completed audit row (§6.3): the open tasks it handed on, to the sprint started after it or back to
/// the Backlog. <c>SprintService</c> writes them and the Sprint status report reads them back (§12), so this record is the
/// contract between the two. Serialised with <c>OrbitJson.Options</c> (camelCase, nulls left out) it reads
/// <c>{"rolledForwardTo":"…","rolledForwardTaskIds":[…]}</c> or <c>{"returnedToBacklogTaskIds":[…]}</c>, as rows written
/// before it existed do; renaming a property would strand those rows.
/// </summary>
public sealed record SprintCompletedDetails(
    Guid? RolledForwardTo, IReadOnlyList<Guid>? RolledForwardTaskIds, IReadOnlyList<Guid>? ReturnedToBacklogTaskIds)
{
    public static SprintCompletedDetails RolledForward(Guid to, IEnumerable<Guid> taskIds) => new(to, taskIds.ToArray(), null);
    public static SprintCompletedDetails ReturnedToBacklog(IEnumerable<Guid> taskIds) => new(null, null, taskIds.ToArray());
}
