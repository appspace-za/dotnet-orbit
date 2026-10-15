using System.Text.Json;
using Orbit.Application;
using Orbit.Application.Messages;

namespace Orbit.Tests.Messages;

/// <summary>Sending through Brevo's transactional email API (spec §6.7).</summary>
public class BrevoRulesTests
{
    private const string Key = "xkeysib-0123456789abcdef";

    private static EmailOptions Settings(Action<EmailOptions>? change = null)
    {
        var options = new EmailOptions
        {
            Provider = EmailProvider.Brevo,
            FromAddress = "orbit@example.com",
            FromName = "Orbit",
            Brevo = new BrevoOptions { ApiKey = Key }
        };
        change?.Invoke(options);
        return options;
    }

    /// <summary>MSG-016: settings that will send pass; each that won't is named by its key.</summary>
    [Fact]
    public void Config_problems_name_what_is_missing_or_wrong()
    {
        Assert.Empty(BrevoRules.ConfigProblems(Settings()));
        Assert.Empty(BrevoRules.ConfigProblems(Settings(o => o.FromName = "")));
        Assert.Empty(BrevoRules.ConfigProblems(Settings(o => o.Brevo.BaseUrl = "http://localhost:5199/v3")));

        Assert.Contains("Email:Brevo:ApiKey", Assert.Single(BrevoRules.ConfigProblems(Settings(o => o.Brevo.ApiKey = " "))));
        Assert.Contains("Email:FromAddress is not set", Assert.Single(BrevoRules.ConfigProblems(Settings(o => o.FromAddress = null))));
        Assert.Contains("not an email address", Assert.Single(BrevoRules.ConfigProblems(Settings(o => o.FromAddress = "orbit"))));
        Assert.Contains("not an email address", Assert.Single(BrevoRules.ConfigProblems(Settings(o => o.FromAddress = "Orbit <orbit@example.com>"))));
        Assert.Contains("Email:FromName", Assert.Single(BrevoRules.ConfigProblems(Settings(o => o.FromName = new string('x', 71)))));
        Assert.Contains("Email:Brevo:BaseUrl", Assert.Single(BrevoRules.ConfigProblems(Settings(o => o.Brevo.BaseUrl = "http://api.brevo.com/v3/"))));
        Assert.Contains("Email:Brevo:BaseUrl", Assert.Single(BrevoRules.ConfigProblems(Settings(o => o.Brevo.BaseUrl = "api.brevo.com"))));
        Assert.Equal(2, BrevoRules.ConfigProblems(Settings(o => { o.Brevo.ApiKey = null; o.FromAddress = null; })).Count);

        Assert.Equal("https://api.brevo.com/v3/smtp/email", new Uri(BrevoRules.BaseUri(new BrevoOptions { BaseUrl = "https://api.brevo.com/v3" }), BrevoRules.SendPath).ToString());
        Assert.Equal("https://api.brevo.com/v3/smtp/email", new Uri(BrevoRules.BaseUri(new BrevoOptions()), BrevoRules.SendPath).ToString());
    }

    /// <summary>MSG-017: one email is Brevo's sender, one recipient, subject and HTML, in its field names, with no empty fields and no key.</summary>
    [Fact]
    public void Request_is_the_sender_recipient_subject_and_html()
    {
        var json = JsonSerializer.Serialize(BrevoRules.Request(Settings(o => o.FromAddress = " orbit@example.com "), " ann@example.com ",
            "Task T-26-00012 assigned", "<p>Hi</p>\n"), OrbitJson.Options);
        Assert.DoesNotContain(Key, json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["sender", "to", "subject", "htmlContent"], root.EnumerateObject().Select(p => p.Name));
        var sender = root.GetProperty("sender");
        Assert.Equal(["email", "name"], sender.EnumerateObject().Select(p => p.Name));
        Assert.Equal("orbit@example.com", sender.GetProperty("email").GetString());
        Assert.Equal("Orbit", sender.GetProperty("name").GetString());
        var to = Assert.Single(root.GetProperty("to").EnumerateArray());
        Assert.Equal(["email"], to.EnumerateObject().Select(p => p.Name));
        Assert.Equal("ann@example.com", to.GetProperty("email").GetString());
        Assert.Equal("Task T-26-00012 assigned", root.GetProperty("subject").GetString());
        Assert.Equal("<p>Hi</p>\n", root.GetProperty("htmlContent").GetString());

        var nameless = JsonSerializer.Serialize(BrevoRules.Request(Settings(o => o.FromName = " "), "ann@example.com", "S", "<p>B</p>"), OrbitJson.Options);
        using var namelessDocument = JsonDocument.Parse(nameless);
        Assert.Equal(["email"], namelessDocument.RootElement.GetProperty("sender").EnumerateObject().Select(p => p.Name));
    }

    /// <summary>MSG-018: a refusal reads as Brevo's code and message, or the start of whatever came back; an accepted email's id is read for the log.</summary>
    [Fact]
    public void Refusals_are_described_and_message_ids_read()
    {
        Assert.Equal("Brevo refused the email (401 unauthorized): Key not found",
            BrevoRules.Describe(401, """{"code":"unauthorized","message":"Key not found"}"""));
        Assert.Equal("Brevo refused the email (400 invalid_parameter): valid sender email required",
            BrevoRules.Describe(400, """{"code":"invalid_parameter","message":" valid sender email required "}"""));
        Assert.Equal("Brevo refused the email (400 missing_parameter).", BrevoRules.Describe(400, """{"code":"missing_parameter"}"""));
        Assert.Equal("Brevo is limiting how fast Orbit sends (429).", BrevoRules.Describe(429, ""));
        Assert.Equal("Brevo had a problem with the email (502): <html>Bad gateway</html>", BrevoRules.Describe(502, " <html>Bad gateway</html> "));
        Assert.Equal("Brevo refused the email (403).", BrevoRules.Describe(403, null));
        Assert.Equal("Brevo refused the email (400): [1,2]", BrevoRules.Describe(400, "[1,2]"));

        var clipped = BrevoRules.Describe(500, new string('x', 1000));
        Assert.EndsWith("…", clipped);
        Assert.True(clipped.Length < BrevoRules.ErrorBodyMaxLength + 60);

        Assert.Equal("<202610151200.123@smtp-relay.mailin.fr>", BrevoRules.MessageId("""{"messageId":"<202610151200.123@smtp-relay.mailin.fr>"}"""));
        Assert.Equal("<a@b>", BrevoRules.MessageId("""{"messageIds":["<a@b>","<c@d>"]}"""));
        Assert.Null(BrevoRules.MessageId(""));
        Assert.Null(BrevoRules.MessageId("not json"));
        Assert.Null(BrevoRules.MessageId("""{"other":1}"""));
    }
}
