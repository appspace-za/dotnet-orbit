using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Messages;
using Orbit.Data;
using Orbit.Data.Entities;
using Orbit.Jobs;

namespace Orbit.Application.Services;

/// <summary>
/// Sends the outbox (spec §6.7): each email that is due goes through Identity's <see cref="IEmailSender"/>, as HTML made from its plain
/// text. A failure is tried again later (<see cref="MessageRules.RetryAt"/>) until the attempts run out, when the email is marked
/// Failed. Run by <see cref="EmailDispatchJob"/>; nothing here checks permissions.
/// </summary>
public sealed class EmailDispatcher(
    ApplicationDbContext db,
    IEmailSender sender,
    IOptions<EmailOptions> options,
    ILogger<EmailDispatcher> logger)
{
    /// <summary>How long a claimed email is left to its try before another run may take it (a try cut short by a restart).</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);
    private static DateTime _lastPurge = DateTime.MinValue;

    /// <summary>Send the emails that are due, oldest first, and now and then clear old ones out. Returns how many were sent and how many given up on.</summary>
    public async Task<(int Sent, int Failed)> DispatchAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        var now = DateTime.UtcNow;
        var due = await db.OutboundEmails.AsNoTracking()
            .Where(e => e.Status == OutboundEmailStatus.Pending && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt).ThenBy(e => e.CreatedAt)
            .Select(e => e.Id).Take(Math.Max(1, settings.BatchSize)).ToListAsync(ct);

        int sent = 0, failed = 0;
        foreach (var id in due)
        {
            var started = DateTime.UtcNow;
            // The claim: a run that finds the email already taken, or sent, leaves it.
            var claimed = await db.OutboundEmails
                .Where(e => e.Id == id && e.Status == OutboundEmailStatus.Pending && e.NextAttemptAt <= started)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.NextAttemptAt, started + Lease)
                    .SetProperty(e => e.Attempts, e => e.Attempts + 1), ct);
            if (claimed == 0) continue;
            var email = await db.OutboundEmails.AsNoTracking().FirstAsync(e => e.Id == id, ct);

            try
            {
                await sender.SendEmailAsync(email.ToAddress, email.Subject, MessageRules.ToHtml(email.Body));
                await db.OutboundEmails.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, OutboundEmailStatus.Sent)
                    .SetProperty(e => e.SentAt, DateTime.UtcNow)
                    .SetProperty(e => e.LastError, (string?)null), ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var retry = MessageRules.RetryAt(email.Attempts, Math.Max(1, settings.MaxAttempts), DateTime.UtcNow);
                var error = MessageRules.Clip(ex.Message, 990);
                await db.OutboundEmails.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, retry is null ? OutboundEmailStatus.Failed : OutboundEmailStatus.Pending)
                    .SetProperty(e => e.NextAttemptAt, retry ?? DateTime.UtcNow)
                    .SetProperty(e => e.LastError, error), ct);
                if (retry is null)
                {
                    failed++;
                    logger.LogError(ex, "Email to {To} ({Subject}) failed {Attempts} time(s) and was given up on.", email.ToAddress, email.Subject, email.Attempts);
                }
                else
                {
                    logger.LogWarning(ex, "Email to {To} ({Subject}) failed; trying again at {Retry:u}.", email.ToAddress, email.Subject, retry);
                }
            }
        }

        if (DateTime.UtcNow - _lastPurge >= PurgeEvery)
        {
            _lastPurge = DateTime.UtcNow;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, settings.KeepDays));
            var purged = await db.OutboundEmails
                .Where(e => e.Status != OutboundEmailStatus.Pending && e.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);
            if (purged > 0) logger.LogInformation("Cleared {Count} email(s) older than {Days} days from the outbox.", purged, settings.KeepDays);
        }
        return (sent, failed);
    }
}
