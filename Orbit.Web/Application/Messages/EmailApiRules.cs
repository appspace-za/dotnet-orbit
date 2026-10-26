using System.Net.Mail;
using System.Text.Json;

namespace Orbit.Application.Messages;

/// <summary>
/// What every email provider's API has in common (spec §6.7): the From address it is sent from, a base address that keeps the
/// credentials off the network in clear, and a refusal in words for the outbox's error. <see cref="BrevoRules"/>,
/// <see cref="MailjetRules"/> and <see cref="ResendRules"/> add what is each provider's own - its keys, its request and the shape of
/// its errors.
/// </summary>
public static class EmailApiRules
{
    /// <summary>The most of a refusal's body that is kept when it isn't the provider's own JSON (a proxy's error page, say).</summary>
    public const int ErrorBodyMaxLength = 300;

    /// <summary>What is wrong with <c>Email:FromAddress</c>: it must be set, and be a bare address rather than <c>Name &lt;address&gt;</c>.</summary>
    public static IEnumerable<string> FromProblems(EmailOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FromAddress)) yield return "Email:FromAddress is not set";
        else if (!IsAddress(options.FromAddress)) yield return $"Email:FromAddress \"{options.FromAddress}\" is not an email address";
    }

    /// <summary>
    /// What is wrong with a provider's base address, named by its <paramref name="setting"/>; null when it will do. It must be https,
    /// except on this machine (a stand-in for the provider while testing), so the credentials never cross a network in clear.
    /// </summary>
    public static string? BaseUrlProblem(string setting, string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? null
            : $"{setting} \"{url}\" is not an https address";

    /// <summary>An API's base address, ending in a slash so that a relative path is added to it rather than replacing its last part.</summary>
    public static Uri BaseUri(string url) => new(url.Trim().TrimEnd('/') + "/");

    /// <summary>
    /// A refusal in words, for the outbox's error and the test email's message: the provider's code and message when its body said
    /// them, otherwise the start of the body. Every failure is tried again, so none of them says it is final.
    /// </summary>
    public static string Describe(string provider, int status, string? code, string? message, string? body)
    {
        var detail = code is null && message is null ? MessageRules.Clip(body?.Trim(), ErrorBodyMaxLength) : message;
        var what = status switch
        {
            429 => $"{provider} is limiting how fast Orbit sends",
            >= 500 => $"{provider} had a problem with the email",
            _ => $"{provider} refused the email"
        };
        var head = code is null ? $"{what} ({status})" : $"{what} ({status} {code})";
        return string.IsNullOrWhiteSpace(detail) ? head + "." : $"{head}: {detail}";
    }

    /// <summary>The body's JSON object, or null when it is empty, isn't JSON or isn't an object.</summary>
    public static JsonElement? ParseObject(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string property of a JSON object, trimmed; null when there is no object, or the property is missing, not a string or blank.</summary>
    public static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool IsAddress(string value) =>
        MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim();
}
