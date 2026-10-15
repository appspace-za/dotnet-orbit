using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Messages;

namespace Orbit.Auth;

/// <summary>
/// Sends email through Brevo's transactional email API (spec §6.7), when <c>Email:Provider</c> is Brevo: the outbox's emails, which
/// <c>EmailDispatcher</c> retries when this throws, and Identity's account emails, which are sent once. A failure throws with a
/// message an admin can act on - it becomes the outbox's error. The API key goes only in the request's header.
/// </summary>
public sealed class BrevoEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<BrevoEmailSender> logger) : IEmailSender
{
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, BrevoRules.SendPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(BrevoRules.Request(settings, email, subject, htmlMessage), OrbitJson.Options),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add(BrevoRules.ApiKeyHeader, settings.Brevo.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"Brevo did not answer within {http.Timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach Brevo: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(BrevoRules.Describe((int)response.StatusCode, body));
            logger.LogInformation("Email to {To} ({Subject}) accepted by Brevo as {MessageId}.", email, subject, BrevoRules.MessageId(body) ?? "(no id)");
        }
    }
}
