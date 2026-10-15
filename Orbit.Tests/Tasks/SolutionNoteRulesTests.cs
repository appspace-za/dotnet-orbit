using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Tasks;

/// <summary>Solution notes: how a task was resolved, required to set it Done (spec §6.2.4, §13 item 86).</summary>
public class SolutionNoteRulesTests
{
    private static readonly TaskItemStatus[] Open =
        [TaskItemStatus.Todo, TaskItemStatus.InProgress, TaskItemStatus.Waiting, TaskItemStatus.Blocked];

    /// <summary>SOL-001: moving to Done without notes is refused, from every open status and from Cancelled; blank or whitespace counts as none.</summary>
    [Fact]
    public void Done_needs_notes()
    {
        foreach (var from in Open.Append(TaskItemStatus.Cancelled))
        {
            foreach (var none in new[] { null, "", "  \n " })
            {
                var refused = Assert.Throws<ValidationException>(() => SolutionNoteRules.Require(from, TaskItemStatus.Done, null, none));
                Assert.Equal(SolutionNoteRules.RequiredMessage, refused.Message);
            }
            SolutionNoteRules.Require(from, TaskItemStatus.Done, null, "Replaced the lamp.");
        }
        // Notes kept from an earlier close don't count unless they are still in the save.
        Assert.Throws<ValidationException>(() => SolutionNoteRules.Require(TaskItemStatus.InProgress, TaskItemStatus.Done, "Old notes", null));
        SolutionNoteRules.Require(TaskItemStatus.InProgress, TaskItemStatus.Done, "Old notes", "Old notes");
    }

    /// <summary>SOL-002: Cancelled and the open statuses never need notes, and may drop them.</summary>
    [Fact]
    public void Other_statuses_never_need_notes()
    {
        foreach (var to in Open.Append(TaskItemStatus.Cancelled))
        {
            SolutionNoteRules.Require(TaskItemStatus.Todo, to, null, null);
            SolutionNoteRules.Require(TaskItemStatus.Done, to, "Replaced the lamp.", null);
            SolutionNoteRules.Require(TaskItemStatus.Cancelled, to, "Duplicate of T-26-00010.", "");
        }
    }

    /// <summary>SOL-003: a Done task's notes can be changed, but not cleared while it stays Done.</summary>
    [Fact]
    public void A_done_tasks_notes_can_change_but_not_be_cleared()
    {
        SolutionNoteRules.Require(TaskItemStatus.Done, TaskItemStatus.Done, "Replaced the lamp.", "Replaced the lamp and the filter.");
        foreach (var none in new[] { null, "", " " })
        {
            var refused = Assert.Throws<ValidationException>(() =>
                SolutionNoteRules.Require(TaskItemStatus.Done, TaskItemStatus.Done, "Replaced the lamp.", none));
            Assert.Equal(SolutionNoteRules.CantClearMessage, refused.Message);
        }
    }

    /// <summary>SOL-004: a task that was Done before solution notes existed, with none, can still be edited without adding them.</summary>
    [Fact]
    public void A_task_closed_before_the_rule_stays_editable()
    {
        SolutionNoteRules.Require(TaskItemStatus.Done, TaskItemStatus.Done, null, null);
        SolutionNoteRules.Require(TaskItemStatus.Done, TaskItemStatus.Done, " ", "");
        SolutionNoteRules.Require(TaskItemStatus.Done, TaskItemStatus.Done, null, "Found out afterwards: a firmware update.");
    }
}
