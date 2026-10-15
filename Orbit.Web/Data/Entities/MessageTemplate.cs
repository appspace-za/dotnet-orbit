namespace Orbit.Data.Entities;

/// <summary>
/// A message (spec §6.7): an email Orbit sends when an event happens - its trigger, the one condition it waits for, whom it goes to,
/// and its subject and body, written as plain text with <c>{{tokens}}</c> for the event's values. Managed under Admin &gt; Messages by
/// someone holding messages.manage. A trigger may have any number of messages; a person gets at most one email per event.
/// </summary>
public class MessageTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Fixed once the message is created: the condition, recipients and tokens it may use all depend on it.</summary>
    public MessageTrigger Trigger { get; set; }
    public MessageCondition Condition { get; set; } = MessageCondition.Always;
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Addresses it also goes to, beside its recipients' roles - a shared mailbox, a supplier, someone in no role - as
    /// <c>Name &lt;address&gt;; address</c>; null for none. They get it whenever its condition holds.
    /// </summary>
    public string? Addresses { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MessageTemplateRecipient> Recipients { get; set; } = new List<MessageTemplateRecipient>();
}

/// <summary>One of the roles a message goes to (§6.7): the assignees, the requestee, the department's members, and so on.</summary>
public class MessageTemplateRecipient
{
    public Guid MessageTemplateId { get; set; }
    public MessageTemplate MessageTemplate { get; set; } = null!;
    public MessageRecipientKind Kind { get; set; }
}
