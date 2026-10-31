using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// The Tasks list's Due filter (spec §6.2, decision 135): a window of due dates counted from today, so a link to it - the
/// dashboard's Overdue and Due in 7 days figures - means the same tomorrow as it does now, and a remembered filter doesn't
/// freeze yesterday's dates. The dashboard counts its figures through the same <see cref="Within"/>, so each figure matches the
/// list it opens. No database access, so the windows are unit-tested (DSH-010).
/// </summary>
public static class DueWindowRules
{
    /// <summary>The Due soon window: today and the next seven days.</summary>
    public const int SoonDays = 7;

    /// <summary>
    /// The window's due dates, both ends inclusive and null for no bound: Overdue is due before today, Soon is due from today to
    /// <see cref="SoonDays"/> days ahead. Today is in Soon, not Overdue.
    /// </summary>
    public static (DateOnly? After, DateOnly? Before) Range(DueWindow window, DateOnly today) => window switch
    {
        DueWindow.Overdue => (null, today.AddDays(-1)),
        _ => (today, today.AddDays(SoonDays))
    };

    /// <summary>The tasks due in the window (<see cref="Range"/>); a task with no due date is in none.</summary>
    public static IQueryable<TaskItem> Within(IQueryable<TaskItem> q, DueWindow window, DateOnly today)
    {
        var (after, before) = Range(window, today);
        q = q.Where(t => t.DueDate != null);
        if (after is DateOnly first) q = q.Where(t => t.DueDate >= first);
        if (before is DateOnly last) q = q.Where(t => t.DueDate <= last);
        return q;
    }

    /// <summary>The window's name, on the Tasks list's Due filter and the dashboard's figure alike.</summary>
    public static string Label(DueWindow window) => window switch
    {
        DueWindow.Overdue => "Overdue",
        _ => $"Due in {SoonDays} days"
    };
}
