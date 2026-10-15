using Orbit.Application;
using Orbit.Application.Messages;
using Orbit.Data.Entities;

namespace Orbit.Tests.Messages;

/// <summary>Which messages an event sends, to whom, and what they say (spec §6.7).</summary>
public class MessageRulesTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Ann = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Cat = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, MessagePerson> People = new Dictionary<Guid, MessagePerson>
    {
        [Me] = Person(Me, "Me"),
        [Ann] = Person(Ann, "Ann"),
        [Bob] = Person(Bob, "Bob"),
        [Cat] = Person(Cat, "Cat")
    };

    private static MessagePerson Person(Guid id, string name, bool active = true, bool system = false) =>
        new(id, name, $"{name.ToLowerInvariant()}@example.com", active, system);

    private static MessageSpec Message(string name, MessageCondition condition, params MessageRecipientKind[] to) =>
        new(Guid.NewGuid(), name, condition, to, $"[Orbit] {name}: {{{{task.title}}}}", "Hi {{recipient.name}},\n\n{{actor.name}} did it.");

    private static MessageEvent Event(MessageTrigger trigger, Guid? actor, IReadOnlySet<MessageCondition> holds,
        params (MessageRecipientKind Kind, Guid[] Ids)[] people) =>
        new(trigger, actor, holds, people.ToDictionary(p => p.Kind, p => (IReadOnlyCollection<Guid>)p.Ids),
            new Dictionary<string, string?> { ["task.title"] = "Fix the printer", ["actor.name"] = "Me" });

    /// <summary>MSG-002: a message is refused without a name, subject, body or recipient, with a condition or recipient its trigger doesn't offer, or with a token that isn't one of its values.</summary>
    [Fact]
    public void Validate_refuses_what_cant_be_sent()
    {
        MessageRecipientKind[] assignees = [MessageRecipientKind.Assignees];
        MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Unassigned, [MessageRecipientKind.DepartmentMembers], "New", "{{task.title}}", "{{task.url}}");

        Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, assignees, " ", "s", "b"));
        Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, assignees, "n", null, "b"));
        Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, assignees, "n", "s", ""));
        Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, assignees, new string('n', 101), "s", "b"));
        Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, assignees, "n", new string('s', 301), "b"));
        Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, assignees, "n", "s", new string('b', 8001)));
        Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, [], "n", "s", "b"));

        var condition = Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Done, assignees, "n", "s", "b"));
        Assert.Contains("Task created", condition.Message);
        var recipient = Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.ApprovalDecided, MessageCondition.Always, assignees, "n", "s", "b"));
        Assert.Contains("The assignees", recipient.Message);
        var token = Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.TaskCreated, MessageCondition.Always, assignees, "n", "s", "{{comment.body}}"));
        Assert.Contains("{{comment.body}}", token.Message);
        Assert.StartsWith("The body", token.Message);
        var inSubject = Assert.Throws<ValidationException>(() => MessageRules.Validate(MessageTrigger.ActionRan, MessageCondition.Failed, [MessageRecipientKind.Requester], "n", "{{task.title}}", "b"));
        Assert.StartsWith("The subject", inSubject.Message);
    }

    /// <summary>MSG-003: the conditions that hold for each kind of event; Always always does.</summary>
    [Fact]
    public void Conditions_follow_the_event()
    {
        Assert.Equal([MessageCondition.Always, MessageCondition.Assigned], MessageRules.TaskConditions(true).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.Unassigned], MessageRules.TaskConditions(false).Order());
        Assert.Contains(MessageCondition.Done, MessageRules.ClosedConditions(TaskItemStatus.Done));
        Assert.Contains(MessageCondition.Cancelled, MessageRules.ClosedConditions(TaskItemStatus.Cancelled));
        Assert.Equal([MessageCondition.Always], MessageRules.ClosedConditions(TaskItemStatus.Todo));
        Assert.Contains(MessageCondition.Approved, MessageRules.ApprovalConditions(true));
        Assert.Contains(MessageCondition.Declined, MessageRules.ApprovalConditions(false));
        Assert.Contains(MessageCondition.Succeeded, MessageRules.ActionConditions(true));
        Assert.Contains(MessageCondition.Failed, MessageRules.ActionConditions(false));
        Assert.Contains(MessageCondition.Completed, MessageRules.FinishedConditions(RequestStatus.Completed));
        Assert.Contains(MessageCondition.Declined, MessageRules.FinishedConditions(RequestStatus.Declined));
        Assert.Contains(MessageCondition.Cancelled, MessageRules.FinishedConditions(RequestStatus.Cancelled));
    }

    /// <summary>MSG-003: a comment is by an assignee, by the requestee, by both, or by someone else - another person, Claude, or the system.</summary>
    [Fact]
    public void A_comment_is_by_an_assignee_the_requestee_or_someone_else()
    {
        Guid[] assignees = [Ann, Bob];
        Assert.Equal([MessageCondition.Always, MessageCondition.ByAssignee], MessageRules.CommentConditions(Ann, assignees, Cat).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.ByRequestee], MessageRules.CommentConditions(Cat, assignees, Cat).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.ByAssignee, MessageCondition.ByRequestee], MessageRules.CommentConditions(Ann, assignees, Ann).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.ByOther], MessageRules.CommentConditions(Me, assignees, Cat).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.ByOther], MessageRules.CommentConditions(null, assignees, null).Order());
    }

    /// <summary>MSG-014: a project's new status, and whether its owner wrote a comment on it.</summary>
    [Fact]
    public void Project_conditions_follow_the_new_status_and_the_author()
    {
        Assert.Equal([MessageCondition.Always, MessageCondition.Reopened], MessageRules.ProjectStatusConditions(ProjectStatus.Active).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.OnHold], MessageRules.ProjectStatusConditions(ProjectStatus.OnHold).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.Completed], MessageRules.ProjectStatusConditions(ProjectStatus.Completed).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.Archived], MessageRules.ProjectStatusConditions(ProjectStatus.Archived).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.ByOwner], MessageRules.ProjectCommentConditions(Ann, Ann).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.ByOther], MessageRules.ProjectCommentConditions(Bob, Ann).Order());
        Assert.Equal([MessageCondition.Always, MessageCondition.ByOther], MessageRules.ProjectCommentConditions(null, Ann).Order());
    }

    /// <summary>MSG-015: a project's owner who is also one of its department's members gets the owner's email only, and the one who acted none.</summary>
    [Fact]
    public void A_project_owner_in_the_department_gets_the_owners_email()
    {
        var toOwner = Message("Project comment: tell its owner", MessageCondition.ByOther, MessageRecipientKind.ProjectOwner);
        var toDepartment = Message("Any project comment: tell the department", MessageCondition.Always, MessageRecipientKind.DepartmentMembers);
        var e = Event(MessageTrigger.ProjectCommented, Bob, MessageRules.ProjectCommentConditions(Bob, Ann),
            (MessageRecipientKind.ProjectOwner, [Ann]), (MessageRecipientKind.DepartmentMembers, [Ann, Bob, Cat]));

        var planned = MessageRules.Plan(e, [toDepartment, toOwner], People);

        Assert.Equal([("Ann", "Project comment: tell its owner"), ("Cat", "Any project comment: tell the department")],
            planned.Select(p => (p.To.DisplayName, p.Message.Name)));
        // The owner writing it: the owner's message doesn't hold, and the owner isn't told what they wrote.
        var byOwner = Event(MessageTrigger.ProjectCommented, Ann, MessageRules.ProjectCommentConditions(Ann, Ann),
            (MessageRecipientKind.ProjectOwner, [Ann]), (MessageRecipientKind.DepartmentMembers, [Ann, Bob]));
        Assert.Equal(["Bob"], MessageRules.Plan(byOwner, [toDepartment, toOwner], People).Select(p => p.To.DisplayName));
    }

    /// <summary>MSG-004 (and ASGN-014): nobody is told what they did themselves; an event of the system or an API key leaves nobody out.</summary>
    [Fact]
    public void The_actor_is_never_told()
    {
        var message = Message("Assigned", MessageCondition.Always, MessageRecipientKind.AddedAssignees);
        var byMe = Event(MessageTrigger.TaskAssigned, Me, MessageRules.AlwaysHolds, (MessageRecipientKind.AddedAssignees, [Ann, Me, Bob, Ann]));
        Assert.Equal(["Ann", "Bob"], MessageRules.Plan(byMe, [message], People).Select(p => p.To.DisplayName));

        var onlyMe = Event(MessageTrigger.TaskAssigned, Me, MessageRules.AlwaysHolds, (MessageRecipientKind.AddedAssignees, [Me]));
        Assert.Empty(MessageRules.Plan(onlyMe, [message], People));

        var bySystem = Event(MessageTrigger.TaskAssigned, null, MessageRules.AlwaysHolds, (MessageRecipientKind.AddedAssignees, [Me, Ann]));
        Assert.Equal(["Ann", "Me"], MessageRules.Plan(bySystem, [message], People).Select(p => p.To.DisplayName));
    }

    /// <summary>MSG-005: only active people, never the Claude user, and only with an address.</summary>
    [Fact]
    public void Only_people_who_can_receive_an_email_get_one()
    {
        Assert.True(MessageRules.CanReceive(Person(Ann, "Ann")));
        Assert.False(MessageRules.CanReceive(Person(Ann, "Ann", active: false)));
        Assert.False(MessageRules.CanReceive(Person(Ann, "Ann", system: true)));
        Assert.False(MessageRules.CanReceive(Person(Ann, "Ann") with { Email = " " }));
        Assert.False(MessageRules.CanReceive(Person(Ann, "Ann") with { Email = null }));

        var people = new Dictionary<Guid, MessagePerson> { [Ann] = Person(Ann, "Ann", active: false), [Bob] = Person(Bob, "Bob") };
        var e = Event(MessageTrigger.TaskCreated, null, MessageRules.TaskConditions(true), (MessageRecipientKind.Assignees, [Ann, Bob, Cat]));
        var planned = MessageRules.Plan(e, [Message("Created", MessageCondition.Always, MessageRecipientKind.Assignees)], people);
        Assert.Equal(["Bob"], planned.Select(p => p.To.DisplayName)); // Ann is inactive; Cat wasn't loaded
    }

    /// <summary>
    /// MSG-006 (and §6.2.2): one email per person per event - the message whose recipient role ranks first, then by name. A requestee
    /// who is also an assignee gets the assignees' email only.
    /// </summary>
    [Fact]
    public void Each_person_gets_one_email_the_most_personal()
    {
        var toAssignees = Message("Task created: tell its assignees", MessageCondition.Assigned, MessageRecipientKind.Assignees);
        var toRequestee = Message("Task created: tell its requestee", MessageCondition.Always, MessageRecipientKind.Requestee);
        var toDepartment = Message("A department notice", MessageCondition.Always, MessageRecipientKind.DepartmentMembers);
        var e = Event(MessageTrigger.TaskCreated, Me, MessageRules.TaskConditions(true),
            (MessageRecipientKind.Assignees, [Ann]), (MessageRecipientKind.Requestee, [Ann]), (MessageRecipientKind.DepartmentMembers, [Ann, Bob, Me]));

        var planned = MessageRules.Plan(e, [toDepartment, toRequestee, toAssignees], People);

        Assert.Equal(2, planned.Count);
        Assert.Same(toAssignees, planned[0].Message);
        Assert.Equal(("Ann", MessageRecipientKind.Assignees), (planned[0].To.DisplayName, planned[0].As));
        Assert.Same(toDepartment, planned[1].Message);
        Assert.Equal("Bob", planned[1].To.DisplayName);

        // Two messages to the same role: the first by name wins, whatever the order they come in.
        var second = Message("b second", MessageCondition.Always, MessageRecipientKind.Requestee);
        var first = Message("A first", MessageCondition.Always, MessageRecipientKind.Requestee);
        Assert.Same(first, MessageRules.Plan(e, [second, first], People).Single().Message);
        // One message naming a role twice, or two roles holding the same person, still sends one email.
        var both = Message("Both", MessageCondition.Always, MessageRecipientKind.Requestee, MessageRecipientKind.Assignees, MessageRecipientKind.Assignees);
        Assert.Equal(MessageRecipientKind.Assignees, MessageRules.Plan(e, [both], People).Single().As);
    }

    /// <summary>MSG-007: someone another event of the same save has told isn't told again.</summary>
    [Fact]
    public void People_told_earlier_in_the_save_are_skipped()
    {
        var e = Event(MessageTrigger.RequesteeNamed, Me, MessageRules.AlwaysHolds, (MessageRecipientKind.Requestee, [Ann]), (MessageRecipientKind.Assignees, [Ann, Bob]));
        var messages = new[] { Message("Named", MessageCondition.Always, MessageRecipientKind.Requestee, MessageRecipientKind.Assignees) };
        Assert.Equal(["Bob"], MessageRules.Plan(e, messages, People, new HashSet<Guid> { Ann }).Select(p => p.To.DisplayName));
    }

    /// <summary>MSG-008: a message whose condition doesn't hold sends nothing, and a recipient role the trigger doesn't offer is ignored.</summary>
    [Fact]
    public void Conditions_and_offered_roles_decide_what_is_sent()
    {
        var unassigned = Message("Unassigned", MessageCondition.Unassigned, MessageRecipientKind.DepartmentMembers);
        var assigned = Event(MessageTrigger.TaskCreated, null, MessageRules.TaskConditions(true), (MessageRecipientKind.DepartmentMembers, [Ann]));
        Assert.Empty(MessageRules.Plan(assigned, [unassigned], People));
        var nobody = Event(MessageTrigger.TaskCreated, null, MessageRules.TaskConditions(false), (MessageRecipientKind.DepartmentMembers, [Ann]));
        Assert.Single(MessageRules.Plan(nobody, [unassigned], People));

        // Department members aren't a recipient of a request's events, whatever a row says.
        var stray = Message("Stray", MessageCondition.Always, MessageRecipientKind.DepartmentMembers, MessageRecipientKind.Requester);
        var finished = Event(MessageTrigger.RequestFinished, null, MessageRules.FinishedConditions(RequestStatus.Completed),
            (MessageRecipientKind.DepartmentMembers, [Ann]), (MessageRecipientKind.Requester, [Bob]));
        Assert.Equal(["Bob"], MessageRules.Plan(finished, [stray], People).Select(p => p.To.DisplayName));
        Assert.Equal([MessageRecipientKind.Requester], MessageRules.Wanted(MessageTrigger.RequestFinished, [stray]));
    }

    /// <summary>MSG-009: the subject and body are filled in, the recipient's name per email; an unknown token renders as nothing; the subject fits a header.</summary>
    [Fact]
    public void Render_fills_in_the_values_and_cleans_the_subject()
    {
        var message = new MessageSpec(Guid.NewGuid(), "Fallback", MessageCondition.Always, [MessageRecipientKind.Assignees],
            "[Orbit] {{task.title}}{{nope}}", "Hi {{recipient.name}},\r\n\r\n{{actor.name}} assigned you {{task.title}}.\r\n");
        var e = Event(MessageTrigger.TaskCreated, null, MessageRules.AlwaysHolds, (MessageRecipientKind.Assignees, [Ann]));
        var (subject, body) = MessageRules.Render(MessageRules.Plan(e, [message], People).Single(), e);
        Assert.Equal("[Orbit] Fix the printer", subject);
        Assert.Equal("Hi Ann,\n\nMe assigned you Fix the printer.", body);

        Assert.Equal("Two lines and tabs", MessageRules.CleanSubject("Two\r\nlines \t and\ttabs ", "x"));
        Assert.Equal("Fallback", MessageRules.CleanSubject("  \n ", " Fallback "));
        Assert.Equal(MessageRules.SubjectMaxLength, MessageRules.CleanSubject(new string('s', 500), "x").Length);
        Assert.Equal("abc…", MessageRules.Clip("abcdef", 3));
        Assert.Null(MessageRules.Clip(null));
    }

    /// <summary>MSG-010: the HTML body encodes everything, links http(s) addresses only, keeps line breaks and makes paragraphs of blank lines.</summary>
    [Fact]
    public void Html_is_encoded_with_links_breaks_and_paragraphs()
    {
        Assert.Equal("<p>Hi &lt;script&gt;alert(1)&lt;/script&gt; &amp; you,<br>\nsecond line</p>\n<p>third</p>\n",
            MessageRules.ToHtml("Hi <script>alert(1)</script> & you,\r\nsecond line\r\n\r\n \n\nthird\n\n"));
        Assert.Equal("<p>See <a href=\"https://x.org/a?b=1&amp;c=2\">https://x.org/a?b=1&amp;c=2</a>.</p>\n",
            MessageRules.ToHtml("See https://x.org/a?b=1&c=2."));
        Assert.Equal("<p>javascript:alert(1)</p>\n", MessageRules.ToHtml("javascript:alert(1)"));
        Assert.Equal("<p>&quot;quoted&quot; &#39;text&#39;</p>\n", MessageRules.ToHtml("\"quoted\" 'text'"));
        Assert.Equal(string.Empty, MessageRules.ToHtml("  \n\n "));
        Assert.Equal(string.Empty, MessageRules.ToHtml(null));
    }

    /// <summary>MSG-011: when a task is due, in words.</summary>
    [Fact]
    public void Due_when_says_today_tomorrow_or_in_days()
    {
        var today = new DateOnly(2026, 10, 14);
        Assert.Equal("today", MessageRules.DueWhen(today, today));
        Assert.Equal("today", MessageRules.DueWhen(today.AddDays(-2), today));
        Assert.Equal("tomorrow", MessageRules.DueWhen(today.AddDays(1), today));
        Assert.Equal("in 3 days", MessageRules.DueWhen(today.AddDays(3), today));
    }

    /// <summary>MSG-012: a failed email is retried after 1, 5, 15 and 60 minutes, then every 4 hours, until the attempts run out.</summary>
    [Fact]
    public void Retries_back_off_then_give_up()
    {
        var now = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(1), MessageRules.RetryAt(1, 6, now));
        Assert.Equal(now.AddMinutes(5), MessageRules.RetryAt(2, 6, now));
        Assert.Equal(now.AddMinutes(15), MessageRules.RetryAt(3, 6, now));
        Assert.Equal(now.AddMinutes(60), MessageRules.RetryAt(4, 6, now));
        Assert.Equal(now.AddMinutes(240), MessageRules.RetryAt(5, 6, now));
        Assert.Equal(now.AddMinutes(240), MessageRules.RetryAt(7, 9, now));
        Assert.Null(MessageRules.RetryAt(6, 6, now));
        Assert.Null(MessageRules.RetryAt(1, 1, now));
    }

    /// <summary>MSG-009: the preview fills every token of a trigger with its sample, so a valid message never previews a blank.</summary>
    [Fact]
    public void Samples_cover_every_token()
    {
        foreach (var t in MessageCatalogue.All)
        {
            var samples = MessageRules.Samples(t.Trigger);
            Assert.All(t.Tokens, token => Assert.False(string.IsNullOrEmpty(samples[token.Name])));
        }
    }
}
