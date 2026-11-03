using Orbit.Application;
using Orbit.Components.Pages.Tasks;
using Orbit.Data.Entities;
using static Orbit.Data.Entities.TaskPriority;

namespace Orbit.Tests.Tasks;

/// <summary>
/// SUB-023: "Add subtask" on a task without subtasks starts the new form from the parent's assignees, priority, due date, estimate
/// and sprint (a completed sprint is not carried); on a task that already has subtasks it starts from nothing.
/// </summary>
public class TaskFormPrefillTests
{
    [Fact]
    public void Prefills_from_a_task_without_subtasks_and_not_from_a_parent()
    {
        var alice = Guid.NewGuid();
        var sprint = new Sprint { Id = Guid.NewGuid(), Status = SprintStatus.Active };
        var parent = new TaskItem
        {
            Priority = High, DueDate = new(2026, 11, 10), EstimateMinutes = 480, SprintId = sprint.Id, Sprint = sprint,
            Assignments = Assigned.To(alice)
        };
        var form = new TaskForm { Sprint = SprintPlacement.Backlog };
        form.PrefillFromParent(parent);
        Assert.Equal((High, (DateOnly?)new DateOnly(2026, 11, 10), (int?)480, SprintPlacement.In(sprint.Id)),
            (form.Priority, form.DueDate, form.EstimateMinutes, form.Sprint));
        Assert.Equal([alice], form.AssigneeIds);

        sprint.Status = SprintStatus.Completed;
        var later = new TaskForm { Sprint = SprintPlacement.Backlog };
        later.PrefillFromParent(parent);
        Assert.Equal(SprintPlacement.Backlog, later.Sprint);

        parent.HasSubtasks = true;
        var none = new TaskForm();
        none.PrefillFromParent(parent);
        Assert.Equal((Medium, (DateOnly?)null), (none.Priority, none.DueDate));
    }

    /// <summary>SUB-023: the inherited sprint beats the project default, an explicit ?sprintId= beats both, and none inherited falls back.</summary>
    [Fact]
    public void A_new_subtask_keeps_the_inherited_sprint()
    {
        var project = Guid.NewGuid();
        var sprint = new Sprint { Id = Guid.NewGuid(), Status = SprintStatus.Active };
        var parent = new TaskItem { SprintId = sprint.Id, Sprint = sprint, Assignments = Assigned.To() };
        var inherited = TaskForm.InheritedSprint(parent);
        Assert.Equal(SprintPlacement.In(sprint.Id), TaskForm.SprintForNew(null, inherited, project));
        Assert.Equal(SprintPlacement.Backlog, TaskForm.SprintForNew(SprintPlacement.Backlog, inherited, project));
        sprint.Status = SprintStatus.Completed;
        Assert.Null(TaskForm.InheritedSprint(parent));
        Assert.Equal(SprintPlacement.DefaultFor(project), TaskForm.SprintForNew(null, TaskForm.InheritedSprint(parent), project));
        parent.HasSubtasks = true;
        Assert.Null(TaskForm.InheritedSprint(parent));
    }
}
