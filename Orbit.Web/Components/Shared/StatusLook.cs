using MudBlazor;
using Orbit.Data.Entities;

namespace Orbit.Components.Shared;

/// <summary>Chip colours for the statuses outside a task's own (<see cref="Orbit.Components.Tasks.TaskLook"/>), as in <see cref="Orbit.Helpers.Ui"/>.</summary>
public static class StatusLook
{
    /// <summary>An asset's status (§6.19).</summary>
    public static Color Asset(AssetStatus s) => s switch
    {
        AssetStatus.Active => Color.Success,
        AssetStatus.Damaged => Color.Warning,
        AssetStatus.Lost => Color.Error,
        AssetStatus.Disposed => Color.Dark,
        _ => Color.Default
    };

    /// <summary>A sprint's status (§6.3).</summary>
    public static Color Sprint(SprintStatus s) => s switch
    {
        SprintStatus.Active => Color.Success,
        SprintStatus.Completed => Color.Dark,
        _ => Color.Default
    };

    /// <summary>A project's status (§6.1).</summary>
    public static Color Project(ProjectStatus s) => s switch
    {
        ProjectStatus.Active => Color.Success,
        ProjectStatus.OnHold => Color.Warning,
        ProjectStatus.Completed => Color.Primary,
        _ => Color.Default
    };

    /// <summary>A project buffer's status (§6.17): green, amber, red, or grey when there's no target date to protect.</summary>
    public static Color Buffer(BufferStatus s) => s switch
    {
        BufferStatus.Green => Color.Success,
        BufferStatus.Amber => Color.Warning,
        BufferStatus.Red => Color.Error,
        _ => Color.Default
    };

    /// <summary>A request's status (§6.20).</summary>
    public static Color Request(RequestStatus s) => s switch
    {
        RequestStatus.InProgress => Color.Primary,
        RequestStatus.Completed => Color.Success,
        RequestStatus.Declined => Color.Error,
        RequestStatus.Cancelled => Color.Secondary,
        _ => Color.Default
    };

    /// <summary>A request step's status (§6.20).</summary>
    public static Color RequestStep(RequestStepStatus s) => s switch
    {
        RequestStepStatus.Ready => Color.Primary,
        RequestStepStatus.Completed => Color.Success,
        RequestStepStatus.Declined or RequestStepStatus.Failed => Color.Error,
        RequestStepStatus.Skipped or RequestStepStatus.Cancelled => Color.Secondary,
        _ => Color.Default
    };

    /// <summary>An approver's decision on a request step (§6.20).</summary>
    public static Color Approval(ApprovalDecision d) => d switch
    {
        ApprovalDecision.Approved => Color.Success,
        ApprovalDecision.Declined => Color.Error,
        _ => Color.Default
    };

    /// <summary>A Nagios problem state (§6.21), in Nagios' own colours: red for down and critical, amber for warning.</summary>
    public static Color Nagios(NagiosProblemState s) => s switch
    {
        NagiosProblemState.Down or NagiosProblemState.Critical => Color.Error,
        NagiosProblemState.Warning => Color.Warning,
        _ => Color.Default
    };
}
