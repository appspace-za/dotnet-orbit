using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Messages;

namespace Orbit.Auth;

/// <summary>
/// Sends email through Mailjet's Send API v3.1 (spec §6.7), when <c>Email:Provider</c> is Mailjet. The API key and secret key go
/// only in the request's Basic <c>Authorization</c> header; <see cref="MailjetRules"/> builds the request and reads the answers.
/// </summary>
public sealed class MailjetEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<MailjetEmailSender> logger)
    : ApiEmailSender(http, logger)
{
    protected override string Provider => MailjetRules.Provider;

    protected override HttpRequestMessage Request(string to, string subject, string html)
    {
        var settings = options.Value;
        var request = new HttpRequestMessage(HttpMethod.Post, MailjetRules.SendPath)
        {
            Content = Json(MailjetRules.Request(settings, to, subject, html), MailjetRules.Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", MailjetRules.Credentials(settings.Mailjet));
        return request;
    }

    protected override string? Refusal(int status, string body) => MailjetRules.Refusal(status, body);

    protected override string? MessageId(string body) => MailjetRules.MessageId(body);
}
