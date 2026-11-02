using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>
/// What <see cref="TaskTreeRules.ListOrder"/> needs of a task to order siblings as the Tasks list does (design spec
/// 2026-11-02 §6; orbit-spec §6.15). <see cref="IsMatch"/> is false for a row shown only as context above a match.
/// </summary>
public sealed record TaskTreeKey(Guid Id, Guid? ParentTaskId, bool IsMatch, bool HasSubtasks, TaskItemStatus Status,
    DateOnly? DueDate, TaskPriority Priority, DateTime CreatedAt);
