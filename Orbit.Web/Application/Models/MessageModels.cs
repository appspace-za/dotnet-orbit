using Orbit.Data.Entities;

namespace Orbit.Application.Models;

/// <summary>A message as the editor saves it (§6.7). The trigger is given when the message is created and never changes.</summary>
public sealed class MessageTemplateInput
{
    public string? Name { get; set; }
    public MessageCondition Condition { get; set; } = MessageCondition.Always;
    public IReadOnlyList<MessageRecipientKind> Recipients { get; set; } = [];
    /// <summary>Addresses it also goes to, separated by <c>;</c>: <c>someone@example.com</c> or <c>Name &lt;someone@example.com&gt;</c>.</summary>
    public string? Addresses { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>A message filled in with its trigger's sample values: the subject, the plain-text body, and the HTML that is sent.</summary>
public sealed record MessagePreview(string Subject, string Body, string Html);
