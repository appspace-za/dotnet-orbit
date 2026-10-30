using Orbit.Data.Entities;

namespace Orbit.Application.Models;

public enum ReportKind
{
    ClosedByPerson,
    CreatedByPerson,
    MeanTimeToRespond,
    MeanTimeToResolve,
    TimeByPerson,
    EstimateAccuracy,
    ProjectStatus,
    AssetStatus,
    SprintStatus
}

/// <summary>
/// A report's filters. <see cref="OwnerId"/> and <see cref="AssigneeId"/> pick projects for the project status report
/// (<see cref="ReportDefinition.ByOwnerAndAssignee"/>) and are ignored by the others.
/// </summary>
public sealed record ReportFilter(
    DateTime FromUtc, DateTime ToUtc, Guid? ProjectId, Guid? DepartmentId, Guid? OwnerId = null, Guid? AssigneeId = null);

public sealed record PersonCountRow(Guid? UserId, string Name, int Count);

/// <summary>
/// A count of tasks by person (§12). <see cref="Total"/> counts each task once, so it is less than the rows' sum when a task
/// several people share (§6.2.3) is under each of them.
/// </summary>
public sealed record PersonCountReport(IReadOnlyList<PersonCountRow> Rows, int Total);

public sealed record PersonAverageRow(Guid? UserId, string Name, int Count, double AverageHours);

public sealed record MeanTimeReport(IReadOnlyList<PersonAverageRow> Rows, int OverallCount, double? OverallAverageHours);

/// <summary>One person's time on one task within a report's date range, summed in SQL (§12 Time by person).</summary>
public sealed record LoggedTime(Guid UserId, Guid TaskId, int Minutes);

/// <summary>What the time reports need to know about a task. <see cref="TotalMinutes"/> is all time logged on it to date, by anyone.</summary>
public sealed record TaskTimeFacts(
    Guid Id, string Number, string Title, TaskItemStatus Status, IReadOnlyList<Guid> AssigneeIds, int? EstimateMinutes, int TotalMinutes);

/// <summary>
/// Estimated against actual effort over a set of tasks. Only tasks with an estimate that aren't Cancelled count
/// (as on the project page, §6.10); <see cref="ActualMinutes"/> is all time logged on those tasks to date.
/// </summary>
public sealed record EstimateComparison(int EstimatedTasks, int EstimatedMinutes, int ActualMinutes, int OverTasks)
{
    public static readonly EstimateComparison None = new(0, 0, 0, 0);

    public int VarianceMinutes => ActualMinutes - EstimatedMinutes;

    /// <summary>Variance as a percentage of the estimate; null when nothing is estimated.</summary>
    public double? VariancePercent => EstimatedMinutes == 0 ? null : VarianceMinutes * 100.0 / EstimatedMinutes;
}

/// <summary>
/// Time by person (§12): who logged how much in the range. The totals count each task once, so a task two people
/// worked on shows under both rows but isn't estimated twice in <see cref="Total"/>.
/// </summary>
public sealed record TimeByPersonReport(
    IReadOnlyList<PersonTimeRow> Rows, int TotalLoggedMinutes, int TotalTasks, int TotalUnestimatedTasks, EstimateComparison Total);

public sealed record PersonTimeRow(
    Guid UserId, string Name, int LoggedMinutes, int TaskCount, int UnestimatedTasks, EstimateComparison Estimate,
    IReadOnlyList<PersonTaskTimeRow> Tasks);

/// <summary>A task one person worked on: <see cref="LoggedMinutes"/> is their time in the range, <see cref="TotalMinutes"/> everyone's to date.</summary>
public sealed record PersonTaskTimeRow(
    Guid TaskId, string Number, string Title, TaskItemStatus Status, int? EstimateMinutes, int LoggedMinutes, int TotalMinutes)
{
    public bool IsCancelled => Status == TaskItemStatus.Cancelled;
    public bool IsOver => !IsCancelled && EstimateMinutes is int e && TotalMinutes > e;
}

/// <summary>
/// Estimate accuracy (§12): tasks completed in the range, grouped by assignee. A task several people share (§6.2.3) is under each
/// of them with its whole estimate, and counts once in <see cref="DoneCount"/> and <see cref="Overall"/>.
/// </summary>
public sealed record EstimateAccuracyReport(
    IReadOnlyList<AssigneeAccuracyRow> Rows, int DoneCount, EstimateComparison Overall);

public sealed record AssigneeAccuracyRow(
    Guid? UserId, string Name, int DoneCount, EstimateComparison Estimate, IReadOnlyList<TaskEstimateRow> Tasks);

/// <summary>A completed task: its estimate against all time logged on it.</summary>
public sealed record TaskEstimateRow(Guid TaskId, string Number, string Title, int? EstimateMinutes, int ActualMinutes)
{
    public int? VarianceMinutes => EstimateMinutes is int e ? ActualMinutes - e : null;
    public bool IsOver => EstimateMinutes is int e && ActualMinutes > e;
}

