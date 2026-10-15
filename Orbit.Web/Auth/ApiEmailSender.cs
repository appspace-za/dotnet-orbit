using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity.UI.Services;

namespace Orbit.Auth;

/// <summary>
/// An email provider's HTTP API as Identity's sender (spec §6.7): the outbox's emails, which <c>EmailDispatcher</c> retries when
/// this throws, and Identity's account emails, which are sent once. One request per email; a failure throws with a message an admin
/// can act on - it becomes the outbox's error - and an accepted email's id is logged. Each provider builds its request, credentials
/// included, and reads its own answers.
/// </summary>
public abstract class ApiEmailSender(HttpClient http, ILogger logger) : IEmailSender
{
    /// <summary>The provider's name, as the errors and the log give it.</summary>
    protected abstract string Provider { get; }

    /// <summary>The request that sends one email, relative to the client's base address and carrying the credentials.</summary>
    protected abstract HttpRequestMessage Request(string to, string subject, string html);

    /// <summary>What was wrong, in words, when the provider didn't take the email; null when it did.</summary>
    protected abstract string? Refusal(int status, string body);

    /// <summary>The id the provider gave an email it accepted, for the log; null when its answer has none.</summary>
    protected abstract string? MessageId(string body);

    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        using var request = Request(email, subject, htmlMessage);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException($"{Provider} did not answer within {http.Timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach {Provider}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (Refusal((int)response.StatusCode, body) is { } refusal) throw new InvalidOperationException(refusal);
            logger.LogInformation("Email to {To} ({Subject}) accepted by {Provider} as {MessageId}.", email, subject, Provider, MessageId(body) ?? "(no id)");
        }
    }

    /// <summary>A request body of <paramref name="value"/> as JSON.</summary>
    protected static StringContent Json<T>(T value, JsonSerializerOptions options) =>
        new(JsonSerializer.Serialize(value, options), Encoding.UTF8, "application/json");
}
