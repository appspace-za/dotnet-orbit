using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using Orbit.Application.Requests;
using Orbit.Data.Entities;

namespace Orbit.Application.Messages;

/// <summary>A message as the rules see it (§6.7): when it goes, to whom - its recipient roles and the addresses it lists - and its templates.</summary>
public sealed record MessageSpec(Guid Id, string Name, MessageCondition Condition, IReadOnlyCollection<MessageRecipientKind> Recipients,
    IReadOnlyList<MessageAddress> Addresses, string Subject, string Body);

/// <summary>Someone a message might go to, as far as the rules need to know. No id for an address a message lists.</summary>
public sealed record MessagePerson(Guid? Id, string DisplayName, string? Email, bool IsActive, bool IsSystemAccount);

/// <summary>An address a message lists beside its recipient roles (§6.7), with the name given before it, if any.</summary>
public sealed record MessageAddress(string Address, string? Name)
{
    /// <summary>As stored and shown: <c>helpdesk@example.com</c>, or <c>IT Helpdesk &lt;helpdesk@example.com&gt;</c>, the name quoted when it needs to be.</summary>
    public override string ToString() => Name is null ? Address : new MailAddress(Address, Name).ToString();
}

/// <summary>What a list of addresses held: the addresses, each once, and the entries that aren't addresses, as typed.</summary>
public sealed record MessageAddressList(IReadOnlyList<MessageAddress> Addresses, IReadOnlyList<string> Invalid);