/// <summary>What the project status report (§12) needs to know about a project. <see cref="Status"/> is its status now.</summary>
public sealed record ProjectFacts(
    Guid Id, string Number, string Name, ProjectStatus Status, string DepartmentName, string OwnerName,
    DateTime CreatedAt, DateOnly? TargetDate);

/// <summary>One change of a project's status, read from its audit trail.</summary>
public sealed record ProjectStatusChange(Guid ProjectId, DateTime At, ProjectStatus From, ProjectStatus To);

/// <summary>A project's status over a report's period, rebuilt from its status changes (§12 Project status).</summary>
public sealed record ProjectStatusHistory(
    ProjectStatus AtStart, ProjectStatus AtEnd, ProjectStatus Current, IReadOnlyList<ProjectStatusChange> Changes)
{
    /// <summary>Active or On Hold at some point in the period.</summary>
    public bool WasOpen => AtStart.IsOpen() || Changes.Any(c => c.To.IsOpen());
}

/// <summary>A task on a project: <see cref="PeriodMinutes"/> is all time logged on it in the range, <c>Task.TotalMinutes</c> all to date.</summary>
public sealed record ProjectTaskFacts(
    TaskTimeFacts Task, Guid ProjectId, DateOnly? DueDate, DateTime CreatedAt, DateTime? CompletedAt, int PeriodMinutes);

/// <summary>One person's time on one project: in the range, and to date.</summary>
public sealed record ProjectPersonTime(Guid ProjectId, Guid UserId, int PeriodMinutes, int TotalMinutes);

/// <summary>
/// Project status (§12): every project in the range - open at some point in it, or with task or time activity in it -
/// closed ones included. <see cref="StatusCounts"/> counts the projects by their status at the end of the range.
/// </summary>
public sealed record ProjectStatusReport(
    IReadOnlyList<ProjectStatusRow> Rows, ProjectStatusTotals Total, IReadOnlyList<ProjectStatusCount> StatusCounts);

/// <summary>A person the project status report's Owner or Assignee filter offers.</summary>
public sealed record ReportPerson(Guid Id, string Name, bool IsActive);

/// <summary>
/// Who the project status report's filters offer (§12 Project status): the owners of the projects the viewer can report on,
/// and the assignees of those projects' tasks that aren't Cancelled.
/// </summary>
public sealed record ProjectReportPeople(IReadOnlyList<ReportPerson> Owners, IReadOnlyList<ReportPerson> Assignees);

/// <summary>How many projects ended the range in a status, and how many of those changed to it during the range.</summary>
public sealed record ProjectStatusCount(ProjectStatus Status, int Count, int ChangedInPeriod);

/// <summary>The task counts of a project (or all of them): now, apart from the two "in period" counts.</summary>
public sealed record ProjectTaskCounts(
    int Total, int Open, int Blocked, int Overdue, int Done, int Cancelled, int CreatedInPeriod, int DoneInPeriod)
{
    public static readonly ProjectTaskCounts None = new(0, 0, 0, 0, 0, 0, 0, 0);

    public int PercentDone => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total);

    public ProjectTaskCounts Plus(ProjectTaskCounts o) => new(Total + o.Total, Open + o.Open, Blocked + o.Blocked,
        Overdue + o.Overdue, Done + o.Done, Cancelled + o.Cancelled, CreatedInPeriod + o.CreatedInPeriod, DoneInPeriod + o.DoneInPeriod);
}

public sealed record ProjectStatusTotals(
    int Projects, ProjectTaskCounts Tasks, int LoggedInPeriodMinutes, int LoggedToDateMinutes, EstimateComparison Estimate);

/// <summary>
/// One project. <see cref="Schedule"/> is its latest critical path analysis (§6.17), only while the project is open;
/// <see cref="IsPastTarget"/> is an open project whose target date has gone by.
/// </summary>
public sealed record ProjectStatusRow(
    ProjectFacts Project, ProjectStatusHistory Status, bool CreatedInPeriod, ProjectTaskCounts Tasks,
    int LoggedInPeriodMinutes, int LoggedToDateMinutes, EstimateComparison Estimate, bool IsPastTarget,
    CriticalPathSummary? Schedule, IReadOnlyList<ProjectPersonRow> People, IReadOnlyList<ProjectTaskRow> TaskRows);

/// <summary>
/// A person on a project: their assigned tasks (open now, done in the range) and the estimates of those that aren't
/// Cancelled, against the time they logged on the project. <see cref="UserId"/> null is the Unassigned row. A task several
/// people share (§6.2.3) is in each one's figures, whole.
/// </summary>
public sealed record ProjectPersonRow(
    Guid? UserId, string Name, int OpenTasks, int DoneInPeriod, int EstimatedTasks, int EstimatedMinutes,
    int LoggedInPeriodMinutes, int LoggedToDateMinutes);

