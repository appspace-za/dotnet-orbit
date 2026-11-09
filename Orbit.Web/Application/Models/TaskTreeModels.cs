using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>
/// A row of the task table's tree mode (spec §6.15, decision 145): the task, how deep it sits, whether it is a greyed context row
/// (an ancestor of a match), whether rows sit under it, whether only its number and title may be shown (an ancestor the viewer
/// can't open, on the Tasks list), and its branch summary when it has subtasks the page may show.
/// </summary>
public sealed record TaskTreeEntry(TaskItem Task, int Depth, bool IsContext, bool HasChildren, bool TitleOnly, BranchSummary? Summary);

/// <summary>One page of the Tasks list as a tree (<c>TaskService.ListTreeAsync</c>): its rows, the page of how many, and the matches on every page.</summary>
public sealed record TaskTreePage(IReadOnlyList<TaskTreeEntry> Rows, int Page, int PageCount, int MatchCount)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
}
