using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Auth;
using Orbit.Data.Entities;

namespace Orbit.Pages.Admin.Messages;

/// <summary>The messages (spec §6.7, Admin &gt; Messages): the emails Orbit sends, by the event each is sent on.</summary>
public class IndexModel(MessageTemplateService messages, IEmailSender sender, IOptions<EmailOptions> email) : OrbitPageModel
{
    public IReadOnlyList<MessageTemplate> Items { get; private set; } = [];
    public IReadOnlyDictionary<Guid, int> Sent { get; private set; } = new Dictionary<Guid, int>();
    /// <summary>No mail server yet: emails are written to the log, not sent.</summary>
    public bool OnlyLogged => sender is LoggingEmailSender;
    /// <summary>Who the emails are from, when they are sent.</summary>
    public EmailOptions Email => email.Value;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Items = await messages.ListAsync(ct);
        Sent = await messages.SentCountsAsync(ct);
    }

    public async Task<IActionResult> OnPostEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var message = await messages.SetEnabledAsync(id, enabled, ct);
        Success($"\"{message.Name}\" is {(enabled ? "on" : "off")}.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestEmailAsync(CancellationToken ct)
    {
        var address = await messages.SendTestEmailAsync(ct);
        Success(OnlyLogged
            ? $"A test email to {address} was written to the log: no mail server is set up, so nothing was sent."
            : $"A test email went to {address}. If it doesn't arrive, look in Brevo's transactional logs.");
        return RedirectToPage();
    }

    public IReadOnlyList<MessageTemplate> For(MessageTrigger trigger) => Items.Where(m => m.Trigger == trigger).ToList();
}
