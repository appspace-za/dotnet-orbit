using Orbit.Data.Entities;
using Orbit.Helpers;

namespace Orbit.Application;

/// <summary>
/// Where a task sits for sprint planning (spec §6.3): in a sprint; on the <b>Backlog</b>, the pool discussed and planned into a
/// sprint at sprint planning (typically project work); or <b>Not Planned</b>, work that comes up during a sprint without being
/// planned (typically support). Stored as <see cref="TaskItem.SprintId"/> and <see cref="TaskItem.InBacklog"/>; a task in a
/// sprint never carries the backlog flag. The default value is Not Planned, as a new row's flag defaults to false.
/// </summary>
public readonly record struct SprintPlacement
{
    /// <summary>The address and MCP spelling of the Backlog. Not Planned is <see cref="FilterChoice.None"/>; a sprint is its id.</summary>
    public const string BacklogValue = "backlog";
    public const string NotPlannedValue = FilterChoice.None;
    public const string BacklogLabel = "Backlog";
    public const string NotPlannedLabel = "Not Planned";

    private SprintPlacement(Guid? sprintId, bool backlog)
    {
        SprintId = sprintId;
        IsBacklog = backlog;
    }

    /// <summary>The sprint the task is planned into; null on the Backlog and Not Planned.</summary>
    public Guid? SprintId { get; }
    public bool IsBacklog { get; }
    public bool IsNotPlanned => SprintId is null && !IsBacklog;
    public bool IsSprint => SprintId is not null;

    public static SprintPlacement NotPlanned => default;
    public static SprintPlacement Backlog => new(null, true);

    /// <summary>A sprint's placement. An empty id is refused as a <see cref="ValidationException"/>, so a page or MCP shows the message.</summary>
    public static SprintPlacement In(Guid sprintId) =>
        sprintId == Guid.Empty ? throw new ValidationException("A sprint id can't be empty.") : new(sprintId, false);

    /// <summary>SPR-001: the task's placement. A sprint wins over the flag.</summary>
    public static SprintPlacement Of(TaskItem task) => Of(task.SprintId, task.InBacklog);

    public static SprintPlacement Of(Guid? sprintId, bool backlog) =>
        sprintId is Guid id ? In(id) : backlog ? Backlog : NotPlanned;

    /// <summary>SPR-003: a new task with a project starts on the Backlog; a standalone one starts Not Planned (§13 item 116).</summary>
    public static SprintPlacement DefaultFor(Guid? projectId) => projectId is null ? NotPlanned : Backlog;

    /// <summary>
    /// SPR-003: the placement a save asks for (<see cref="Models.TaskInput"/>): a sprint, else the backlog flag given, else
    /// <paramref name="otherwise"/> - the default on create, the task's own flag on update.
    /// </summary>
    public static SprintPlacement Choose(Guid? sprintId, bool? backlog, SprintPlacement otherwise) =>
        sprintId is Guid id ? In(id) : backlog is bool b ? Of(null, b) : otherwise;

    /// <summary>
    /// SPR-007: where a save that takes the task out of its sprint without naming a place leaves it - the Backlog, as completing
    /// a sprint does, since planned work goes back to be planned again (§13 item 117). The Backlog and Not Planned stay as they are.
    /// </summary>
    public SprintPlacement WithoutSprint => IsSprint ? Backlog : this;

    /// <summary>SPR-002: writes both fields, so moving into a sprint clears the backlog flag and leaving one clears the sprint.</summary>
    public void ApplyTo(TaskItem task)
    {
        task.SprintId = SprintId;
        task.InBacklog = IsBacklog;
    }

    /// <summary>SPR-005: the spelling <see cref="TryParse"/> reads back: a sprint's id, "backlog" or "none".</summary>
    public string Value => SprintId?.ToString() ?? (IsBacklog ? BacklogValue : NotPlannedValue);

    /// <summary>
    /// SPR-004: reads an address or MCP value - "none" for Not Planned, "backlog" for the Backlog, or a sprint's id - ignoring case
    /// and surrounding spaces. Anything else, blank included, isn't a placement.
    /// </summary>
    public static bool TryParse(string? value, out SprintPlacement placement)
    {
        var text = value?.Trim();
        placement = default;
        if (string.IsNullOrEmpty(text)) return false;
        if (string.Equals(text, NotPlannedValue, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, BacklogValue, StringComparison.OrdinalIgnoreCase))
        {
            placement = Backlog;
            return true;
        }
        if (!Guid.TryParse(text, out var id) || id == Guid.Empty) return false;
        placement = In(id);
        return true;
    }

    /// <summary>SPR-008: <see cref="TryParse"/>'s placement, or null for a blank or unreadable value (as <see cref="FilterChoice.Id"/> reads an id).</summary>
    public static SprintPlacement? Read(string? value) => TryParse(value, out var placement) ? placement : null;

    /// <summary>SPR-006: "Not Planned", "Backlog", or the sprint's name when given.</summary>
    public string Label(string? sprintName = null) =>
        IsBacklog ? BacklogLabel : SprintId is null ? NotPlannedLabel : sprintName ?? string.Empty;
}
