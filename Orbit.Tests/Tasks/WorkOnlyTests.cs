using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Tasks;

/// <summary>SUB-019: counts and estimate sums leave out a task that has subtasks, as it is only the container of work its subtasks do.</summary>
public class WorkOnlyTests
{
    [Fact]
    public void WorkOnly_leaves_parents_out()
    {
        TaskItem[] tasks = [new() { Title = "parent", HasSubtasks = true }, new() { Title = "work" }];

        Assert.Equal(["work"], tasks.AsQueryable().WorkOnly().Select(t => t.Title).ToList());
    }
}
