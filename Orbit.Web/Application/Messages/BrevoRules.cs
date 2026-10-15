using System.Text.Json;

namespace Orbit.Application.Messages;

/// <summary>An address as Brevo's API takes it: the name is left out when there is none.</summary>
public sealed record BrevoAddress(string Email, string? Name = null);

/// <summary>The body of Brevo's <c>POST smtp/email</c>: one email, sent now, with no Brevo template.</summary>
public sealed record BrevoSendRequest(BrevoAddress Sender, IReadOnlyList<BrevoAddress> To, string Subject, string HtmlContent);

/// <summary>
/// Sending through Brevo's transactional email API (spec §6.7): whether the settings will do, the request for one email, and what a
/// refusal said, in words for the outbox's error. No HTTP - <c>BrevoEmailSender</c> sends what these build. The API key travels
/// only in the <see cref="ApiKeyHeader"/> header, never in a request body or a message.
/// </summary>
public static class BrevoRules
{
    public const string Provider = "Brevo";
    public const string ApiKeyHeader = "api-key";
    /// <summary>Relative to <see cref="BaseUri"/>.</summary>
    public const string SendPath = "smtp/email";
    public const int FromNameMaxLength = 70;
    /// <summary>The most of a refusal's body that is kept when it isn't Brevo's own JSON (a proxy's error page, say).</summary>
    public const int ErrorBodyMaxLength = EmailApiRules.ErrorBodyMaxLength;

    /// <summary>
    /// MSG-016: what is wrong with the settings for sending through Brevo, each naming its key - empty when they will do. The base
    /// address must be https, except on this machine (a stand-in for Brevo while testing), so the key never crosses a network in clear.
    /// </summary>
    public static IReadOnlyList<string> ConfigProblems(EmailOptions options)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Brevo.ApiKey)) problems.Add("Email:Brevo:ApiKey is not set");
        problems.AddRange(EmailApiRules.FromProblems(options));
        if ((options.FromName?.Trim().Length ?? 0) > FromNameMaxLength)
            problems.Add($"Email:FromName is longer than Brevo's {FromNameMaxLength} characters");
        if (EmailApiRules.BaseUrlProblem("Email:Brevo:BaseUrl", options.Brevo.BaseUrl) is { } url) problems.Add(url);
        return problems;
    }

    /// <summary>The API's base address, ending in a slash so that <see cref="SendPath"/> is added to it rather than replacing its last part.</summary>
    public static Uri BaseUri(BrevoOptions options) => EmailApiRules.BaseUri(options.BaseUrl);

    /// <summary>MSG-017: the request that sends one email from the configured sender to one address.</summary>
    public static BrevoSendRequest Request(EmailOptions options, string to, string subject, string html) =>
        new(new BrevoAddress(options.FromAddress!.Trim(), string.IsNullOrWhiteSpace(options.FromName) ? null : options.FromName.Trim()),
            [new BrevoAddress(to.Trim())], subject, html);

    /// <summary>
    /// MSG-018: a refusal in words, for the outbox's error and the test email's message - Brevo's code and message when the body is
    /// its JSON error, otherwise the start of the body. Every failure is tried again, so none of them says it is final.
    /// </summary>
    public static string Describe(int status, string? body)
    {
        var root = EmailApiRules.ParseObject(body);
        return EmailApiRules.Describe(Provider, status, EmailApiRules.Text(root, "code"), EmailApiRules.Text(root, "message"), body);
    }

    /// <summary>MSG-018: the id Brevo gave an email it accepted, for the log; null when the answer doesn't carry one.</summary>
    public static string? MessageId(string? body)
    {
        if (EmailApiRules.ParseObject(body) is not { } root) return null;
        if (root.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String) return id.GetString();
        if (root.TryGetProperty("messageIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()).FirstOrDefault();
        return null;
    }
}
