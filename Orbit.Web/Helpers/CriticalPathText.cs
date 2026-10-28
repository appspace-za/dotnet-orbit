using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Helpers;

/// <summary>What a run of the critical path analysis (spec §6.17) says when it finishes, on the project page and the Gantt view.</summary>
public static class CriticalPathText
{
    public static string Describe(CriticalPathResult r)
    {
        var s = r.Schedule;
        var buffer = s.BufferStatus == BufferStatus.NotAvailable ? "project buffer not available (no target date)"
            : s.BufferConsumptionPercent is int pct ? $"buffer {s.BufferStatus.Label().ToUpperInvariant()} ({pct}% consumed, {s.BufferRemainingDays} working day(s) remaining)"
            : $"buffer {s.BufferStatus.Label().ToUpperInvariant()}";
        return $"Critical path analysis complete: {r.CriticalTaskCount} critical task(s) on {r.CriticalPaths.Count} path(s), " +
               $"planned completion {s.PlannedCompletion:d MMM yyyy}, {buffer}, {r.WarningCount} warning(s).";
    }

    /// <summary>Why a run was blocked: it stores nothing and says so.</summary>
    public static string Blocked(CriticalPathResult r) => "Analysis not run. " + string.Join(" ", r.Errors.Select(e => e.Message));
}
