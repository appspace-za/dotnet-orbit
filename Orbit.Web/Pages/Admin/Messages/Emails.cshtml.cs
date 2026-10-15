using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Admin.Messages;

/// <summary>The outbox (spec §6.7): the emails messages have written, newest first - sent, waiting, or given up on.</summary>
public class EmailsModel(MessageTemplateService messages, IOptions<EmailOptions> options) : OrbitPageModel
{
    public const int Shown = 200;

    [BindProperty(SupportsGet = true)] public OutboundEmailStatus? Status { get; set; }
    public IReadOnlyList<OutboundEmail> Items { get; private set; } = [];
    public int KeepDays => options.Value.KeepDays;

    public async Task OnGetAsync(CancellationToken ct) => Items = await messages.RecentEmailsAsync(Status, Shown, ct);

    public static string StatusBadge(OutboundEmailStatus s) => s switch
    {
        OutboundEmailStatus.Sent => "text-bg-success",
        OutboundEmailStatus.Failed => "text-bg-danger",
        _ => "text-bg-secondary"
    };
}