/// <summary>
/// One event that happened (§6.7): what it was, who did it (null for the system or no person), the conditions that hold, the people
/// in each recipient role, and the values its tokens render to (all but <c>recipient.name</c>, which is per email).
/// </summary>
public sealed record MessageEvent(
    MessageTrigger Trigger,
    Guid? ActorUserId,
    IReadOnlySet<MessageCondition> Holds,
    IReadOnlyDictionary<MessageRecipientKind, IReadOnlyCollection<Guid>> People,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>One email the rules decided on: which message, to whom, and in which role they get it - none for an address the message lists.</summary>
public sealed record PlannedEmail(MessageSpec Message, MessagePerson To, MessageRecipientKind? As);

/// <summary>
/// Messages (spec §6.7): which messages an event sends, to whom, and what they say. No database access - the notification service
/// gathers the event's facts and people, these rules decide, and the service queues what they decide.
/// </summary>
public static partial class MessageRules
{
    public const int NameMaxLength = 100;
    public const int SubjectMaxLength = 300;
    public const int BodyMaxLength = 8000;

    /// <summary>The longest a long value (a description, a comment, a script's output) is let into an email.</summary>
    public const int LongValueMaxLength = 4000;

    /// <summary>The most addresses one message may list, and the longest the list may be as stored.</summary>
    public const int MaxAddresses = 20;
    public const int AddressesMaxLength = 2000;

    /// <summary>The waits between attempts to send an email, in minutes; the last repeats until the attempts run out.</summary>
    private static readonly int[] RetryMinutes = [1, 5, 15, 60, 240];

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // ---------------------------------------------------------------- which conditions hold

    public static IReadOnlySet<MessageCondition> AlwaysHolds { get; } = new HashSet<MessageCondition> { MessageCondition.Always };

    /// <summary>MSG-003: a task created or falling due - with assignees or with none.</summary>
    public static IReadOnlySet<MessageCondition> TaskConditions(bool assigned) =>
        Holding(assigned ? MessageCondition.Assigned : MessageCondition.Unassigned);

    /// <summary>MSG-003: a task closed as Done or Cancelled.</summary>
    public static IReadOnlySet<MessageCondition> ClosedConditions(TaskItemStatus status) => status switch
    {
        TaskItemStatus.Done => Holding(MessageCondition.Done),
        TaskItemStatus.Cancelled => Holding(MessageCondition.Cancelled),
        _ => AlwaysHolds
    };

    /// <summary>
    /// MSG-003: who wrote a comment - one of the task's assignees, its requestee (both, when they are both), or anyone else: another
    /// person, Claude, or the system (no author).
    /// </summary>
    public static IReadOnlySet<MessageCondition> CommentConditions(Guid? authorId, IReadOnlyCollection<Guid> assigneeIds, Guid? requesteeId)
    {
        var byAssignee = authorId is Guid a && assigneeIds.Contains(a);
        var byRequestee = authorId is not null && authorId == requesteeId;
        var holds = new HashSet<MessageCondition> { MessageCondition.Always };
        if (byAssignee) holds.Add(MessageCondition.ByAssignee);
        if (byRequestee) holds.Add(MessageCondition.ByRequestee);
        if (!byAssignee && !byRequestee) holds.Add(MessageCondition.ByOther);
        return holds;
    }

    /// <summary>MSG-014: the status a project was given - made active again, put on hold, completed or archived.</summary>
    public static IReadOnlySet<MessageCondition> ProjectStatusConditions(ProjectStatus to) => to switch
    {
        ProjectStatus.Active => Holding(MessageCondition.Reopened),
        ProjectStatus.OnHold => Holding(MessageCondition.OnHold),
        ProjectStatus.Completed => Holding(MessageCondition.Completed),
        ProjectStatus.Archived => Holding(MessageCondition.Archived),
        _ => AlwaysHolds
    };

    /// <summary>MSG-014: a project's comment is by its owner or by someone else - another person, or Claude.</summary>
    public static IReadOnlySet<MessageCondition> ProjectCommentConditions(Guid? authorId, Guid ownerId) =>
        Holding(authorId == ownerId ? MessageCondition.ByOwner : MessageCondition.ByOther);

    /// <summary>MSG-003: an approval step approved or declined.</summary>
    public static IReadOnlySet<MessageCondition> ApprovalConditions(bool approved) =>
        Holding(approved ? MessageCondition.Approved : MessageCondition.Declined);

    /// <summary>MSG-003: an action that succeeded or failed.</summary>
    public static IReadOnlySet<MessageCondition> ActionConditions(bool succeeded) =>
        Holding(succeeded ? MessageCondition.Succeeded : MessageCondition.Failed);

    /// <summary>MSG-003: how a request finished.</summary>
    public static IReadOnlySet<MessageCondition> FinishedConditions(RequestStatus status) => status switch
    {
        RequestStatus.Completed => Holding(MessageCondition.Completed),
        RequestStatus.Declined => Holding(MessageCondition.Declined),
        RequestStatus.Cancelled => Holding(MessageCondition.Cancelled),
        _ => AlwaysHolds
    };

    private static IReadOnlySet<MessageCondition> Holding(MessageCondition condition) =>
        new HashSet<MessageCondition> { MessageCondition.Always, condition };

    // ---------------------------------------------------------------- who gets what

    /// <summary>MSG-008: the messages whose condition holds for the event.</summary>
    public static IReadOnlyList<MessageSpec> Matching(MessageEvent e, IEnumerable<MessageSpec> messages) =>
        messages.Where(m => e.Holds.Contains(m.Condition)).ToList();

    /// <summary>The recipient roles the matching messages ask for that the trigger offers - so the service fetches only those people.</summary>
    public static IReadOnlySet<MessageRecipientKind> Wanted(MessageTrigger trigger, IEnumerable<MessageSpec> matching)
    {
        var definition = MessageCatalogue.Get(trigger);
        return matching.SelectMany(m => m.Recipients).Where(definition.Offers).ToHashSet();
    }

    /// <summary>MSG-005: someone an email can go to - an active person, not the Claude user, with an address.</summary>
    public static bool CanReceive(MessagePerson person) =>
        person.IsActive && !person.IsSystemAccount && !string.IsNullOrWhiteSpace(person.Email);

    /// <summary>
    /// MSG-004, MSG-006, MSG-007, MSG-008, MSG-020: the emails an event sends. Only messages whose condition holds, to the recipient
    /// roles the trigger offers - never to whoever caused the event (nobody needs telling what they just did, and an event of the system
    /// or an API key leaves nobody out), and only to people who can receive one - and to the addresses each lists, which the admin chose
    /// and get it whoever acted. Never to an address in <paramref name="alreadyTold"/> (told by another event of the same save, compared
    /// as <see cref="AddressKey"/>), and at most one email per address: when several would reach it, the one whose recipient role ranks
    /// first (the most personal; a listed address last), then by message name, then by id. People not in <paramref name="people"/> are skipped.
    /// </summary>
    public static IReadOnlyList<PlannedEmail> Plan(MessageEvent e, IEnumerable<MessageSpec> messages,
        IReadOnlyDictionary<Guid, MessagePerson> people, IReadOnlySet<string>? alreadyTold = null)
    {
        var definition = MessageCatalogue.Get(e.Trigger);
        var matching = Matching(e, messages);
        var byRole =
            from message in matching
            from kind in message.Recipients.Distinct().Where(definition.Offers)
            from id in e.People.GetValueOrDefault(kind) ?? []
            where id != e.ActorUserId
            let person = people.GetValueOrDefault(id)
            where person is not null && CanReceive(person)
            select new PlannedEmail(message, person, kind);
        var listed =
            from message in matching
            from address in message.Addresses
            select new PlannedEmail(message, new MessagePerson(null, address.Name ?? address.Address, address.Address, true, false), null);
        return byRole.Concat(listed)
            .Where(p => alreadyTold is null || !alreadyTold.Contains(AddressKey(p.To.Email!)))
            .GroupBy(p => AddressKey(p.To.Email!))
            .Select(g => g.OrderBy(Rank)
                .ThenBy(p => p.Message.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Message.Id)
                .First())
            .OrderBy(Rank).ThenBy(p => p.To.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(p => AddressKey(p.To.Email!))
            .ToList();
    }

    /// <summary>A recipient role's rank, the declaration order of <see cref="MessageRecipientKind"/>; an address the message lists ranks last.</summary>
    private static int Rank(PlannedEmail email) => email.As is { } kind ? (int)kind : int.MaxValue;

    /// <summary>An email address as it is compared: one mailbox is one recipient, however it is written.</summary>
    public static string AddressKey(string address) => address.Trim().ToLowerInvariant();

    // ---------------------------------------------------------------- the addresses a message lists

    /// <summary>
    /// MSG-019: the addresses in what was typed - separated by <c>;</c> or line breaks (not commas, which a quoted name may hold), each
    /// <c>someone@example.com</c> or <c>Name &lt;someone@example.com&gt;</c>, blanks skipped and each address once, ignoring case - and the
    /// entries that aren't addresses.
    /// </summary>
    public static MessageAddressList ParseAddresses(string? text)
    {
        var addresses = new List<MessageAddress>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in (text ?? string.Empty).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A bare entry must be the address and nothing else; one with a name must put the address in angle brackets.
            if (!MailAddress.TryCreate(entry, out var parsed) || (!entry.Contains('<') && parsed.Address != entry))
            {
                invalid.Add(entry);
                continue;
            }
            if (!seen.Add(AddressKey(parsed.Address))) continue;
            addresses.Add(new MessageAddress(parsed.Address, string.IsNullOrWhiteSpace(parsed.DisplayName) ? null : parsed.DisplayName.Trim()));
        }
        return new MessageAddressList(addresses, invalid);
    }

    /// <summary>MSG-019: the addresses as stored and shown, <c>; </c> between them; null for none.</summary>
    public static string? FormatAddresses(IEnumerable<MessageAddress> addresses)
    {
        var text = string.Join("; ", addresses.Select(a => a.ToString()));
        return text.Length == 0 ? null : text;
    }

    // ---------------------------------------------------------------- what it says

    /// <summary>MSG-009: the email's subject and plain-text body, with the event's values and the recipient's name filled in.</summary>
    public static (string Subject, string Body) Render(PlannedEmail email, MessageEvent e)
    {
        string? Value(string token) => token == "recipient.name" ? email.To.DisplayName : e.Values.GetValueOrDefault(token);
        var subject = CleanSubject(RequestTokenRules.Render(email.Message.Subject, Value), email.Message.Name);
        var body = Normalise(RequestTokenRules.Render(email.Message.Body, Value));
        return (subject, body);
    }

    /// <summary>
    /// MSG-009: a subject fit for a mail header - control characters (a line break in a task's title) become spaces, runs of
    /// whitespace one, at most <see cref="SubjectMaxLength"/> characters; the fallback when nothing is left.
    /// </summary>
    public static string CleanSubject(string? rendered, string fallback)
    {
        var text = new string((rendered ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        text = Whitespace().Replace(text, " ").Trim();
        if (text.Length == 0) text = fallback.Trim();
        return text.Length > SubjectMaxLength ? text[..SubjectMaxLength].TrimEnd() : text;
    }

    /// <summary>A template or body with Windows line breaks made plain ones and trailing whitespace dropped.</summary>
    public static string Normalise(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>
    /// MSG-010: the HTML of a plain-text body - everything encoded, so nothing a task's title or a comment holds can become markup;
    /// http and https addresses made links (<see cref="LinkRules"/>); a blank line starts a paragraph and a line break is kept.
    /// </summary>
    public static string ToHtml(string? text)
    {
        var html = new StringBuilder();
        foreach (var paragraph in ParagraphBreak().Split(Normalise(text)))
        {
            if (string.IsNullOrWhiteSpace(paragraph)) continue;
            html.Append("<p>");
            var lines = paragraph.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) html.Append("<br>\n");
                foreach (var segment in LinkRules.Split(lines[i]))
                {
                    var encoded = WebUtility.HtmlEncode(segment.Text);
                    html.Append(segment.IsLink ? $"<a href=\"{encoded}\">{encoded}</a>" : encoded);
                }
            }
            html.Append("</p>\n");
        }
        return html.ToString();
    }

    /// <summary>A long value cut to <paramref name="max"/> characters, with an ellipsis when cut; null stays null.</summary>
    public static string? Clip(string? value, int max = LongValueMaxLength) =>
        value is null || value.Length <= max ? value : value[..max].TrimEnd() + "…";

    /// <summary>MSG-011: when a task is due, in words: today (or overdue), tomorrow, or in so many days.</summary>
    public static string DueWhen(DateOnly due, DateOnly today)
    {
        var days = due.DayNumber - today.DayNumber;
        return days <= 0 ? "today" : days == 1 ? "tomorrow" : $"in {days} days";
    }

    /// <summary>The preview's values: each token's sample.</summary>
    public static IReadOnlyDictionary<string, string?> Samples(MessageTrigger trigger) =>
        MessageCatalogue.Get(trigger).Tokens.ToDictionary(t => t.Name, t => (string?)t.Sample, StringComparer.Ordinal);

    // ---------------------------------------------------------------- sending

    /// <summary>
    /// MSG-012: when to try a failed email again, after <paramref name="attempts"/> tries - 1, 5, 15, 60 minutes, then every 4 hours -
    /// or null once <paramref name="maxAttempts"/> have been made, when it is given up on.
    /// </summary>
    public static DateTime? RetryAt(int attempts, int maxAttempts, DateTime now) =>
        attempts >= maxAttempts ? null : now.AddMinutes(RetryMinutes[Math.Clamp(attempts - 1, 0, RetryMinutes.Length - 1)]);

    // ---------------------------------------------------------------- saving a message

    /// <summary>
    /// MSG-002, MSG-019: refuses a message that can't be sent as written - a missing or too long name, subject or body; a condition or
    /// recipient the trigger doesn't offer; an entry in <paramref name="addresses"/> that isn't an email address, or too many of them;
    /// neither a recipient nor an address; or a token that isn't one of the trigger's values. Returns the addresses listed.
    /// </summary>
    public static IReadOnlyList<MessageAddress> Validate(MessageTrigger trigger, MessageCondition condition, IReadOnlyCollection<MessageRecipientKind> recipients,
        string? name, string? subject, string? body, string? addresses = null)
    {
        var definition = MessageCatalogue.Get(trigger);
        Require(name, "A name", NameMaxLength);
        Require(subject, "A subject", SubjectMaxLength);
        Require(body, "A body", BodyMaxLength);
        if (!definition.Offers(condition))
            throw new ValidationException($"\"{condition.Label()}\" isn't a condition of {definition.Label}.");
        foreach (var kind in recipients.Where(k => !definition.Offers(k)))
            throw new ValidationException($"\"{kind.Label()}\" isn't a recipient of {definition.Label}.");
        var listed = ParseAddresses(addresses);
        if (listed.Invalid.Count > 0)
            throw new ValidationException($"\"{listed.Invalid[0]}\" isn't an email address. Separate addresses with ;.");
        if (listed.Addresses.Count > MaxAddresses)
            throw new ValidationException($"List at most {MaxAddresses} addresses.");
        if ((FormatAddresses(listed.Addresses)?.Length ?? 0) > AddressesMaxLength)
            throw new ValidationException($"The addresses can be at most {AddressesMaxLength} characters.");
        if (recipients.Count == 0 && listed.Addresses.Count == 0)
            throw new ValidationException("Choose at least one recipient, or list an address.");
        var tokens = definition.Tokens.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        CheckTokens(subject, "The subject", tokens, definition);
        CheckTokens(body, "The body", tokens, definition);
        return listed.Addresses;
    }

    private static void Require(string? value, string what, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ValidationException($"{what} is required.");
        if (value.Trim().Length > max) throw new ValidationException($"{what} can be at most {max} characters.");
    }

    private static void CheckTokens(string? template, string what, IReadOnlySet<string> tokens, MessageTriggerDefinition definition)
    {
        foreach (var token in RequestTokenRules.Parse(template).Where(t => !tokens.Contains(t)))
            throw new ValidationException($"{what} uses {{{{{token}}}}}, which isn't a value of {definition.Label}.");
    }
}
