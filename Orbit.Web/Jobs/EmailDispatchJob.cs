using Orbit.Application.Services;
using Quartz;

namespace Orbit.Jobs;

/// <summary>
/// Quartz.NET job: sends the emails messages have queued in the outbox (spec §6.7), on the schedule Jobs:EmailDispatch:Cron (every 15
/// seconds). One run at a time; each email is claimed before it is sent, so two runs never send it twice.
/// </summary>
[DisallowConcurrentExecution]
public sealed class EmailDispatchJob(EmailDispatcher dispatcher, ILogger<EmailDispatchJob> logger) : IJob
{
    public static readonly JobKey Key = new("email-dispatch", "orbit");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        try
        {
            var (sent, failed) = await dispatcher.DispatchAsync(ct);
            if (sent + failed > 0) logger.LogInformation("Email dispatch sent {Sent} email(s); {Failed} given up on.", sent, failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email dispatch failed.");
            throw new JobExecutionException(ex);
        }
    }
}
