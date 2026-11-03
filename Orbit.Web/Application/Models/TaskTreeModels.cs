using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>
/// What <see cref="TaskTreeRules.ListOrder"/> needs of a task to order siblings as the Tasks list does (design spec
/// 2026-11-02 §6; orbit-spec §6.15). <see cref="IsMatch"/> is false for a row shown only as context above a match.
/// </summary>
public sealed record TaskTreeKey(Guid Id, Guid? ParentTaskId, bool IsMatch, bool HasSubtasks, TaskItemStatus Status,
    DateOnly? DueDate, TaskPriority Priority, DateTime CreatedAt);

/// <summary>A task on a page of the tree list (<see cref="IsContext"/>: shown only above a match).</summary>
public sealed record TaskTreeRow(TaskItem Task, int Depth, bool IsContext, bool HasShownChildren);

/// <summary>A page of the tree list; <see cref="Count"/> is the matches without subtasks (design spec 2026-11-02 §6).</summary>
public sealed record TaskTreePage(IReadOnlyList<TaskTreeRow> Rows, int Page, int TotalPages, int Count);