/// <summary>
/// A task in a project's list: <see cref="PeriodMinutes"/> is everyone's time on it in the range, <see cref="TotalMinutes"/> to date.
/// <see cref="Assignee"/> is its assignees' names, or "Unassigned".
/// </summary>
public sealed record ProjectTaskRow(
    Guid TaskId, string Number, string Title, TaskItemStatus Status, string Assignee, DateOnly? DueDate, bool IsOverdue,
    int? EstimateMinutes, int PeriodMinutes, int TotalMinutes, bool CreatedInPeriod, bool DoneInPeriod)
{
    public bool IsCancelled => Status == TaskItemStatus.Cancelled;
    public bool IsOver => !IsCancelled && EstimateMinutes is int e && TotalMinutes > e;
}

/// <summary>
/// What the asset status report (§12) needs to know about an asset. Its type, location and managing department are as they are
/// now; its last check is the latest on or before the report's as-at day (<see cref="AssetReportRules.AsAt"/>).
/// <see cref="HadTaskActivity"/> is a linked task created, completed or logged against in the range.
/// </summary>
public sealed record AssetFacts(
    Guid Id, string? AssetNumber, string Name, AssetStatus Status, DateOnly? DisposedOn, DateTime CreatedAt,
    Guid DepartmentId, string DepartmentName, Guid TypeId, string TypeName, string? TypeCategory, int? CheckIntervalDays,
    Guid? LocationId, string? LocationName, decimal? PurchaseValue,
    DateOnly? LastCheckedOn, AssetCheckOutcome? LastCheckOutcome, int ChecksInPeriod, bool HadTaskActivity);

/// <summary>One change of an asset's status, read from its audit trail; <see cref="At"/> is when it took effect.</summary>
public sealed record AssetStatusChange(Guid AssetId, DateTime At, AssetStatus From, AssetStatus To);

/// <summary>An asset's status over a report's period, rebuilt from its status changes (§12 Asset status).</summary>
public sealed record AssetStatusHistory(
    AssetStatus AtStart, AssetStatus AtEnd, AssetStatus Current, IReadOnlyList<AssetStatusChange> Changes)
{
    /// <summary>Not Disposed at some point in the period.</summary>
    public bool WasInService => AtStart != AssetStatus.Disposed || Changes.Any(c => c.To != AssetStatus.Disposed);
}

/// <summary>A task about an asset: <see cref="PeriodMinutes"/> is everyone's time on it in the range.</summary>
public sealed record AssetTaskFacts(
    Guid Id, string Number, string Title, TaskItemStatus Status, IReadOnlyList<Guid> AssigneeIds, Guid AssetId,
    DateOnly? DueDate, DateTime CreatedAt, DateTime? CompletedAt, int PeriodMinutes);

/// <summary>The linked tasks of some assets: open (and overdue) now; created, done and logged in the range.</summary>
public sealed record AssetTaskCounts(int Open, int Overdue, int Created, int Done, int LoggedMinutes)
{
    public static readonly AssetTaskCounts None = new(0, 0, 0, 0, 0);

    public AssetTaskCounts Plus(AssetTaskCounts o) =>
        new(Open + o.Open, Overdue + o.Overdue, Created + o.Created, Done + o.Done, LoggedMinutes + o.LoggedMinutes);
}

/// <summary>
/// The figures of a group of assets - a type, a location, one within the other, or all of them (§12 Asset status). The statuses
/// are at the end of the range; held means not Disposed then. Values are at cost (<c>PurchaseValue</c>). Checked counts the held
/// assets checked in the range; check overdue and not OK are as at the as-at day.
/// </summary>
public sealed record AssetGroupCounts(
    int Active, int InStorage, int Damaged, int Lost, int Disposed, int Registered,
    decimal HeldValue, int HeldUnvalued, decimal DisposedValue,
    int Checked, int CheckOverdue, int CheckNotOk, AssetTaskCounts Tasks)
{
    public static readonly AssetGroupCounts None = new(0, 0, 0, 0, 0, 0, 0m, 0, 0m, 0, 0, 0, AssetTaskCounts.None);

    public int Total => Active + InStorage + Damaged + Lost + Disposed;
    public int Held => Total - Disposed;

    public int Of(AssetStatus status) => status switch
    {
        AssetStatus.Active => Active,
        AssetStatus.InStorage => InStorage,
        AssetStatus.Damaged => Damaged,
        AssetStatus.Lost => Lost,
        AssetStatus.Disposed => Disposed,
        _ => 0
    };

    public AssetGroupCounts Plus(AssetGroupCounts o) => new(
        Active + o.Active, InStorage + o.InStorage, Damaged + o.Damaged, Lost + o.Lost, Disposed + o.Disposed,
        Registered + o.Registered, HeldValue + o.HeldValue, HeldUnvalued + o.HeldUnvalued, DisposedValue + o.DisposedValue,
        Checked + o.Checked, CheckOverdue + o.CheckOverdue, CheckNotOk + o.CheckNotOk, Tasks.Plus(o.Tasks));
}

/// <summary>
/// A type (with its <see cref="Category"/>) or a location, with its figures and its breakdown by the other: a type's locations, a
/// location's types. <see cref="Id"/> null is "No location".
/// </summary>
public sealed record AssetGroupRow(
    Guid? Id, string Name, string? Category, string DepartmentName, AssetGroupCounts Counts, IReadOnlyList<AssetGroupRow> Breakdown);

