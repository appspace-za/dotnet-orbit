using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Messages;

namespace Orbit.Auth;

/// <summary>
/// Sends email through Resend's email API (spec §6.7), when <c>Email:Provider</c> is Resend. The API key goes only in the request's
/// Bearer <c>Authorization</c> header, and the request names Orbit as its User-Agent, which Resend asks for; <see cref="ResendRules"/>
/// builds the request and reads the answers.
/// </summary>
public sealed class ResendEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<ResendEmailSender> logger)
    : ApiEmailSender(http, logger)
{
    protected override string Provider => ResendRules.Provider;

    protected override HttpRequestMessage Request(string to, string subject, string html)
    {
        var settings = options.Value;
        var request = new HttpRequestMessage(HttpMethod.Post, ResendRules.SendPath)
        {
            Content = Json(ResendRules.Request(settings, to, subject, html), OrbitJson.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Resend.ApiKey!.Trim());
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(ResendRules.UserAgent, null));
        return request;
    }

    protected override string? Refusal(int status, string body) => status is >= 200 and < 300 ? null : ResendRules.Describe(status, body);

    protected override string? MessageId(string body) => ResendRules.MessageId(body);
}
