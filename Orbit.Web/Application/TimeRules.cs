using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>Time tracking rules (spec §6.10) that need no database.</summary>
public static class TimeRules
{
    /// <summary>
    /// Whether setting the task Done should first ask the actor to confirm: they are one of its assignees, may log their own time
    /// on it, and have neither logged any nor a clock running on it. A prompt in the web UI only, never a block (§13 item 63).
    /// </summary>
    public static bool AskBeforeDoneWithoutTime(Actor actor, TaskItem task, bool hasLoggedTime, bool clockRunning) =>
        actor.UserId is Guid me && task.IsAssignedTo(me) && task.Status != TaskItemStatus.Done
        && AccessPolicy.CanLogTimeFor(actor, task, me) && !hasLoggedTime && !clockRunning;

    // --- The task clock (§6.10, §13 item 64) ---

    /// <summary>How often a task page with a running clock checks in with the server.</summary>
    public const int ClockHeartbeatSeconds = 60;

    /// <summary>
    /// A clock not heard from for this long has lost its page (closed without the beacon, crashed, asleep, frozen by the
    /// browser). Several missed heartbeats, so a background tab whose timers the browser slows down isn't caught.
    /// </summary>
    public static readonly TimeSpan ClockStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>The longest a single entry, and so a single clock run, can log.</summary>
    public const int MaxEntryMinutes = 24 * 60;

    public static bool IsClockStale(DateTime lastSeenAt, DateTime now) => now - lastSeenAt > ClockStaleAfter;

    /// <summary>Where a clock's run ends: now, or at its last heartbeat when its page was lost, so the time since isn't logged.</summary>
    public static DateTime ClockStoppedAt(DateTime lastSeenAt, DateTime now) => IsClockStale(lastSeenAt, now) ? lastSeenAt : now;

    /// <summary>The minutes a clock run logs: rounded to the nearest minute (so under 30 seconds is 0, nothing logged), at most 24 hours.</summary>
    public static int ClockMinutes(DateTime startedAt, DateTime stoppedAt) =>
        Math.Clamp((int)Math.Round((stoppedAt - startedAt).TotalMinutes, MidpointRounding.AwayFromZero), 0, MaxEntryMinutes);

    // --- The estimate (§6.10) ---

    /// <summary>The largest estimate a task can carry: a year.</summary>
    public const int MaxEstimateMinutes = 525_600;

    /// <summary>The estimate to store: none for blank or 0; refused when negative or over a year.</summary>
    public static int? CleanEstimate(int? minutes)
    {
        if (minutes is null or 0) return null;
        if (minutes < 0) throw new ValidationException("The estimate can't be negative.");
        if (minutes > MaxEstimateMinutes) throw new ValidationException("The estimate must be at most a year (525,600 minutes).");
        return minutes;
    }

    /// <summary>
    /// Reads the task tables' estimate box (§6.2): whole minutes, as on the edit form. Blank or 0 is no estimate. Anything
    /// else - hours, a fraction, a sign - is refused. The range is <see cref="CleanEstimate"/>'s to check.
    /// </summary>
    public static bool TryParseEstimate(string? text, out int? minutes)
    {
        minutes = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;
        minutes = value == 0 ? null : value;
        return true;
    }
}
