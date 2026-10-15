using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Messages;
using Orbit.Application.Models;
using Orbit.Application.Requests;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Messages;

/// <summary>
/// Writing a message (spec §6.7): its name, the condition it waits for, whom it goes to, and its subject and body with the trigger's
/// values. The trigger is chosen when it is created (<c>?trigger=</c>) and never changes.
/// </summary>
public class EditModel(MessageTemplateService messages) : OrbitPageModel
{
    public sealed class MessageForm
    {
        public string? Name { get; set; }
        public MessageCondition Condition { get; set; } = MessageCondition.Always;
        public List<MessageRecipientKind> Recipients { get; set; } = [];
        /// <summary>Addresses it also goes to, separated by <c>;</c>.</summary>
        public string? Addresses { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public bool Enabled { get; set; } = true;

        public MessageTemplateInput ToInput() => new()
        {
            Name = Name, Condition = Condition, Recipients = Recipients, Addresses = Addresses, Subject = Subject, Body = Body, Enabled = Enabled
        };

        public static MessageForm From(MessageTemplate m) => new()
        {
            Name = m.Name, Condition = m.Condition, Recipients = m.Recipients.Select(r => r.Kind).Order().ToList(),
            Addresses = m.Addresses, Subject = m.Subject, Body = m.Body, Enabled = m.Enabled
        };
    }

    public MessageTemplate? Item { get; private set; }
    public MessageTriggerDefinition Definition { get; private set; } = null!;
    [BindProperty] public MessageForm Form { get; set; } = new();
    public string? SaveError { get; private set; }
    public MessagePreview? Preview { get; private set; }

    /// <summary>The trigger's values as the token picker lists them.</summary>
    public IReadOnlyList<RequestToken> Tokens => Definition.Tokens.Select(t => new RequestToken(t.Name, t.Label)).ToList();

    public async Task<IActionResult> OnGetAsync(Guid? id, MessageTrigger? trigger, CancellationToken ct)
    {
        if (!await LoadAsync(id, trigger, ct)) return RedirectToPage("/Admin/Messages/Index");
        Form = Item is null
            ? new MessageForm { Recipients = [Definition.Recipients[0]], Subject = "[Orbit] ", Body = "Hi {{recipient.name}},\n\n" }
            : MessageForm.From(Item);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, MessageTrigger? trigger, CancellationToken ct)
    {
        if (!await LoadAsync(id, trigger, ct)) return RedirectToPage("/Admin/Messages/Index");
        try
        {
            var saved = id is Guid existing
                ? await messages.UpdateAsync(existing, Form.ToInput(), ct)
                : await messages.CreateAsync(Definition.Trigger, Form.ToInput(), ct);
            Success($"Message \"{saved.Name}\" saved.");
            return RedirectToPage(new { id = saved.Id, trigger = (MessageTrigger?)null });
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(Guid? id, MessageTrigger? trigger, CancellationToken ct)
    {
        if (!await LoadAsync(id, trigger, ct)) return RedirectToPage("/Admin/Messages/Index");
        try { Preview = await messages.PreviewAsync(Definition.Trigger, Form.ToInput(), ct); }
        catch (ValidationException ex) { SaveError = ex.Message; }
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var message = await messages.GetAsync(id, ct);
        await messages.DeleteAsync(id, ct);
        Success($"Message \"{message.Name}\" deleted.");
        return RedirectToPage("/Admin/Messages/Index");
    }

    /// <summary>The message and its trigger's definition; false for a new message with no trigger, or one that isn't a trigger.</summary>
    private async Task<bool> LoadAsync(Guid? id, MessageTrigger? trigger, CancellationToken ct)
    {
        Item = id is Guid existing ? await messages.GetAsync(existing, ct) : null;
        var which = Item?.Trigger ?? trigger;
        if (which is not MessageTrigger t || !MessageCatalogue.ByTrigger.TryGetValue(t, out var definition)) return false;
        Definition = definition;
        return true;
    }
}
