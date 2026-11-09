using Orbit.Application;
using Orbit.Data.Entities;
using static Orbit.Data.Entities.TaskItemStatus;

namespace Orbit.Tests.Tasks;

/// <summary>
/// The summary line under a parent on the Tasks list and the project page (spec §6.15, decision 145): its branch, the status,
/// priority and sprint chips, the due range, the estimate total and the people, all from the subtasks the page may show.
/// </summary>
public class BranchSummaryRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 9);

    private static Guid G() => Guid.NewGuid();

    private static TaskItem T(TaskItemStatus s = Todo, TaskPriority p = TaskPriority.Medium, DateOnly? due = null, int? est = null,
        Sprint? sprint = null, bool backlog = false, IEnumerable<ApplicationUser>? people = null) =>
        new()
        {
            Status = s, Priority = p, DueDate = due, EstimateMinutes = est, Sprint = sprint, SprintId = sprint?.Id,
            InBacklog = backlog, Assignments = Assigned.ToPeople([.. people ?? []])
        };

    private static BranchSummary Summ(IReadOnlyCollection<TaskItem> branch) => BranchSummaryRules.Summarise(T(), branch, Today)!;

    /// <summary>TREE-010: one status chip per status present, with its count, over every subtask, in workflow order.</summary>
    [Fact]
    public void Status_chips_count_every_subtask_in_workflow_order()
    {
        var s = Summ([T(Done), T(InProgress), T(Done), T(Cancelled), T(Todo)]);
        Assert.Equal([new StatusCount(Todo, 1), new StatusCount(InProgress, 1), new StatusCount(Done, 2), new StatusCount(Cancelled, 1)], s.Statuses);
        Assert.Equal(5, s.SubtaskCount);
    }

    /// <summary>TREE-010: the branch reaches every level, passing through a task the viewer can't count.</summary>
    [Fact]
    public void The_branch_reaches_every_level_through_tasks_it_cannot_count()
    {
        var a = T(); var b = T(); var c = T(); var d = T();
        b.ParentTaskId = a.Id; c.ParentTaskId = b.Id; d.ParentTaskId = a.Id;
        var idsByParent = new[] { a, b, c, d }.ToLookup(t => t.ParentTaskId, t => t.Id);
        var countable = new[] { a, c, d }.ToDictionary(t => t.Id);
        Assert.Equal(new[] { c.Id, d.Id }.Order(), BranchSummaryRules.BranchOf(a.Id, idsByParent, countable).Select(t => t.Id).Order());
    }

    /// <summary>TREE-010: a loop in the ids doesn't hang, and the parent never counts in its own branch.</summary>
    [Fact]
    public void A_loop_ends_and_leaves_out_the_parent()
    {
        var a = T(); var b = T();
        a.ParentTaskId = b.Id; b.ParentTaskId = a.Id;
        var idsByParent = new[] { a, b }.ToLookup(t => t.ParentTaskId, t => t.Id);
        Assert.Equal([b.Id], BranchSummaryRules.BranchOf(a.Id, idsByParent, new[] { a, b }.ToDictionary(t => t.Id)).Select(t => t.Id));
    }

    /// <summary>TREE-011: priority chips are the open subtasks' priorities, each once, highest first.</summary>
    [Fact]
    public void Priority_chips_are_the_open_subtasks_distinct_highest_first()
    {
        var s = Summ([T(p: TaskPriority.Low), T(Done, TaskPriority.Critical), T(p: TaskPriority.High), T(p: TaskPriority.Low), T(Cancelled, TaskPriority.Critical)]);
        Assert.Equal([TaskPriority.High, TaskPriority.Low], s.Priorities);
    }

    /// <summary>TREE-012: sprint chips are the open subtasks' sprints by start date, then the Backlog and Not Planned.</summary>
    [Fact]
    public void Sprint_chips_follow_sprint_dates_then_backlog_and_not_planned()
    {
        var s10 = new Sprint { Name = "Sprint 10", StartDate = new(2026, 10, 5) };
        var s12 = new Sprint { Name = "Sprint 12", StartDate = new(2026, 11, 2) };
        var s14 = new Sprint { Name = "Sprint 14", StartDate = new(2026, 11, 30) };
        var s = Summ([T(sprint: s14), T(sprint: s12), T(sprint: s14), T(backlog: true), T(), T(Done, sprint: s10)]);
        Assert.Equal(["Sprint 12", "Sprint 14"], s.Sprints.Select(x => x.Name));
        Assert.True(s.OnBacklog);
        Assert.True(s.NotPlanned);
        var planned = Summ([T(sprint: s12)]);
        Assert.False(planned.OnBacklog);
        Assert.False(planned.NotPlanned);
    }

    /// <summary>TREE-013: the due range runs from the open subtasks' earliest due date to their latest; overdue when the earliest is past.</summary>
    [Fact]
    public void Due_range_comes_from_open_subtasks()
    {
        var s = Summ([T(due: new(2026, 11, 20)), T(due: new(2026, 11, 12)), T(), T(Done, due: new(2026, 10, 1))]);
        Assert.Equal(new DateOnly(2026, 11, 12), s.DueFrom);
        Assert.Equal(new DateOnly(2026, 11, 20), s.DueTo);
        Assert.False(s.Overdue);
        var late = Summ([T(due: new(2026, 11, 8)), T(due: new(2026, 11, 8))]);
        Assert.Equal(late.DueFrom, late.DueTo);
        Assert.True(late.Overdue);
        var none = Summ([T(Done, due: new(2026, 11, 1))]);
        Assert.Null(none.DueFrom);
        Assert.False(none.Overdue);
    }

    /// <summary>TREE-014: the estimate total is the parent's own plus every subtask's but the cancelled ones; none when no subtask has one.</summary>
    [Fact]
    public void Estimate_total_counts_done_work_and_leaves_out_cancelled()
    {
        var s = BranchSummaryRules.Summarise(T(est: 120), [T(est: 240), T(Done, est: 480), T(Cancelled, est: 600), T()], Today)!;
        Assert.Equal(840, s.EstimateTotal);
        Assert.Equal(120, s.OwnEstimate);
        Assert.Equal(720, s.SubtasksEstimate);
        Assert.Null(BranchSummaryRules.Summarise(T(est: 120), [T(), T(Cancelled, est: 60)], Today)!.EstimateTotal);
        Assert.Equal(30, BranchSummaryRules.Summarise(T(), [T(est: 30)], Today)!.EstimateTotal);
    }

    /// <summary>TREE-015: the people are those assigned to open subtasks, each once, by name.</summary>
    [Fact]
    public void Assignees_are_the_distinct_people_on_open_subtasks()
    {
        var ann = Assigned.Person(G(), "Ann"); var bob = Assigned.Person(G(), "Bob"); var cy = Assigned.Person(G(), "Cy");
        var s = Summ([T(people: [bob, ann]), T(people: [ann]), T(Done, people: [cy])]);
        Assert.Equal(["Ann", "Bob"], s.Assignees.Select(u => u.DisplayName));
    }

    /// <summary>TREE-016: no subtasks to count means no summary; the open flag says whether any counted subtask is open.</summary>
    [Fact]
    public void No_subtasks_means_no_summary_and_the_open_flag_is_right()
    {
        Assert.Null(BranchSummaryRules.Summarise(T(), [], Today));
        Assert.True(Summ([T(Waiting), T(Done)]).HasOpenSubtasks);
        Assert.False(Summ([T(Done), T(Cancelled)]).HasOpenSubtasks);
    }
}
