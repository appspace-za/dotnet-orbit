using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data;
using Orbit.Data.Entities;
using Quartz;

namespace Orbit.Jobs;

/// <summary>
/// Quartz.NET job: raises Task due soon (§6.7) for each open task due within the configured lead time, whose messages say who is
/// emailed. Scheduled by <see cref="QuartzJobRegistration"/> from Jobs:DueDateNotifications:Cron.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DueDateNotificationJob(
    ApplicationDbContext db,
    NotificationService notifications,
    IOptions<JobOptions> options,
    ILogger<DueDateNotificationJob> logger) : IJob
{
    public static readonly JobKey Key = new("due-date-notifications", "orbit");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var horizon = today.AddDays(Math.Max(0, options.Value.DueDateNotifications.LeadDays));
            var due = await db.Tasks
                .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled
                    && t.DueSoonNotifiedAt == null
                    && t.DueDate != null && t.DueDate >= today && t.DueDate <= horizon)
                .ToListAsync(ct);

            // The task is stamped once anyone is told: someone assigned later isn't reminded again. A task nobody was told of - no
            // message for it, or nobody to send one to - is looked at again next run, so an assignee added later still is (§6.2.3).
            var queued = 0;
            foreach (var task in due)
            {
                var told = await notifications.TaskDueSoonAsync(task, today, ct);
                if (told > 0) task.DueSoonNotifiedAt = DateTime.UtcNow;
                queued += told;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Due-date notification run complete: {Count} email(s) for {Tasks} task(s) due soon. Next fire: {Next}",
                queued, due.Count, context.NextFireTimeUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Due-date notification run failed.");
            throw new JobExecutionException(ex);
        }
    }
}
