using Orbit.Data.Entities;

namespace Orbit.Application.Messages;

/// <summary>One value a message may use (§6.7): its token name ("task.title"), a label for the picker, and a sample for the preview.</summary>
public sealed record MessageToken(string Name, string Label, string Sample)
{
    /// <summary>The token as written in a template: {{task.title}}.</summary>
    public string Text => "{{" + Name + "}}";
}

/// <summary>What one trigger offers a message (§6.7): when it fires, the conditions it may wait for, whom it may go to, and the values it may use.</summary>
/// <param name="Conditions">Always first.</param>
/// <param name="Recipients">In rank order (<see cref="MessageRecipientKind"/>).</param>
public sealed record MessageTriggerDefinition(
    MessageTrigger Trigger,
    string Description,
    string Group,
    IReadOnlyList<MessageCondition> Conditions,
    IReadOnlyList<MessageRecipientKind> Recipients,
    IReadOnlyList<MessageToken> Tokens)
{
    public string Label => Trigger.Label();

    public bool IsAboutTasks => Group == MessageCatalogue.TasksGroup;

    public bool IsAboutProjects => Group == MessageCatalogue.ProjectsGroup;

    public bool IsAboutRequests => Group == MessageCatalogue.RequestsGroup;

    public bool Offers(MessageCondition condition) => Conditions.Contains(condition);

    public bool Offers(MessageRecipientKind kind) => Recipients.Contains(kind);
}

/// <summary>
/// The fixed, code-defined triggers a message can be sent on (spec §6.7), as <see cref="PermissionCatalog"/> is for permissions:
/// messages are data (Admin &gt; Messages), what each trigger means and offers is defined here, and the services raise them.
/// </summary>
public static class MessageCatalogue
{
    public const string TasksGroup = "Tasks";
    public const string ProjectsGroup = "Projects";
    public const string RequestsGroup = "Requests";

    private static readonly MessageToken[] Everyone =
    [
        new("recipient.name", "Who the email is to", "Alex Morgan"),
        new("actor.name", "Who did it (System for a scheduled job)", "Sam Taylor")
    ];

    private static readonly MessageToken[] TaskTokens =
    [
        new("task.number", "Task: number", "T-26-00042"),
        new("task.title", "Task: title", "Replace the meeting room projector"),
        new("task.url", "Task: link", "https://orbit.example.com/Tasks/Details/6f1d2c9e-8b3a-4f57-9d0e-2a4b6c8d0e1f"),
        new("task.status", "Task: status", "In Progress"),
        new("task.priority", "Task: priority", "High"),
        new("task.type", "Task: type", "Task"),
        new("task.due", "Task: due date (none when it has none)", "2026-10-21"),
        new("task.department", "Task: department", "IT"),
        new("task.project", "Task: project (none when it has none)", "Office move"),
        new("task.assignees", "Task: assignees (nobody when it has none)", "Jordan Lee, Priya Shah"),
        new("task.requestee", "Task: requestee (nobody when it has none)", "Alex Morgan"),
        new("task.creator", "Task: who created it", "Sam Taylor"),
        new("task.description", "Task: description", "The projector in room 2.14 flickers and switches itself off after about ten minutes."),
        new("task.solution", "Task: solution notes - how it was resolved (none when it has none)", "Replaced the lamp and cleaned the filter; it has run for an hour without switching off.")
    ];

    private static readonly MessageToken[] ProjectTokens =
    [
        new("project.number", "Project: number", "P-26-00003"),
        new("project.name", "Project: name", "Office move"),
        new("project.url", "Project: link", "https://orbit.example.com/Projects/Details/9c4e2a71-3b5d-4f60-8e1a-7d2c5b9f0a34"),
        new("project.status", "Project: status", "On Hold"),
        new("project.department", "Project: department", "IT"),
        new("project.owner", "Project: owner", "Sam Taylor"),
        new("project.target", "Project: target date (none when it has none)", "2026-12-18"),
        new("project.description", "Project: description", "Move the IT office from the second floor to the new building over the Christmas break.")
    ];

