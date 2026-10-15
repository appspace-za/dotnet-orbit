namespace Orbit.Data.Entities;

/// <summary>
/// One email in the outbox (spec §6.7): written, already filled in, in the same save as the change it tells of - so nobody hears of a
/// change that was rolled back - and sent afterwards by the email dispatch job, which retries one that fails. Kept for a while after
/// it is sent, for Admin &gt; Messages to show what went out.
/// </summary>
public class OutboundEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public MessageTrigger Trigger { get; set; }
    /// <summary>The message it was written from; null once that message is deleted, when <see cref="MessageName"/> still says which.</summary>
    public Guid? MessageTemplateId { get; set; }
    public MessageTemplate? MessageTemplate { get; set; }
    public string MessageName { get; set; } = string.Empty;
    /// <summary>What it is about: an audit entity type (Task, Request) and its id.</summary>
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public Guid? ToUserId { get; set; }
    public ApplicationUser? ToUser { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary>Plain text; made HTML when it is sent.</summary>
    public string Body { get; set; } = string.Empty;
    public OutboundEmailStatus Status { get; set; } = OutboundEmailStatus.Pending;
    /// <summary>Tries so far, counted when a try starts.</summary>
    public int Attempts { get; set; }
    /// <summary>When it may next be tried: now for a new one, later after a failure, and a few minutes on while a try is under way.</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
}
