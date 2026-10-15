using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Services;
using Orbit.Auth;
using Orbit.Data.Entities;

namespace Orbit.Pages.Admin.Messages;

/// <summary>The messages (spec §6.7, Admin &gt; Messages): the emails Orbit sends, by the event each is sent on.</summary>
public class IndexModel(MessageTemplateService messages, IEmailSender sender) : OrbitPageModel
{
    public IReadOnlyList<MessageTemplate> Items { get; private set; } = [];
    public IReadOnlyDictionary<Guid, int> Sent { get; private set; } = new Dictionary<Guid, int>();
    /// <summary>No mail server yet: emails are written to the log, not sent.</summary>
    public bool OnlyLogged => sender is LoggingEmailSender;

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

    public IReadOnlyList<MessageTemplate> For(MessageTrigger trigger) => Items.Where(m => m.Trigger == trigger).ToList();
}
