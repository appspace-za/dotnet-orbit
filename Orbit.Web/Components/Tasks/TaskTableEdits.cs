namespace Orbit.Components.Tasks;

/// <summary>
/// Which live controls a <see cref="TaskTable"/> puts on the rows the viewer may change (spec §6.2 <i>Quick edit</i>). The Tasks
/// list and the project and sprint pages allow them all; the Planning page (§6.3, decision 141) only the assignee and the estimate.
/// <see cref="TaskTable.ReadOnly"/> allows none.
/// </summary>
[Flags]
public enum TaskTableEdits
{
    None = 0,
    Status = 1,
    Assignee = 2,
    DueDate = 4,
    Estimate = 8,
    Sprint = 16,
    /// <summary>The Take button on an open, unassigned task (§6.2).</summary>
    Take = 32,
    /// <summary>The Today tick box (§6.12).</summary>
    Today = 64,
    All = Status | Assignee | DueDate | Estimate | Sprint | Take | Today
}
