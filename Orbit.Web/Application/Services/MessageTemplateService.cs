using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Messages;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// The messages (spec §6.7, Admin &gt; Messages): the emails Orbit sends on events, kept by whoever holds messages.manage, and the
/// outbox of what was sent. What a trigger offers is <see cref="MessageCatalogue"/>; the rules a message must keep are
/// <see cref="MessageRules.Validate"/>.
/// </summary>
public sealed class MessageTemplateService(
    ApplicationDbContext db,
    IActorProvider actors,
    AuditService audit,
    IEmailSender sender,
    IOptions<EmailOptions> emailOptions,
    ILogger<MessageTemplateService> logger)
{
    public async Task<IReadOnlyList<MessageTemplate>> ListAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await db.MessageTemplates.AsNoTracking().Include(m => m.Recipients)
            .OrderBy(m => m.Name).ToListAsync(ct);
    }

    public async Task<MessageTemplate> GetAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await db.MessageTemplates.AsNoTracking().Include(m => m.Recipients).FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Message not found.");
    }

    /// <summary>How many emails each message has written that are still in the outbox, by message.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> SentCountsAsync(CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await db.OutboundEmails.AsNoTracking().Where(e => e.MessageTemplateId != null)
            .GroupBy(e => e.MessageTemplateId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<MessageTemplate> CreateAsync(MessageTrigger trigger, MessageTemplateInput input, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        if (!Enum.IsDefined(trigger)) throw new ValidationException("Choose what the message is sent on.");
        var (name, subject, body, recipients) = Clean(trigger, input);
        await RequireUniqueNameAsync(name, null, ct);

        var now = DateTime.UtcNow;
        var message = new MessageTemplate
        {
            Name = name, Trigger = trigger, Condition = input.Condition, Enabled = input.Enabled, Subject = subject, Body = body,
            CreatedAt = now, UpdatedAt = now
        };
        foreach (var kind in recipients)
            message.Recipients.Add(new MessageTemplateRecipient { MessageTemplateId = message.Id, Kind = kind });
        db.MessageTemplates.Add(message);
        audit.Add(actor, AuditEntity.Message, message.Id, AuditAction.Created, null, message.Name, new
        {
            message.Name, trigger, message.Condition, recipients, message.Enabled, message.Subject, message.Body
        });
        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task<MessageTemplate> UpdateAsync(Guid id, MessageTemplateInput input, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var message = await db.MessageTemplates.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Message not found.");
        var (name, subject, body, recipients) = Clean(message.Trigger, input);
        if (!string.Equals(name, message.Name, StringComparison.OrdinalIgnoreCase)) await RequireUniqueNameAsync(name, id, ct);

        var changes = new ChangeSet()
            .TrackText("name", message.Name, name)
            .Track("condition", message.Condition, input.Condition)
            .Track("recipients", Describe(message.Recipients.Select(r => r.Kind)), Describe(recipients))
            .Track("enabled", message.Enabled, input.Enabled)
            .TrackText("subject", message.Subject, subject)
            .TrackText("body", message.Body, body);
        if (!changes.HasChanges) return message;

        message.Name = name;
        message.Condition = input.Condition;
        message.Enabled = input.Enabled;
        message.Subject = subject;
        message.Body = body;
        message.UpdatedAt = DateTime.UtcNow;
        var keep = recipients.ToHashSet();
        db.MessageTemplateRecipients.RemoveRange(message.Recipients.Where(r => !keep.Contains(r.Kind)));
        foreach (var kind in recipients.Where(k => message.Recipients.All(r => r.Kind != k)))
            db.MessageTemplateRecipients.Add(new MessageTemplateRecipient { MessageTemplateId = message.Id, Kind = kind });
        audit.Add(actor, AuditEntity.Message, message.Id, AuditAction.Updated, null, message.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>Switch a message on or off from the list, without opening it.</summary>
    public async Task<MessageTemplate> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var message = await db.MessageTemplates.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Message not found.");
        if (message.Enabled == enabled) return message;
        var changes = new ChangeSet().Track("enabled", message.Enabled, enabled);
        message.Enabled = enabled;
        message.UpdatedAt = DateTime.UtcNow;
        audit.Add(actor, AuditEntity.Message, message.Id, AuditAction.Updated, null, message.Name, changes.Changes);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>A message can always go: the emails it wrote stay in the outbox, under its name.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var message = await db.MessageTemplates.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Message not found.");
        db.MessageTemplates.Remove(message);
        audit.Add(actor, AuditEntity.Message, message.Id, AuditAction.Deleted, null, message.Name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The message as it would read, filled in with the trigger's sample values. Checks it as a save would.</summary>
    public async Task<MessagePreview> PreviewAsync(MessageTrigger trigger, MessageTemplateInput input, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var (name, subject, body, recipients) = Clean(trigger, input);
        var e = new MessageEvent(trigger, null, MessageRules.AlwaysHolds, new Dictionary<MessageRecipientKind, IReadOnlyCollection<Guid>>(),
            MessageRules.Samples(trigger));
        var sample = MessageCatalogue.Get(trigger).Tokens.First(t => t.Name == "recipient.name").Sample;
        var spec = new MessageSpec(Guid.Empty, name, input.Condition, recipients, subject, body);
        var (renderedSubject, renderedBody) = MessageRules.Render(new PlannedEmail(spec, new MessagePerson(Guid.Empty, sample, null, true, false), recipients[0]), e);
        return new MessagePreview(renderedSubject, renderedBody, MessageRules.ToHtml(renderedBody));
    }

    /// <summary>The outbox, newest first: what was sent, what waits to be, and what was given up on.</summary>
    public async Task<IReadOnlyList<OutboundEmail>> RecentEmailsAsync(OutboundEmailStatus? status, int take = 200, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var q = db.OutboundEmails.AsNoTracking();
        if (status is OutboundEmailStatus s) q = q.Where(e => e.Status == s);
        return await q.OrderByDescending(e => e.CreatedAt).ThenBy(e => e.ToName).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
    }

    public async Task<OutboundEmail> GetEmailAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await db.OutboundEmails.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("Email not found.");
    }

    /// <summary>
    /// Send a short email to the caller's own address straight through the sender, not the outbox, so whoever sets up the mail
    /// settings sees at once whether they work. A failure is refused with the sender's reason. Returns the address it went to.
    /// </summary>
    public async Task<string> SendTestEmailAsync(CancellationToken ct = default)
    {
        var actor = await RequireAsync(ct);
        var me = await db.Users.AsNoTracking().Where(u => u.Id == actor.UserId)
            .Select(u => new { u.Email, u.DisplayName }).FirstOrDefaultAsync(ct);
        var address = me?.Email?.Trim();
        if (string.IsNullOrEmpty(address)) throw new ValidationException("Your account has no email address to send a test email to.");

        var how = emailOptions.Value.Provider == EmailProvider.Brevo ? "through Brevo" : "to Orbit's log only";
        var body = $"Hello {me!.DisplayName},\n\nThis is a test email from Orbit, sent {how} at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC from Admin > Messages. " +
                   "If you can read it, Orbit's emails reach you.";
        try
        {
            await sender.SendEmailAsync(address, "Orbit test email", MessageRules.ToHtml(body));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The test email to {To} failed.", address);
            throw new ValidationException($"The test email to {address} failed: {ex.Message}");
        }
        logger.LogInformation("Test email sent to {To} {How}.", address, how);
        return address;
    }

    /// <summary>The input made storable, or refused: a trimmed name, subject and body with plain line breaks, and each recipient once in rank order.</summary>
    private static (string Name, string Subject, string Body, IReadOnlyList<MessageRecipientKind> Recipients) Clean(MessageTrigger trigger, MessageTemplateInput input)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        var subject = input.Subject?.Trim() ?? string.Empty;
        var body = MessageRules.Normalise(input.Body);
        var recipients = input.Recipients.Distinct().Order().ToList();
        MessageRules.Validate(trigger, input.Condition, recipients, name, subject, body);
        return (name, subject, body, recipients);
    }

    private static string Describe(IEnumerable<MessageRecipientKind> kinds) => string.Join(", ", kinds.Order());

    private async Task RequireUniqueNameAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var n = name.ToLowerInvariant();
        if (await db.MessageTemplates.AnyAsync(m => m.Name.ToLower() == n && m.Id != exceptId, ct))
            throw new ValidationException($"There is already a message called \"{name}\".");
    }

    private async Task<Actor> RequireAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageMessages(actor), "You don't have permission to manage messages.");
        return actor;
    }
}
