using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Messages;

namespace Orbit.Auth;

/// <summary>
/// Sends email through Brevo's transactional email API (spec §6.7), when <c>Email:Provider</c> is Brevo. The API key goes only in the
/// request's <c>api-key</c> header; <see cref="BrevoRules"/> builds the request and reads the answers.
/// </summary>
public sealed class BrevoEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<BrevoEmailSender> logger)
    : ApiEmailSender(http, logger)
{
    protected override string Provider => BrevoRules.Provider;

    protected override HttpRequestMessage Request(string to, string subject, string html)
    {
        var settings = options.Value;
        var request = new HttpRequestMessage(HttpMethod.Post, BrevoRules.SendPath)
        {
            Content = Json(BrevoRules.Request(settings, to, subject, html), OrbitJson.Options)
        };
        request.Headers.Add(BrevoRules.ApiKeyHeader, settings.Brevo.ApiKey);
        return request;
    }

    protected override string? Refusal(int status, string body) => status is >= 200 and < 300 ? null : BrevoRules.Describe(status, body);

    protected override string? MessageId(string body) => BrevoRules.MessageId(body);
}