/// <summary>How many assets ended the range in a status, and how many of those changed to it during the range.</summary>
public sealed record AssetStatusCount(AssetStatus Status, int Count, int ChangedInPeriod);

/// <summary>An asset with a linked task open now or worked on in the range: its status at the end of the range, and those tasks.</summary>
public sealed record AssetWorkRow(
    Guid AssetId, string? AssetNumber, string Name, string TypeName, string? LocationName, string DepartmentName,
    AssetStatus Status, AssetTaskCounts Tasks, IReadOnlyList<AssetTaskRow> TaskRows);

/// <summary>A task about an asset: <see cref="PeriodMinutes"/> is everyone's time on it in the range.</summary>
public sealed record AssetTaskRow(
    Guid TaskId, string Number, string Title, TaskItemStatus Status, string Assignee, DateOnly? DueDate, bool IsOverdue,
    DateTime? CompletedAt, int PeriodMinutes, bool CreatedInPeriod, bool DoneInPeriod);

/// <summary>
/// Asset status (§12): every asset in the register during the range, disposed ones included, by type (each with its locations)
/// and by location (each with its types). <see cref="StatusCounts"/> counts the assets by their status at the end of the range;
/// <see cref="Assets"/> are the ones with linked-task work; checks are read as at <see cref="AsAt"/>.
/// </summary>
public sealed record AssetStatusReport(
    IReadOnlyList<AssetGroupRow> ByType, IReadOnlyList<AssetGroupRow> ByLocation, AssetGroupCounts Total,
    IReadOnlyList<AssetStatusCount> StatusCounts, IReadOnlyList<AssetWorkRow> Assets, DateOnly AsAt);

/// <summary>
/// The sprint status report's filters (§12 Sprint status). No <see cref="SprintId"/> is the active sprint, or the latest completed
/// one when none is active. The project and assignee pick tasks; a picked task's figures stay whole.
/// </summary>
public sealed record SprintReportFilter(Guid? SprintId, Guid? ProjectId, Guid? DepartmentId, Guid? AssigneeId);