    private static readonly MessageToken[] RequestTokens =
    [
        new("request.number", "Request: number", "R-26-00007"),
        new("request.title", "Request: title", "New starter: Jamie Fox"),
        new("request.url", "Request: link", "https://orbit.example.com/Requests/Request/2b7e4f1a-5c3d-4e6f-8a9b-0c1d2e3f4a5b"),
        new("request.status", "Request: status", "In progress"),
        new("request.flow", "Request: flow", "New starter"),
        new("request.department", "Request: department", "HR"),
        new("request.requester", "Request: who logged it", "Alex Morgan")
    ];

    private static readonly MessageToken StepTitle = new("step.title", "Step: title", "Manager approval");

    private static readonly MessageCondition[] AlwaysOnly = [MessageCondition.Always];

    private static readonly MessageRecipientKind[] TaskPeople =
        [MessageRecipientKind.Assignees, MessageRecipientKind.Requestee, MessageRecipientKind.Creator, MessageRecipientKind.DepartmentMembers];

    private static readonly MessageRecipientKind[] ProjectPeople = [MessageRecipientKind.ProjectOwner, MessageRecipientKind.DepartmentMembers];

    private static readonly MessageRecipientKind[] RequesterOnly = [MessageRecipientKind.Requester];

    private static readonly MessageToken CommentAuthor = new("comment.author", "Comment: who wrote it", "Jordan Lee");

