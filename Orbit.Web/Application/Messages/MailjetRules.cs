using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Application.Messages;

/// <summary>An address as Mailjet's Send API takes it: the name is left out when there is none.</summary>
public sealed record MailjetAddress(string Email, string? Name = null);

/// <summary>One email in Mailjet's Send API v3.1: HTML, with no Mailjet template.</summary>
public sealed record MailjetMessage(MailjetAddress From, IReadOnlyList<MailjetAddress> To, string Subject,
    [property: JsonPropertyName("HTMLPart")] string HtmlPart);

/// <summary>The body of Mailjet's <c>POST send</c> (v3.1): Orbit sends one message a request.</summary>
public sealed record MailjetSendRequest(IReadOnlyList<MailjetMessage> Messages);

/// <summary>
/// Sending through Mailjet's Send API v3.1 (spec §6.7): whether the settings will do, the request for one email, whether Mailjet
/// took it, and what a refusal said, in words for the outbox's error. No HTTP - <c>MailjetEmailSender</c> sends what these build.
/// The API key and secret key travel only in the request's Basic <c>Authorization</c> header, never in a body or a message.
/// </summary>
public static class MailjetRules
{
    public const string Provider = "Mailjet";
    /// <summary>Relative to <see cref="BaseUri"/>.</summary>
    public const string SendPath = "send";

    /// <summary>Mailjet's names are PascalCase (<c>Messages</c>, <c>From</c>, <c>HTMLPart</c>), so not <c>OrbitJson.Options</c>, which camel-cases them.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// MSG-021: what is wrong with the settings for sending through Mailjet, each naming its key - empty when they will do. The base
    /// address must be https, except on this machine (a stand-in for Mailjet while testing), so the keys never cross a network in clear.
    /// </summary>
    public static IReadOnlyList<string> ConfigProblems(EmailOptions options)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Mailjet.ApiKey)) problems.Add("Email:Mailjet:ApiKey is not set");
        if (string.IsNullOrWhiteSpace(options.Mailjet.SecretKey)) problems.Add("Email:Mailjet:SecretKey is not set");
        problems.AddRange(EmailApiRules.FromProblems(options));
        if (EmailApiRules.BaseUrlProblem("Email:Mailjet:BaseUrl", options.Mailjet.BaseUrl) is { } url) problems.Add(url);
        return problems;
    }

    /// <summary>The API's base address, ending in a slash so that <see cref="SendPath"/> is added to it rather than replacing its last part.</summary>
    public static Uri BaseUri(MailjetOptions options) => EmailApiRules.BaseUri(options.BaseUrl);

    /// <summary>MSG-021: the Basic credentials, <c>key:secret</c> in base64.</summary>
    public static string Credentials(MailjetOptions options) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ApiKey?.Trim()}:{options.SecretKey?.Trim()}"));

    /// <summary>MSG-022: the request that sends one email from the configured sender to one address.</summary>
    public static MailjetSendRequest Request(EmailOptions options, string to, string subject, string html) =>
        new([new MailjetMessage(
            new MailjetAddress(options.FromAddress!.Trim(), string.IsNullOrWhiteSpace(options.FromName) ? null : options.FromName.Trim()),
            [new MailjetAddress(to.Trim())], subject, html)]);

    /// <summary>
    /// MSG-023: what was wrong when Mailjet didn't take the email, in words; null when it did. A 2xx whose message says
    /// <c>"Status":"error"</c> is a refusal too - Mailjet answers 400 when its only message fails, but that isn't promised.
    /// </summary>
    public static string? Refusal(int status, string? body)
    {
        var failed = Items(EmailApiRules.ParseObject(body), "Messages")
            .Any(m => string.Equals(EmailApiRules.Text(m, "Status"), "error", StringComparison.OrdinalIgnoreCase));
        return status is >= 200 and < 300 && !failed ? null : Describe(status, body);
    }

    /// <summary>
    /// MSG-023: a refusal in words, for the outbox's error and the test email's message. Mailjet's code and message come either at the
    /// top of its answer (bad credentials, malformed JSON) or for each message (a bad address, a From address it hasn't validated);
    /// several of the latter are joined. Otherwise the start of the body.
    /// </summary>
    public static string Describe(int status, string? body)
    {
        var root = EmailApiRules.ParseObject(body);
        var code = EmailApiRules.Text(root, "ErrorCode");
        var message = EmailApiRules.Text(root, "ErrorMessage");
        if (code is null && message is null)
        {
            var errors = Items(root, "Messages").SelectMany(m => Items(m, "Errors")).ToList();
            code = errors.Select(e => EmailApiRules.Text(e, "ErrorCode")).FirstOrDefault(c => c is not null);
            var messages = errors.Select(e => EmailApiRules.Text(e, "ErrorMessage")).OfType<string>().Distinct().ToList();
            message = messages.Count > 0 ? string.Join("; ", messages) : null;
        }
        return EmailApiRules.Describe(Provider, status, code, message, body);
    }

    /// <summary>MSG-023: the id Mailjet gave an email it accepted (its <c>MessageID</c>, or else its <c>MessageUUID</c>), for the log; null when the answer doesn't carry one.</summary>
    public static string? MessageId(string? body)
    {
        var to = Items(EmailApiRules.ParseObject(body), "Messages").SelectMany(m => Items(m, "To")).FirstOrDefault();
        if (to.ValueKind != JsonValueKind.Object) return null;
        if (to.TryGetProperty("MessageID", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number) return id.GetRawText();
            if (EmailApiRules.Text(to, "MessageID") is { } text) return text;
        }
        return EmailApiRules.Text(to, "MessageUUID");
    }

    /// <summary>The objects in an array property; none when there is no such array.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
            : [];
}