/// <summary>
/// A sprint as the sprint status report reads it. Its window is <see cref="StartDate"/> to <see cref="EndDate"/>, both days
/// included: [<see cref="FromUtc"/>, <see cref="ToUtc"/>) for times - UTC days, as everywhere in Orbit - and
/// [<see cref="StartDate"/>, <see cref="EndDay"/>) for dates.
/// </summary>
public sealed record SprintFacts(
    Guid Id, string Name, string? Goal, DateOnly StartDate, DateOnly EndDate, SprintStatus Status, DateTime? CompletedAt)
{
    /// <summary>The day after the sprint's last day: the end of its window, excluded.</summary>
    public DateOnly EndDay => EndDate.AddDays(1);
    public DateTime FromUtc => DateTime.SpecifyKind(StartDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    public DateTime ToUtc => DateTime.SpecifyKind(EndDay.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}

/// <summary>
/// A sprint's Completed audit row (§6.3): the tasks it still had open, which went on to the sprint started after it
/// (<see cref="RolledForwardTo"/>) or back to the Backlog (null).
/// </summary>
public sealed record SprintCompletion(Guid SprintId, DateTime At, Guid? RolledForwardTo, IReadOnlyList<Guid> OpenTaskIds);

/// <summary>
/// A task the sprint status report may count: planned into the sprint, or Not Planned, on the Backlog or in another sprint with
/// work in its window (<c>SprintReportRules.UnplannedKind</c>). <see cref="SprintMinutes"/> is everyone's time on it dated in the
/// sprint's window; <c>Task.TotalMinutes</c> all to date.
/// </summary>
public sealed record SprintTaskFacts(
    TaskTimeFacts Task, Guid? ProjectId, Guid? SprintId, bool InBacklog, DateTime CreatedAt, DateTime? CompletedAt, int SprintMinutes);

/// <summary>A project with planned work in the sprint (or an unplanned task on it: Not Planned, Backlog or another sprint's).</summary>
public sealed record SprintProjectFacts(Guid Id, string Number, string Name, string DepartmentName);

/// <summary>One person's time on one task: dated in the sprint's window, and to date.</summary>
public sealed record SprintPersonTime(Guid TaskId, Guid UserId, int SprintMinutes, int TotalMinutes);

/// <summary>Everyone's time on one task on one day of the sprint.</summary>
public sealed record SprintDayTime(Guid TaskId, DateOnly Date, int Minutes);

/// <summary>One person's time on one task on one day, any day: the one time query the report runs, split by <c>SprintReportRules.WithTime</c>.</summary>
public sealed record SprintTimeEntry(Guid TaskId, Guid UserId, DateOnly Date, int Minutes);

/// <summary>A change to a task's status, from its audit row (§6.2): what it was and became, and when.</summary>
public sealed record SprintStatusChange(Guid TaskId, DateTime At, TaskItemStatus From, TaskItemStatus To);

/// <summary>Where a task ended the sprint: Done, Left open (to go on to the next sprint or the Backlog), or Cancelled.</summary>
public enum SprintTaskOutcome
{
    Done,
    LeftOpen,
    Cancelled
}

/// <summary>
/// The tasks of a group by outcome. <see cref="CarriedIn"/> came in open from the sprint before. <see cref="LeftOpenRemainingMinutes"/>
/// is what is left of the left-open tasks' estimates (never below nothing per task); <see cref="LeftOpenUnestimated"/> of them have none.
/// <see cref="DoneBeforeSprint"/> of the Done ones were already Done when the sprint started (<see cref="SprintTaskRow.DoneBeforeSprint"/>).
/// </summary>
public sealed record SprintTaskCounts(
    int Total, int Done, int LeftOpen, int Cancelled, int CarriedIn, int LeftOpenRemainingMinutes, int LeftOpenUnestimated,
    int DoneBeforeSprint = 0)
{
    public static readonly SprintTaskCounts None = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Done of all the tasks, cancelled ones included, as the sprint page counts progress.</summary>
    public int PercentDone => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total);

    public SprintTaskCounts Plus(SprintTaskCounts o) => new(Total + o.Total, Done + o.Done, LeftOpen + o.LeftOpen,
        Cancelled + o.Cancelled, CarriedIn + o.CarriedIn, LeftOpenRemainingMinutes + o.LeftOpenRemainingMinutes,
        LeftOpenUnestimated + o.LeftOpenUnestimated, DoneBeforeSprint + o.DoneBeforeSprint);
}

/// <summary>
/// A task in a group's list. <see cref="Status"/> is its status now; <see cref="Outcome"/> where it stood when the sprint ended.
/// <see cref="ProjectLabel"/> names its project on an unplanned task. <see cref="CarriedInFrom"/> is the sprint it came in from
/// open; <see cref="LeftOpenTo"/> where it went when the sprint ended ("Backlog" or a sprint), null while the sprint runs.
/// <see cref="ReopenedSince"/> is a task closed when the sprint ended and open now. <see cref="OtherSprint"/> is the other sprint an
/// unplanned task is in - a later one (<see cref="OtherSprintIsLater"/>), worked on ahead, or an earlier one.
/// <see cref="DoneBeforeSprint"/> is a task that was already Done when the sprint started and hasn't changed since: a planned one
/// stayed in the sprint, as only open tasks roll forward; an unplanned one only had time logged in it. Either way it wasn't done in
/// this sprint, though its outcome is Done.
/// </summary>
public sealed record SprintTaskRow(
    Guid TaskId, string Number, string Title, TaskItemStatus Status, SprintTaskOutcome Outcome, string Assignee, string? ProjectLabel,
    int? EstimateMinutes, int SprintMinutes, int TotalMinutes, string? CarriedInFrom, string? LeftOpenTo, bool ReopenedSince,
    string? OtherSprint = null, bool OtherSprintIsLater = false, bool DoneBeforeSprint = false)
{
    /// <summary>Cancelled when the sprint ended, whatever it is now: the figures leave it out of the estimates and the people's tasks.</summary>
    public bool IsCancelled => Outcome == SprintTaskOutcome.Cancelled;
    public bool IsOver => !IsCancelled && EstimateMinutes is int e && TotalMinutes > e;
}

/// <summary>
/// A person in a group: their assigned tasks that aren't Cancelled by outcome, those tasks' estimates, and the time they logged on
/// the group's tasks. <see cref="UserId"/> null is the Unassigned row. A task several people share is in each one's row, whole.
/// </summary>
public sealed record SprintPersonRow(
    Guid? UserId, string Name, int Tasks, int Done, int LeftOpen, int EstimatedTasks, int EstimatedMinutes, int SprintMinutes,
    int ToDateMinutes);

public enum SprintGroupKind
{
    Project,
    NoProject,
    NotPlanned,
    Backlog,
    OtherSprint
}

/// <summary>
/// The kinds of work in a sprint's report (§12 Sprint status): the planned tasks - a project's or on no project - and the three
/// kinds of unplanned work, each a group of its own (<see cref="SprintGroupRow.Work"/>). The report keeps their figures apart.
/// </summary>
public enum SprintWorkKind
{
    Planned,
    NotPlanned,
    Backlog,
    OtherSprint
}

/// <summary>
/// A project's planned tasks in the sprint, the planned tasks on no project, the Not Planned work, the Backlog tasks worked on
/// during the sprint, or other sprints' tasks worked on during it (§12 Sprint status), with its people and its task list.
/// </summary>
public sealed record SprintGroupRow(
    SprintGroupKind Kind, SprintProjectFacts? Project, SprintTaskCounts Tasks, int SprintMinutes, int ToDateMinutes,
    EstimateComparison Estimate, IReadOnlyList<SprintPersonRow> People, IReadOnlyList<SprintTaskRow> TaskRows)
{
    public const string NoProjectName = "No project", NotPlannedName = "Not Planned", BacklogName = "From the Backlog",
        OtherSprintName = "From another sprint";

    /// <summary>A key that stays the same between runs, for which rows are open on screen.</summary>
    public string Key => Project?.Id.ToString() ?? Kind.ToString();

    /// <summary>The group's kind of work: a project's and the no-project tasks are planned, each other group its own kind.</summary>
    public SprintWorkKind Work => Kind switch
    {
        SprintGroupKind.Project or SprintGroupKind.NoProject => SprintWorkKind.Planned,
        SprintGroupKind.NotPlanned => SprintWorkKind.NotPlanned,
        SprintGroupKind.Backlog => SprintWorkKind.Backlog,
        SprintGroupKind.OtherSprint => SprintWorkKind.OtherSprint,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
    };

    /// <summary>Work outside the sprint's plan: the Not Planned work, and the Backlog and other sprints' tasks worked on anyway.</summary>
    public bool IsUnplanned => Work != SprintWorkKind.Planned;

    public string Name => Kind == SprintGroupKind.Project ? Project?.Name ?? "" : NameOf(Kind);

    /// <summary>The group an unplanned kind of work makes. Planned work makes a group per project, and one for no project.</summary>
    public static SprintGroupKind GroupOf(SprintWorkKind kind) => kind switch
    {
        SprintWorkKind.NotPlanned => SprintGroupKind.NotPlanned,
        SprintWorkKind.Backlog => SprintGroupKind.Backlog,
        SprintWorkKind.OtherSprint => SprintGroupKind.OtherSprint,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Planned work makes a group per project.")
    };

    /// <summary>The name of a group that isn't a project's.</summary>
    public static string NameOf(SprintGroupKind kind) => kind switch
    {
        SprintGroupKind.NoProject => NoProjectName,
        SprintGroupKind.NotPlanned => NotPlannedName,
        SprintGroupKind.Backlog => BacklogName,
        SprintGroupKind.OtherSprint => OtherSprintName,
        _ => ""
    };
}

/// <summary>
/// A person's sprint, planned against unplanned work: their assigned tasks that aren't Cancelled, by kind of work
/// (<see cref="Tasks"/>, their planned ones done and left open apart), and the time they logged in the sprint on each kind
/// (<see cref="Minutes"/>). <see cref="UserId"/> null is the Unassigned row, which has tasks and no time.
/// </summary>
public sealed record SprintImpactRow(
    Guid? UserId, string Name, int PlannedDone, int PlannedLeftOpen, SprintKindCounts Tasks, SprintKindCounts Minutes)
{
    public int PlannedTasks => Tasks.Planned;
    public int PlannedMinutes => Minutes.Planned;
    public int UnplannedMinutes => Minutes.Unplanned;
    public int SprintMinutes => Minutes.Total;

    /// <summary>Unplanned work's share of the time they logged in the sprint; null when they logged none.</summary>
    public int? UnplannedPercent => SprintReportShare.Percent(UnplannedMinutes, SprintMinutes);
}

/// <summary>One kind of work's figures: its tasks by outcome, its time in the sprint and to date, and its estimates.</summary>
public sealed record SprintWorkTotals(SprintTaskCounts Tasks, int SprintMinutes, int ToDateMinutes, EstimateComparison Estimate)
{
    public static readonly SprintWorkTotals None = new(SprintTaskCounts.None, 0, 0, EstimateComparison.None);
}

/// <summary>
/// The sprint's figures, each kind of work apart: planned, Not Planned, from the Backlog and from another sprint. Each task counts
/// once. The unplanned work is all but the planned.
/// </summary>
public sealed record SprintStatusSummary(
    SprintWorkTotals Planned, SprintWorkTotals NotPlanned, SprintWorkTotals Backlog, SprintWorkTotals OtherSprint,
    EstimateComparison AllEstimate)
{
    public static readonly SprintStatusSummary None = new(SprintWorkTotals.None, SprintWorkTotals.None, SprintWorkTotals.None,
        SprintWorkTotals.None, EstimateComparison.None);

    // The four lists Unplanned can be, by HasBacklog and HasOtherSprint: picked, not built, on each read.
    private static readonly SprintWorkKind[][] Shown =
    [
        [SprintWorkKind.NotPlanned],
        [SprintWorkKind.NotPlanned, SprintWorkKind.Backlog],
        [SprintWorkKind.NotPlanned, SprintWorkKind.OtherSprint],
        [SprintWorkKind.NotPlanned, SprintWorkKind.Backlog, SprintWorkKind.OtherSprint]
    ];

    public SprintTaskCounts All => Planned.Tasks.Plus(NotPlanned.Tasks).Plus(Backlog.Tasks).Plus(OtherSprint.Tasks);
    public int UnplannedSprintMinutes => NotPlanned.SprintMinutes + Backlog.SprintMinutes + OtherSprint.SprintMinutes;
    public int SprintMinutes => Planned.SprintMinutes + UnplannedSprintMinutes;
    public int ToDateMinutes => Planned.ToDateMinutes + NotPlanned.ToDateMinutes + Backlog.ToDateMinutes + OtherSprint.ToDateMinutes;

    /// <summary>Backlog tasks were worked on in the sprint (for the tasks picked): the report shows their group and columns.</summary>
    public bool HasBacklog => Backlog.Tasks.Total > 0;

    /// <summary>Other sprints' tasks were worked on in the sprint (for the tasks picked): the report shows their group and columns.</summary>
    public bool HasOtherSprint => OtherSprint.Tasks.Total > 0;

    /// <summary>Unplanned work's share of the time logged in the sprint; null when none was logged.</summary>
    public int? UnplannedPercent => SprintReportShare.Percent(UnplannedSprintMinutes, SprintMinutes);

    /// <summary>One kind of work's figures.</summary>
    public SprintWorkTotals Of(SprintWorkKind kind) => kind switch
    {
        SprintWorkKind.Planned => Planned,
        SprintWorkKind.NotPlanned => NotPlanned,
        SprintWorkKind.Backlog => Backlog,
        SprintWorkKind.OtherSprint => OtherSprint,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>
    /// The unplanned kinds of work the report shows, in its order: Not Planned always, then the Backlog and other sprints' tasks
    /// when some were worked on. The page, the PDF and the charts give each a column, a row or a series.
    /// </summary>
    public IReadOnlyList<SprintWorkKind> Unplanned => Shown[(HasBacklog ? 1 : 0) + (HasOtherSprint ? 2 : 0)];
}

/// <summary>Shares of the sprint's time, as rounded percentages.</summary>
public static class SprintReportShare
{
    /// <summary>A part's share of a whole; null when the whole is nothing.</summary>
    public static int? Percent(int part, int whole) => whole == 0 ? null : (int)Math.Round(part * 100.0 / whole);

    /// <summary>
    /// Splits <paramref name="total"/> percentage points over <paramref name="parts"/> in proportion to them, by largest remainder
    /// (ties to the earlier part), so the shares add up to <paramref name="total"/> exactly - where rounding each on its own
    /// needn't. All nothing when the parts are.
    /// </summary>
    public static IReadOnlyList<int> Apportion(int total, IReadOnlyList<int> parts)
    {
        var sum = parts.Sum();
        if (sum == 0) return parts.Select(_ => 0).ToList();
        var exact = parts.Select(p => p * (double)total / sum).ToList();
        var shares = exact.Select(e => (int)Math.Floor(e)).ToArray();
        var left = total - shares.Sum();
        foreach (var i in exact.Select((e, i) => (Fraction: e - Math.Floor(e), Index: i))
                     .OrderByDescending(x => x.Fraction).ThenBy(x => x.Index).Take(left).Select(x => x.Index))
            shares[i]++;
        return shares;
    }
}

/// <summary>A number for each kind of work: planned, Not Planned, from the Backlog and from another sprint.</summary>
public sealed record SprintKindCounts(int Planned, int NotPlanned, int Backlog, int OtherSprint)
{
    public static readonly SprintKindCounts None = new(0, 0, 0, 0);

    /// <summary>Each kind's number from <paramref name="of"/>.</summary>
    public static SprintKindCounts From(Func<SprintWorkKind, int> of) =>
        new(of(SprintWorkKind.Planned), of(SprintWorkKind.NotPlanned), of(SprintWorkKind.Backlog), of(SprintWorkKind.OtherSprint));

    public int Total => Planned + NotPlanned + Backlog + OtherSprint;

    /// <summary>The unplanned kinds' together: all but the planned.</summary>
    public int Unplanned => NotPlanned + Backlog + OtherSprint;

    /// <summary>One kind's number.</summary>
    public int Of(SprintWorkKind kind) => kind switch
    {
        SprintWorkKind.Planned => Planned,
        SprintWorkKind.NotPlanned => NotPlanned,
        SprintWorkKind.Backlog => Backlog,
        SprintWorkKind.OtherSprint => OtherSprint,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

/// <summary>
/// One day of the sprint: the time logged on it, and the tasks finished on it (<c>SprintReportRules.FinishedOn</c>), each by kind of
/// work.
/// </summary>
public sealed record SprintDayRow(DateOnly Date, SprintKindCounts Minutes, SprintKindCounts Finished);

/// <summary>
/// A person's tasks in the sprint by outcome, of every kind of work, cancelled ones included - Tasks by person. A task several
/// people share counts for each of them; <see cref="UserId"/> null is the Unassigned row.
/// </summary>
public sealed record SprintPersonTasks(Guid? UserId, string Name, int Done, int LeftOpen, int Cancelled)
{
    public int Total => Done + LeftOpen + Cancelled;
}

/// <summary>
/// A project's work in the sprint, every kind together - Tasks by project and Time by project: its tasks of every kind by outcome,
/// cancelled ones included, and its time in the sprint on each kind of work. <see cref="Project"/> null is the tasks on no project.
/// Unlike the By project rows, an unplanned task counts on its own project, not in a group of its kind.
/// </summary>
public sealed record SprintProjectWork(SprintProjectFacts? Project, SprintTaskCounts Tasks, SprintKindCounts Minutes);

/// <summary>
/// Sprint status (§12): one sprint's planned work, and the unplanned work done in its window - the Not Planned work, and the Backlog
/// and other sprints' tasks worked on anyway. <see cref="Sprint"/> null is no sprint to report on. <see cref="AsAt"/> is when the
/// outcomes are read: the sprint's completion, or now. <see cref="LeftOpenTo"/> is where the open tasks went when it ended ("Backlog"
/// or a sprint), null while it runs. <see cref="Rows"/> are its projects, then No project, then Not Planned, From the Backlog and From
/// another sprint; <see cref="Days"/> one row for each day of the sprint. The options are the filters', worked out before they apply:
/// the projects of the planned tasks, and the assignees of all the tasks. <see cref="Sprints"/> are the sprints the Sprint filter
/// offers (<c>SprintReportRules.Offered</c>), the first of them the default. <see cref="PersonTasks"/> are each person's tasks by
/// outcome, in <see cref="People"/>' order, Unassigned last. <see cref="ProjectWork"/> is each project's work of every kind, in the
/// projects' order, No project last.
/// </summary>
public sealed record SprintStatusReport(
    SprintFacts? Sprint, DateTime AsAt, SprintStatusSummary Summary, IReadOnlyList<SprintImpactRow> People,
    IReadOnlyList<SprintGroupRow> Rows, IReadOnlyList<SprintDayRow> Days, string? LeftOpenTo,
    IReadOnlyList<SprintProjectFacts> ProjectOptions, IReadOnlyList<ReportPerson> AssigneeOptions)
{
    public IReadOnlyList<SprintFacts> Sprints { get; init; } = [];
    public IReadOnlyList<SprintPersonTasks> PersonTasks { get; init; } = [];
    public IReadOnlyList<SprintProjectWork> ProjectWork { get; init; } = [];

    public static SprintStatusReport Empty(DateTime asAt) => new(null, asAt, SprintStatusSummary.None, [], [], [], null, [], []);
}

/// <summary>
/// A report in the catalogue. <see cref="ByProject"/> false hides the project filter: assets belong to no project.
/// <see cref="ByOwnerAndAssignee"/> shows the Owner and Assignee filters, which pick projects. <see cref="BySprint"/> takes a sprint
/// in place of the date range, and an Assignee filter that picks tasks.
/// </summary>
public sealed record ReportDefinition(
    ReportKind Kind, string Title, string Description, bool ByProject = true, bool ByOwnerAndAssignee = false, bool BySprint = false)
{
    /// <summary>The Assignee filter shows.</summary>
    public bool ByAssignee => ByOwnerAndAssignee || BySprint;
}

public static class ReportCatalog
{
    public static readonly IReadOnlyList<ReportDefinition> All =
    [
        new(ReportKind.ClosedByPerson, "Closed count by person",
            "Tasks set to Done in the period, grouped by the assignees who carried them to close. A shared task counts under each of them and once in the total."),
        new(ReportKind.CreatedByPerson, "Created count by person",
            "Tasks created in the period, grouped by who authored them. API-created tasks roll up under Claude."),
        new(ReportKind.MeanTimeToRespond, "Mean time to respond",
            "Average time from creation until the task first left Todo, for tasks created in the period."),
        new(ReportKind.MeanTimeToResolve, "Mean time to resolve",
            "Average time from creation to completion, for tasks completed in the period."),
        new(ReportKind.TimeByPerson, "Time by person",
            "Time each person logged in the period, and for the tasks they worked on, each task's estimate against all time logged on it to date."),
        new(ReportKind.EstimateAccuracy, "Estimate accuracy",
            "Tasks completed in the period, grouped by assignee: the estimate against all time logged on them. A shared task is under each of its assignees and counts once overall."),
        new(ReportKind.ProjectStatus, "Project status",
            "Every project open or active in the period, closed ones included: its status and status changes, tasks, schedule, and estimated and logged time by person.",
            ByOwnerAndAssignee: true),
        new(ReportKind.AssetStatus, "Asset status",
            "Every asset in the register during the period, disposed ones included: status by type and location, value at cost, checks, and the work done through linked tasks.",
            ByProject: false),
        new(ReportKind.SprintStatus, "Sprint status",
            "One sprint's work, for its review at sprint planning: the tasks planned into it that were done, left open or cancelled, beside the Not Planned work that came up during it and the Backlog and other sprints' tasks worked on anyway, with estimated and logged time by project and person.",
            BySprint: true)
    ];

    public static ReportDefinition Get(ReportKind kind) => All.First(r => r.Kind == kind);
}

public static class DurationFormat
{
    public static string Hours(double? hours)
    {
        if (hours is null) return "-";
        var h = hours.Value;
        if (h < 1) return $"{Math.Round(h * 60)} min";
        if (h < 48) return $"{h:0.0} h";
        return $"{h / 24:0.0} days";
    }
}
