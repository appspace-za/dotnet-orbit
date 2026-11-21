using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>
/// The estimate comparison and Estimate accuracy (spec §12). Time by person's own tests (RPT-001, RPT-003, RPT-008) were retired
/// with it: Time spent's RPT-044 to RPT-048 cover the same ground (decision 149).
/// </summary>
public class TimeReportRulesTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string> { [Alice] = "Alice", [Bob] = "Bob" };

    private static string Name(Guid? id) => id is Guid g && Names.TryGetValue(g, out var n) ? n : "Unassigned";

    private static int _number;

    private static TaskTimeFacts Task(int? estimate, int total, TaskItemStatus status = TaskItemStatus.InProgress, Guid? assignee = null, Guid[]? assignees = null) =>
        new(Guid.NewGuid(), $"T-26-{Interlocked.Increment(ref _number):00000}", "Task", status, assignees ?? (assignee is Guid a ? [a] : []), estimate, total);

    /// <summary>
    /// RPT-002: estimated against actual covers estimated, non-cancelled tasks, using each task's total to date; a cancelled task is
    /// never counted over, whatever was logged.
    /// </summary>
    [Fact]
    public void Estimate_comparison_uses_estimated_open_and_done_tasks_against_their_totals()
    {
        var over = Task(240, 300);
        var under = Task(120, 60, TaskItemStatus.Done);
        var unestimated = Task(null, 90);
        var cancelled = Task(60, 500, TaskItemStatus.Cancelled);

        var comparison = TimeReportRules.Compare([over, under, unestimated, cancelled]);
        Assert.Equal(new EstimateComparison(2, 360, 360, 1), comparison);
        Assert.Equal(0, comparison.VarianceMinutes);
        Assert.Equal(new EstimateComparison(0, 0, 0, 0), TimeReportRules.Compare([cancelled]));
    }

    /// <summary>
    /// ASGN-008: a completed task with several assignees (§6.2.3) is under each of them with its whole estimate - it is never
    /// split - and counts once in the report's own totals.
    /// </summary>
    [Fact]
    public void Estimate_accuracy_lists_a_shared_task_under_each_assignee_and_counts_it_once()
    {
        var shared = Task(120, 150, TaskItemStatus.Done, assignees: [Alice, Bob]);
        var report = TimeReportRules.EstimateAccuracy([shared, Task(60, 30, TaskItemStatus.Done, Bob)], Name);

        Assert.Equal(2, report.DoneCount);
        Assert.Equal(new EstimateComparison(2, 180, 180, 1), report.Overall);
        Assert.Equal(["Bob", "Alice"], report.Rows.Select(r => r.Name));

        var bob = report.Rows[0];
        Assert.Equal(2, bob.DoneCount);
        Assert.Equal(new EstimateComparison(2, 180, 180, 1), bob.Estimate);
        var alice = report.Rows[1];
        Assert.Equal(1, alice.DoneCount);
        Assert.Equal(new EstimateComparison(1, 120, 150, 1), alice.Estimate);
        Assert.Contains(shared.Id, bob.Tasks.Select(t => t.TaskId));
        Assert.Contains(shared.Id, alice.Tasks.Select(t => t.TaskId));
        Assert.Equal(3, report.Rows.Sum(r => r.DoneCount)); // the rows count it twice; the report once
    }

    /// <summary>RPT-004: estimate accuracy groups completed tasks by assignee (none = Unassigned) with variance and over-count.</summary>
    [Fact]
    public void Estimate_accuracy_groups_by_assignee()
    {
        var report = TimeReportRules.EstimateAccuracy(
            [
                Task(240, 300, TaskItemStatus.Done, Alice),
                Task(120, 60, TaskItemStatus.Done, Alice),
                Task(null, 45, TaskItemStatus.Done, Alice),
                Task(60, 90, TaskItemStatus.Done, Bob),
                Task(30, 0, TaskItemStatus.Done)
            ],
            Name);

        Assert.Equal(5, report.DoneCount);
        Assert.Equal(["Alice", "Bob", "Unassigned"], report.Rows.Select(r => r.Name));

        var alice = report.Rows[0];
        Assert.Equal(3, alice.DoneCount);
        Assert.Equal(new EstimateComparison(2, 360, 360, 1), alice.Estimate);
        Assert.Equal(0, alice.Estimate.VariancePercent);

        var bob = report.Rows[1];
        Assert.Equal(30, bob.Estimate.VarianceMinutes);
        Assert.Equal(50, bob.Estimate.VariancePercent);

        var unassigned = report.Rows[2];
        Assert.Null(unassigned.UserId);
        Assert.Equal(-30, unassigned.Estimate.VarianceMinutes);
        Assert.Equal(-100, unassigned.Estimate.VariancePercent);

        Assert.Equal(new EstimateComparison(4, 450, 450, 2), report.Overall);
    }

    /// <summary>RPT-005: with nothing estimated there is no percentage and no variance.</summary>
    [Fact]
    public void Nothing_estimated_gives_no_percentage()
    {
        var comparison = TimeReportRules.Compare([Task(null, 120)]);
        Assert.Equal(EstimateComparison.None, comparison);
        Assert.Null(comparison.VariancePercent);
        Assert.Equal(0, comparison.VarianceMinutes);

        var accuracy = TimeReportRules.EstimateAccuracy([], Name);
        Assert.Empty(accuracy.Rows);
        Assert.Null(accuracy.Overall.VariancePercent);
    }

    /// <summary>RPT-006: accuracy rows by count, then name; each row's tasks with the largest overrun first and unestimated ones last.</summary>
    [Fact]
    public void Ordering()
    {
        var bigOverrun = Task(60, 180, TaskItemStatus.Done, Bob);
        var smallOverrun = Task(60, 70, TaskItemStatus.Done, Bob);
        var under = Task(60, 30, TaskItemStatus.Done, Bob);
        var none = Task(null, 500, TaskItemStatus.Done, Bob);
        var accuracy = TimeReportRules.EstimateAccuracy(
            [none, under, Task(60, 60, TaskItemStatus.Done, Alice), smallOverrun, bigOverrun], Name);
        Assert.Equal(["Bob", "Alice"], accuracy.Rows.Select(r => r.Name));
        Assert.Equal([bigOverrun.Id, smallOverrun.Id, under.Id, none.Id], accuracy.Rows[0].Tasks.Select(t => t.TaskId));
    }

    /// <summary>RPT-007: a variance reads "+5h 30m", "-4h" or "0m".</summary>
    [Fact]
    public void Signed_variance_format()
    {
        Assert.Equal("+5h 30m", TimeFormat.Signed(330));
        Assert.Equal("-4h", TimeFormat.Signed(-240));
        Assert.Equal("-45m", TimeFormat.Signed(-45));
        Assert.Equal("0m", TimeFormat.Signed(0));
    }

}
