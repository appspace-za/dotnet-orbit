using Microsoft.AspNetCore.Identity.UI.Services;

namespace Orbit.Auth;

/// <summary>
/// The sender when <c>Email:Provider</c> is Log, the default (spec §6.7): account and message emails are written to the log, and
/// nothing leaves Orbit. <see cref="BrevoEmailSender"/>, <see cref="MailjetEmailSender"/> and <see cref="ResendEmailSender"/> send
/// them for real.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        logger.LogInformation("Email (not sent - no sender configured) To: {To} | Subject: {Subject}\n{Body}",
            email, subject, htmlMessage);
        return Task.CompletedTask;
    }
}
