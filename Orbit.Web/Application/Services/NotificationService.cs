using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Messages;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Where the services tell of what happened (spec §6.7): one method per event. Each finds the enabled messages for its trigger, works
/// out whom they go to and what they say (<see cref="MessageRules"/>), and adds the emails to the outbox - to the caller's unit of
/// work, so they are saved with the change they tell of, or not at all. Call it before the save. <see cref="EmailDispatcher"/> sends
/// them. The actor is explicit and nothing here reads the signed-in user, so the jobs raise events too. Never throws, but for
/// cancellation: an email that can't be written is logged, and the change goes ahead without it.
/// </summary>
public sealed class NotificationService(
    ApplicationDbContext db,
    IOptions<AppOptions> app,
    ILogger<NotificationService> logger)
{
    private readonly Dictionary<MessageTrigger, IReadOnlyList<MessageSpec>> _messages = [];
    private readonly Dictionary<(Guid Department, string Permission), IReadOnlyList<Guid>> _members = [];
    private readonly Dictionary<Guid, string?> _departments = [];

    // ---------------------------------------------------------------- tasks

    /// <summary>A task was created - on the form, over MCP, by the recurring job, by Nagios or by a request flow. Call after its number is set.</summary>
    public Task<int> TaskCreatedAsync(TaskItem task, Actor actor, CancellationToken ct = default) =>
        TaskEventAsync(MessageTrigger.TaskCreated, task, actor, assignees => MessageRules.TaskConditions(assignees.Count > 0), null, ct);

    /// <summary>People were added to an existing task's assignees. Returns how many were emailed.</summary>
    public Task<int> TaskAssignedAsync(TaskItem task, IReadOnlyCollection<Guid> added, Actor actor, HashSet<Guid>? told = null, CancellationToken ct = default) =>
        added.Count == 0
            ? Task.FromResult(0)
            : TaskEventAsync(MessageTrigger.TaskAssigned, task, actor, _ => MessageRules.AlwaysHolds, told, ct, added,
                (values, people) => values["assigned.names"] = Names(added, people, "nobody"));

    /// <summary>An edit named someone as the task's requestee.</summary>
    public Task<int> RequesteeNamedAsync(TaskItem task, Actor actor, HashSet<Guid>? told = null, CancellationToken ct = default) =>
        TaskEventAsync(MessageTrigger.RequesteeNamed, task, actor, _ => MessageRules.AlwaysHolds, told, ct);

    /// <summary>An open task was closed, as Done or Cancelled.</summary>
    public Task<int> TaskClosedAsync(TaskItem task, Actor actor, HashSet<Guid>? told = null, CancellationToken ct = default) =>
        TaskEventAsync(MessageTrigger.TaskClosed, task, actor, _ => MessageRules.ClosedConditions(task.Status), told, ct);

    /// <summary>Someone commented on a task: the actor is the author (the system for a comment with none).</summary>
    public Task<int> TaskCommentedAsync(TaskItem task, Comment comment, Actor actor, CancellationToken ct = default) =>
        TaskEventAsync(MessageTrigger.TaskCommented, task, actor,
            assignees => MessageRules.CommentConditions(comment.AuthorId, assignees, task.RequesteeId), null, ct, null,
            (values, _) =>
            {
                values["comment.author"] = comment.AuthorId is null ? Actor.System.DisplayName : actor.DisplayName;
                values["comment.body"] = MessageRules.Clip(comment.Body);
            });

    /// <summary>An open task falls due soon (the due-date job, as the system). Returns how many were emailed, so the job knows whether anyone was told.</summary>
    public Task<int> TaskDueSoonAsync(TaskItem task, DateOnly today, CancellationToken ct = default) =>
        task.DueDate is not DateOnly due
            ? Task.FromResult(0)
            : TaskEventAsync(MessageTrigger.TaskDueSoon, task, Actor.System, assignees => MessageRules.TaskConditions(assignees.Count > 0), null, ct, null,
                (values, _) => values["due.when"] = MessageRules.DueWhen(due, today));

    // ---------------------------------------------------------------- requests

    /// <summary>A request step waits for people: a form or web page for its person, an approval stage for its approvers. <paramref name="what"/> reads "fill in \"Details\"".</summary>
    public Task<int> RequestStepWaitingAsync(Request request, RequestStep step, IReadOnlyCollection<Guid> people, string what, Actor actor, CancellationToken ct = default) =>
        RequestEventAsync(MessageTrigger.RequestStepWaiting, request, actor, MessageRules.AlwaysHolds, people, values =>
        {
            values["step.title"] = step.FlowStep.Title;
            values["step.what"] = what;
        }, ct);

    /// <summary>An approval step ended: approved (its last stage's rule met) or declined. <paramref name="decision"/> is the decision that ended it.</summary>
    public Task<int> ApprovalDecidedAsync(Request request, RequestStep step, RequestApproval decision, Actor actor, CancellationToken ct = default) =>
        RequestEventAsync(MessageTrigger.ApprovalDecided, request, actor, MessageRules.ApprovalConditions(step.Status == RequestStepStatus.Completed), [], values =>
        {
            values["step.title"] = step.FlowStep.Title;
            values["approval.outcome"] = step.Status == RequestStepStatus.Completed ? ApprovalDecision.Approved.Label() : ApprovalDecision.Declined.Label();
            values["approval.by"] = actor.DisplayName;
            values["approval.comment"] = decision.Comment;
        }, ct);

    /// <summary>An action step's script ran (the action job, as the system).</summary>
    public Task<int> ActionRanAsync(Request request, RequestStep step, string? actionName, bool succeeded, Actor actor, CancellationToken ct = default) =>
        RequestEventAsync(MessageTrigger.ActionRan, request, actor, MessageRules.ActionConditions(succeeded), [], values =>
        {
            values["step.title"] = step.FlowStep.Title;
            values["action.name"] = actionName ?? step.FlowStep.Title;
            values["action.outcome"] = succeeded ? "succeeded" : "failed";
            values["action.output"] = MessageRules.Clip(step.Output);
            values["action.error"] = MessageRules.Clip(step.Error);
        }, ct);

    /// <summary>A request finished: completed, declined or cancelled.</summary>
    public Task<int> RequestFinishedAsync(Request request, Actor actor, CancellationToken ct = default) =>
        RequestEventAsync(MessageTrigger.RequestFinished, request, actor, MessageRules.FinishedConditions(request.Status), [], values =>
            values["request.outcome"] = request.Status.Label().ToLowerInvariant(), ct);

    // ---------------------------------------------------------------- projects

    /// <summary>A project was created - on the form or over MCP. Call after its number is set.</summary>
    public Task<int> ProjectCreatedAsync(Project project, Actor actor, CancellationToken ct = default) =>
        ProjectEventAsync(MessageTrigger.ProjectCreated, project, actor, MessageRules.AlwaysHolds, null, ct);

    /// <summary>A project's status changed from <paramref name="from"/> to the one it has now.</summary>
    public Task<int> ProjectStatusChangedAsync(Project project, ProjectStatus from, Actor actor, HashSet<Guid>? told = null, CancellationToken ct = default) =>
        ProjectEventAsync(MessageTrigger.ProjectStatusChanged, project, actor, MessageRules.ProjectStatusConditions(project.Status), told, ct, [],
            (values, _) => values["project.previous-status"] = from.Label());

    /// <summary>An edit made someone else the project's owner: the project's owner is the new one.</summary>
    public Task<int> ProjectOwnerChangedAsync(Project project, Guid previousOwnerId, Actor actor, HashSet<Guid>? told = null, CancellationToken ct = default) =>
        ProjectEventAsync(MessageTrigger.ProjectOwnerChanged, project, actor, MessageRules.AlwaysHolds, told, ct, [previousOwnerId],
            (values, people) => values["project.previous-owner"] = Names([previousOwnerId], people, "nobody"));

    /// <summary>Someone commented in a project's own thread: the actor is the author.</summary>
    public Task<int> ProjectCommentedAsync(Project project, Comment comment, Actor actor, CancellationToken ct = default) =>
        ProjectEventAsync(MessageTrigger.ProjectCommented, project, actor, MessageRules.ProjectCommentConditions(comment.AuthorId, project.OwnerId), null, ct, [],
            (values, _) =>
            {
                values["comment.author"] = comment.AuthorId is null ? Actor.System.DisplayName : actor.DisplayName;
                values["comment.body"] = MessageRules.Clip(comment.Body);
            });

    // ---------------------------------------------------------------- the events

    /// <summary>
    /// What an event is, before anyone has been looked up. <see cref="MembersOf"/> is the department whose members the Department
    /// members recipient means, and <see cref="MembersPermission"/> the permission they must hold at Department or All to open what
    /// the email is about.
    /// </summary>
    private sealed record Draft(
        MessageTrigger Trigger,
        Actor Actor,
        string EntityType,
        Guid EntityId,
        IReadOnlySet<MessageCondition> Holds,
        Dictionary<MessageRecipientKind, IReadOnlyCollection<Guid>> People,
        Guid? MembersOf,
        string? MembersPermission,
        IReadOnlyCollection<Guid> NamesOf,
        Func<IReadOnlyDictionary<Guid, MessagePerson>, CancellationToken, Task<Dictionary<string, string?>>> Values);

    private Task<int> TaskEventAsync(MessageTrigger trigger, TaskItem task, Actor actor,
        Func<IReadOnlyList<Guid>, IReadOnlySet<MessageCondition>> holds, HashSet<Guid>? told, CancellationToken ct,
        IReadOnlyCollection<Guid>? added = null, Action<Dictionary<string, string?>, IReadOnlyDictionary<Guid, MessagePerson>>? extra = null) =>
        GuardAsync(trigger, async () =>
        {
            var assignees = await CurrentAssigneeIdsAsync(task, ct);
            var people = new Dictionary<MessageRecipientKind, IReadOnlyCollection<Guid>>
            {
                [MessageRecipientKind.Assignees] = assignees,
                [MessageRecipientKind.AddedAssignees] = added ?? [],
                [MessageRecipientKind.Requestee] = task.RequesteeId is Guid r ? [r] : [],
                [MessageRecipientKind.Creator] = task.CreatedById is Guid c ? [c] : []
            };
            List<Guid> names = [.. assignees, .. (added ?? []), .. people[MessageRecipientKind.Requestee], .. people[MessageRecipientKind.Creator]];
            return await QueueAsync(new Draft(trigger, actor, AuditEntity.Task, task.Id, holds(assignees), people, task.DepartmentId, Permission.TasksView, names,
                async (persons, token) =>
                {
                    var values = await TaskValuesAsync(task, assignees, actor, persons, token);
                    extra?.Invoke(values, persons);
                    return values;
                }), told, ct);
        });

    private Task<int> RequestEventAsync(MessageTrigger trigger, Request request, Actor actor, IReadOnlySet<MessageCondition> holds,
        IReadOnlyCollection<Guid> stepPeople, Action<Dictionary<string, string?>> extra, CancellationToken ct) =>
        GuardAsync(trigger, () =>
        {
            var people = new Dictionary<MessageRecipientKind, IReadOnlyCollection<Guid>>
            {
                [MessageRecipientKind.Requester] = [request.RequesterId],
                [MessageRecipientKind.StepPerson] = stepPeople
            };
            return QueueAsync(new Draft(trigger, actor, AuditEntity.Request, request.Id, holds, people, null, null, [],
                (_, _) =>
                {
                    var values = RequestValues(request, actor);
                    extra(values);
                    return Task.FromResult(values);
                }), null, ct);
        });

    private Task<int> ProjectEventAsync(MessageTrigger trigger, Project project, Actor actor, IReadOnlySet<MessageCondition> holds,
        HashSet<Guid>? told, CancellationToken ct, IReadOnlyCollection<Guid>? alsoNamed = null,
        Action<Dictionary<string, string?>, IReadOnlyDictionary<Guid, MessagePerson>>? extra = null) =>
        GuardAsync(trigger, () =>
        {
            var people = new Dictionary<MessageRecipientKind, IReadOnlyCollection<Guid>>
            {
                [MessageRecipientKind.ProjectOwner] = [project.OwnerId]
            };
            List<Guid> names = [project.OwnerId, .. (alsoNamed ?? [])];
            return QueueAsync(new Draft(trigger, actor, AuditEntity.Project, project.Id, holds, people, project.DepartmentId, Permission.ProjectsView, names,
                async (persons, token) =>
                {
                    var values = await ProjectValuesAsync(project, actor, persons, token);
                    extra?.Invoke(values, persons);
                    return values;
                }), told, ct);
        });

    /// <summary>
    /// The work every event shares: the enabled messages for the trigger, those whose condition holds, the people in the roles they
    /// name (a department's members fetched only when asked for), the plan, and one outbox row per email. Stops as soon as there is
    /// nothing to send, so an event no message listens to costs one cached query.
    /// </summary>
    private async Task<int> QueueAsync(Draft draft, HashSet<Guid>? told, CancellationToken ct)
    {
        var messages = await MessagesAsync(draft.Trigger, ct);
        if (messages.Count == 0) return 0;
        var empty = new Dictionary<string, string?>();
        var matching = MessageRules.Matching(new MessageEvent(draft.Trigger, draft.Actor.UserId, draft.Holds, draft.People, empty), messages);
        if (matching.Count == 0) return 0;

        var wanted = MessageRules.Wanted(draft.Trigger, matching);
        if (wanted.Contains(MessageRecipientKind.DepartmentMembers) && draft.MembersOf is Guid department && draft.MembersPermission is { } permission)
            draft.People[MessageRecipientKind.DepartmentMembers] = await DepartmentMembersAsync(department, permission, ct);
        var candidates = wanted.SelectMany(k => draft.People.GetValueOrDefault(k) ?? [])
            .Where(id => id != draft.Actor.UserId && told?.Contains(id) != true).ToHashSet();
        if (candidates.Count == 0) return 0;

        var persons = await PeopleAsync([.. candidates, .. draft.NamesOf], ct);
        var e = new MessageEvent(draft.Trigger, draft.Actor.UserId, draft.Holds, draft.People, await draft.Values(persons, ct));
        var planned = MessageRules.Plan(e, matching, persons, told);
        var now = DateTime.UtcNow;
        var emails = planned.Select(p =>
        {
            var (subject, body) = MessageRules.Render(p, e);
            return new OutboundEmail
            {
                CreatedAt = now,
                NextAttemptAt = now,
                Trigger = draft.Trigger,
                MessageTemplateId = p.Message.Id,
                MessageName = p.Message.Name,
                EntityType = draft.EntityType,
                EntityId = draft.EntityId,
                ToUserId = p.To.Id,
                ToAddress = p.To.Email!.Trim(),
                ToName = p.To.DisplayName,
                Subject = subject,
                Body = body
            };
        }).ToList();
        db.OutboundEmails.AddRange(emails);
        foreach (var email in emails) told?.Add(email.ToUserId!.Value);
        return emails.Count;
    }

    /// <summary>Runs an event so that only cancellation escapes: a failure to write an email must never fail the change it tells of.</summary>
    private async Task<int> GuardAsync(MessageTrigger trigger, Func<Task<int>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't write the emails for the event {Trigger}.", trigger);
            return 0;
        }
    }

    // ---------------------------------------------------------------- what the events need

    /// <summary>The enabled messages for a trigger, read once per scope - a recurring run that creates fifty tasks asks once.</summary>
    private async Task<IReadOnlyList<MessageSpec>> MessagesAsync(MessageTrigger trigger, CancellationToken ct)
    {
        if (_messages.TryGetValue(trigger, out var cached)) return cached;
        var rows = await db.MessageTemplates.AsNoTracking().Include(m => m.Recipients)
            .Where(m => m.Enabled && m.Trigger == trigger).ToListAsync(ct);
        var specs = rows.Select(m => new MessageSpec(m.Id, m.Name, m.Condition, m.Recipients.Select(r => r.Kind).ToList(), m.Subject, m.Body)).ToList();
        _messages[trigger] = specs;
        return specs;
    }

    /// <summary>
    /// A department's members as a message's recipients: active people with an address whose role shows them the department's
    /// tasks or projects (<paramref name="permission"/> - tasks.view or projects.view - at Department or All), not someone who would
    /// be sent something they can't open.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> DepartmentMembersAsync(Guid departmentId, string permission, CancellationToken ct)
    {
        if (_members.TryGetValue((departmentId, permission), out var cached)) return cached;
        var ids = await db.Users.AsNoTracking()
            .Where(u => u.DepartmentId == departmentId && u.IsActive && !u.IsSystemAccount && u.Email != null && u.Email != "")
            .Select(u => u.Id).ToListAsync(ct);
        var roles = await RoleResolver.ForUsersAsync(db, ids, ct);
        var members = ids.Where(id => roles.TryGetValue(id, out var role) && role.ScopeOf(permission) >= PermissionScope.Department).ToList();
        _members[(departmentId, permission)] = members;
        return members;
    }

    private async Task<IReadOnlyDictionary<Guid, MessagePerson>> PeopleAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        var rows = await db.Users.AsNoTracking().Where(u => distinct.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsActive, u.IsSystemAccount }).ToListAsync(ct);
        return rows.ToDictionary(u => u.Id, u => new MessagePerson(u.Id, u.DisplayName, u.Email, u.IsActive, u.IsSystemAccount));
    }

    /// <summary>
    /// Who is assigned as the save will leave it: the task's assignments without the ones being removed, with the ones being added -
    /// before the save, a removed row is still in the collection and an added one may be only in the change tracker.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> CurrentAssigneeIdsAsync(TaskItem task, CancellationToken ct)
    {
        // A task reached through another entity may have come without its assignments; then they are read.
        IEnumerable<Guid> saved = task.Assignments?.Select(a => a.UserId)
            ?? await db.TaskAssignments.AsNoTracking().Where(a => a.TaskId == task.Id).Select(a => a.UserId).ToListAsync(ct);
        var entries = db.ChangeTracker.Entries<TaskAssignment>().Where(x => x.Entity.TaskId == task.Id).ToList();
        var removed = entries.Where(x => x.State == EntityState.Deleted).Select(x => x.Entity.UserId).ToHashSet();
        var added = entries.Where(x => x.State == EntityState.Added).Select(x => x.Entity.UserId);
        return saved.Where(id => !removed.Contains(id)).Concat(added).Distinct().ToList();
    }

    /// <summary>
    /// A task's values. Names come from the people looked up and the task's ids, not its navigation properties, which an edit may
    /// have left behind (a department changed, a requestee replaced).
    /// </summary>
    private async Task<Dictionary<string, string?>> TaskValuesAsync(TaskItem task, IReadOnlyList<Guid> assignees, Actor actor,
        IReadOnlyDictionary<Guid, MessagePerson> people, CancellationToken ct)
    {
        var project = task.ProjectId is Guid p
            ? await db.Projects.AsNoTracking().Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync(ct)
            : null;
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["actor.name"] = actor.DisplayName,
            ["task.number"] = task.Number,
            ["task.title"] = task.Title,
            ["task.url"] = $"{BaseUrl}/Tasks/Details/{task.Id}",
            ["task.status"] = task.Status.Label(),
            ["task.priority"] = task.Priority.ToString(),
            ["task.type"] = task.Type.ToString(),
            ["task.due"] = task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none",
            ["task.department"] = await DepartmentNameAsync(task.DepartmentId, ct),
            ["task.project"] = project ?? "none",
            ["task.assignees"] = Names(assignees, people, "nobody"),
            ["task.requestee"] = task.RequesteeId is Guid r ? Names([r], people, "nobody") : "nobody",
            ["task.creator"] = task.CreatedById is Guid c ? Names([c], people, Actor.System.DisplayName) : Actor.System.DisplayName,
            ["task.description"] = MessageRules.Clip(task.Description, 2000)
        };
    }

    /// <summary>A project's values, by its ids: an edit may have left its owner and department navigations behind.</summary>
    private async Task<Dictionary<string, string?>> ProjectValuesAsync(Project project, Actor actor,
        IReadOnlyDictionary<Guid, MessagePerson> people, CancellationToken ct) => new(StringComparer.Ordinal)
    {
        ["actor.name"] = actor.DisplayName,
        ["project.number"] = project.Number,
        ["project.name"] = project.Name,
        ["project.url"] = $"{BaseUrl}/Projects/Details/{project.Id}",
        ["project.status"] = project.Status.Label(),
        ["project.department"] = await DepartmentNameAsync(project.DepartmentId, ct),
        ["project.owner"] = Names([project.OwnerId], people, "nobody"),
        ["project.target"] = project.TargetDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none",
        ["project.description"] = MessageRules.Clip(project.Description, 2000)
    };

    /// <summary>A request's values. The engine has the request loaded with its requester, department and flow.</summary>
    private Dictionary<string, string?> RequestValues(Request request, Actor actor) => new(StringComparer.Ordinal)
    {
        ["actor.name"] = actor.DisplayName,
        ["request.number"] = request.Number,
        ["request.title"] = request.Title,
        ["request.url"] = $"{BaseUrl}/Requests/Request/{request.Id}",
        ["request.status"] = request.Status.Label(),
        ["request.flow"] = request.Flow.Title,
        ["request.department"] = request.Department.Name,
        ["request.requester"] = request.Requester.DisplayName
    };

    private async Task<string?> DepartmentNameAsync(Guid id, CancellationToken ct)
    {
        if (_departments.TryGetValue(id, out var cached)) return cached;
        var name = await db.Departments.AsNoTracking().Where(d => d.Id == id).Select(d => d.Name).FirstOrDefaultAsync(ct);
        _departments[id] = name;
        return name;
    }

    private static string Names(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, MessagePerson> people, string none)
    {
        var names = ids.Select(id => people.GetValueOrDefault(id)?.DisplayName).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 0 ? none : string.Join(", ", names);
    }

    private string BaseUrl => app.Value.BaseUrl.TrimEnd('/');
}