    public static readonly IReadOnlyList<MessageTriggerDefinition> All =
    [
        new(MessageTrigger.TaskCreated,
            "A task is created: on the task form, over MCP, from a recurring definition, by Nagios monitoring or by a request flow's task step.",
            TasksGroup, [MessageCondition.Always, MessageCondition.Assigned, MessageCondition.Unassigned], TaskPeople,
            [.. Everyone, .. TaskTokens]),
        new(MessageTrigger.TaskAssigned,
            "People are added to an existing task's assignees: by an edit, the assignee control, the task page or taking the task. A new task's assignees hear of it through Task created.",
            TasksGroup, AlwaysOnly,
            [MessageRecipientKind.AddedAssignees, MessageRecipientKind.Assignees, MessageRecipientKind.Requestee, MessageRecipientKind.Creator],
            [.. Everyone, .. TaskTokens, new("assigned.names", "The people just assigned", "Jordan Lee")]),
        new(MessageTrigger.RequesteeNamed,
            "An edit names someone as a task's requestee, the person it is for.",
            TasksGroup, AlwaysOnly,
            [MessageRecipientKind.Assignees, MessageRecipientKind.Requestee, MessageRecipientKind.Creator],
            [.. Everyone, .. TaskTokens]),
        new(MessageTrigger.TaskClosed,
            "An open task is closed, as Done or Cancelled: by an edit, the status control, over MCP, or with the request it belongs to.",
            TasksGroup, [MessageCondition.Always, MessageCondition.Done, MessageCondition.Cancelled], TaskPeople,
            [.. Everyone, .. TaskTokens]),
        new(MessageTrigger.TaskCommented,
            "Someone comments on a task: a person, Claude, or Nagios monitoring (as System).",
            TasksGroup, [MessageCondition.Always, MessageCondition.ByAssignee, MessageCondition.ByRequestee, MessageCondition.ByOther], TaskPeople,
            [.. Everyone, .. TaskTokens, CommentAuthor, new("comment.body", "Comment: the text", "I've ordered a replacement lamp; it should arrive on Thursday.")]),
        new(MessageTrigger.TaskDueSoon,
            "An open task falls due within the reminder window of the daily due-date job. Once per task per due date.",
            TasksGroup, [MessageCondition.Always, MessageCondition.Assigned, MessageCondition.Unassigned], TaskPeople,
            [.. Everyone, .. TaskTokens, new("due.when", "When it is due: today, tomorrow or in so many days", "tomorrow")]),
        new(MessageTrigger.ProjectCreated,
            "A project is created: on the project form or over MCP.",
            ProjectsGroup, AlwaysOnly, ProjectPeople,
            [.. Everyone, .. ProjectTokens]),
        new(MessageTrigger.ProjectStatusChanged,
            "A project's status changes - put on hold, completed, archived or made active again: by an edit, the Archive button or over MCP.",
            ProjectsGroup, [MessageCondition.Always, MessageCondition.OnHold, MessageCondition.Completed, MessageCondition.Archived, MessageCondition.Reopened],
            ProjectPeople,
            [.. Everyone, .. ProjectTokens, new("project.previous-status", "Project: its status before", "Active")]),
        new(MessageTrigger.ProjectOwnerChanged,
            "An edit makes someone else the project's owner. The project's owner is the new one.",
            ProjectsGroup, AlwaysOnly, ProjectPeople,
            [.. Everyone, .. ProjectTokens, new("project.previous-owner", "Project: its owner before", "Jordan Lee")]),
        new(MessageTrigger.ProjectCommented,
            "Someone comments in a project's own thread - a person or Claude. Comments on the project's tasks are Task commented on.",
            ProjectsGroup, [MessageCondition.Always, MessageCondition.ByOwner, MessageCondition.ByOther], ProjectPeople,
            [.. Everyone, .. ProjectTokens, CommentAuthor, new("comment.body", "Comment: the text", "The movers can only come on the 22nd; can we bring the switch-over forward?")]),
        new(MessageTrigger.RequestStepWaiting,
            "A request step waits for someone: a form to fill in or a web page to open, for its person, or an approval stage opening, for its approvers.",
            RequestsGroup, AlwaysOnly, [MessageRecipientKind.StepPerson, MessageRecipientKind.Requester],
            [.. Everyone, .. RequestTokens, StepTitle, new("step.what", "What the step needs of them", "approve \"Manager approval\"")]),
        new(MessageTrigger.ApprovalDecided,
            "An approval step ends: approved once its last stage's rule is met, or declined by any of its approvers.",
            RequestsGroup, [MessageCondition.Always, MessageCondition.Approved, MessageCondition.Declined], RequesterOnly,
            [.. Everyone, .. RequestTokens, StepTitle,
                new("approval.outcome", "Approval: Approved or Declined", "Approved"),
                new("approval.by", "Approval: who decided last", "Chris Doe"),
                new("approval.comment", "Approval: their comment", "Fine by me.")]),
        new(MessageTrigger.ActionRan,
            "An action step's script has run, on the Orbit server or an Orbit Agent.",
            RequestsGroup, [MessageCondition.Always, MessageCondition.Succeeded, MessageCondition.Failed], RequesterOnly,
            [.. Everyone, .. RequestTokens, StepTitle,
                new("action.name", "Action: name", "Create the directory account"),
                new("action.outcome", "Action: succeeded or failed", "succeeded"),
                new("action.output", "Action: the script's output", "Account jfox created."),
                new("action.error", "Action: the error, when it failed", "The agent didn't answer within 120 seconds.")]),
        new(MessageTrigger.RequestFinished,
            "A request finishes: completed, declined or cancelled.",
            RequestsGroup, [MessageCondition.Always, MessageCondition.Completed, MessageCondition.Declined, MessageCondition.Cancelled], RequesterOnly,
            [.. Everyone, .. RequestTokens, new("request.outcome", "Request: how it ended (completed, declined or cancelled)", "completed")])
    ];

    public static readonly IReadOnlyDictionary<MessageTrigger, MessageTriggerDefinition> ByTrigger = All.ToDictionary(t => t.Trigger);

    public static MessageTriggerDefinition Get(MessageTrigger trigger) => ByTrigger[trigger];

    /// <summary>The catalogue in display order, grouped for the Messages page.</summary>
    public static IEnumerable<IGrouping<string, MessageTriggerDefinition>> Groups => All.GroupBy(t => t.Group);
}
